using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Agents.Effects;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

public sealed class TemperatureSystem : ISimulationSystem
{
    // One slow tick is four seconds. Body temperature traverses the signed
    // scale inertially; active relief is deliberately 2.5x faster.
    internal const float BodyDriftPerSlowTick = 0.04f;
    internal const float ActiveReliefPerSlowTick = 0.10f;

    public string Name => nameof(TemperatureSystem);

    public TickLayer Layer => TickLayer.Slow;

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            // NeedsDecay may have killed this body earlier in the same Slow
            // layer. Body.Mean below is recovery/damage bookkeeping, not revival.
            if (npc.Health <= 0f)
            {
                continue;
            }

            var prevThermal = npc.Needs.ThermalDiscomfort;
            var prevBody = npc.Needs.ThermalComfort;

            // Spec 29C.4: warmth is no longer a pure good — graded pressure,
            // clothes shift the effective temperature both ways.
            // Spec 35.3/35.4: the house protects from cold; shade and the
            // river cool; Indoor/Water block UV, shade cuts it to 20 %.
            var isIndoor = world.Tiles.Items.TryGetValue(npc.Tile, out var npcTile) &&
                npcTile.Flags.HasFlag(TileFlags.Indoor);
            var isInWater = npcTile is not null && npcTile.Flags.HasFlag(TileFlags.Water);
            var isShaded = IsShaded(world, npc.Tile);
            var indoorBonus = isIndoor ? SimBalance.IndoorWarmthBonus : 0f;
            // Shade is applied below as a capped heat-SHIELD, not here — a flat
            // cool bonus chilled girls resting in shade on mild days (18°→11°)
            // into cold damage. Water still cools unconditionally (wet + current).
            var coolBonus = isInWater ? -SimBalance.WaterCoolBonus : 0f;
            // Spec 29C.10: a lit campfire warms the tiles around it (the
            // colder it is, the more worth huddling by the fire), but only
            // chases away COLD — it never overheats a warm body.
            var fireWarmth = NearbyFireWarmth(world, npc.Tile, out var onFire);
            // Spec 42 / §120.4: the campfire is a REAL heat source. Outdoor
            // fires retain their two-ring profile; a fire under a completed
            // roof uses the room-confined 100/75/50 % profile. Both are
            // clamped so they only chase away cold, never overheat.
            var baseTemp = world.Environment.GlobalTemperature + npc.EquippedWarmth * 10f +
                indoorBonus + coolBonus;
            var fireRelief = System.Math.Min(fireWarmth, System.Math.Max(0f, SimBalance.HotBandTemp - baseTemp));
            baseTemp += fireRelief;
            // Tier B: shade as a heat-SHIELD — cools only the heat ABOVE the
            // comfy band (down toward ~22°), never chills a cool body. On a 35°
            // day, shade → 28° (the user's example); on an 18° day, no effect.
            if (isShaded)
            {
                baseTemp -= System.Math.Min(-Spec49.ShadeCooling, System.Math.Max(0f, baseTemp - SimBalance.HotBandTemp));
            }

            var environmentTarget = SignedFromTemperature(baseTemp);
            var bodyTarget = environmentTarget;
            var bodyRate = BodyDriftPerSlowTick;
            var recoverySource = "environment";
            if (prevBody < 0f && fireWarmth > 0f)
            {
                bodyTarget = 0f;
                bodyRate = ActiveReliefPerSlowTick;
                recoverySource = "fire";
            }
            else if (prevBody > 0f && isInWater)
            {
                bodyTarget = 0f;
                bodyRate = ActiveReliefPerSlowTick;
                recoverySource = "water";
            }

            var signed = MoveBodyTowards(prevBody, bodyTarget, bodyRate);
            npc.Needs.ThermalComfort = signed;
            var previousMagnitude = System.Math.Abs(prevBody);
            var targetMagnitude = System.Math.Abs(bodyTarget);
            if (previousMagnitude > 0.001f || targetMagnitude > 0.001f)
            {
                var thermalRelief = targetMagnitude < previousMagnitude ||
                    System.Math.Abs(signed) < previousMagnitude;
                npc.EffectImpacts.Record(
                    NeedKind.Temperature,
                    recoverySource == "fire" ? EffectKind.Cozy : EffectKind.AmbientTemperature,
                    thermalRelief
                        ? EffectImpactDirection.Positive
                        : EffectImpactDirection.Negative,
                    EffectImpactCadence.Slow);
            }
            var bodyTemp = TemperatureFromSigned(signed);

            // Spec 42: realistic cold — 10°C in underwear (warmth ~0.02) is
            // genuinely cold; pressure bites below 14°C (a merely-cool girl at
            // ~15° doesn't accumulate — no wardrobe-circling), naked at 10°
            // racks up 0.11+/slow tick unless she's warming by the fire.
            float pressure;
            if (bodyTemp < SimBalance.ColdBandTemp)
            {
                pressure = System.Math.Min(SimBalance.ThermalPressureCap,
                    (SimBalance.ColdBandTemp - bodyTemp) * SimBalance.ColdPressureSlope); // cold
            }
            else if (bodyTemp > SimBalance.HotBandTemp)
            {
                pressure = System.Math.Min(SimBalance.ThermalPressureCap,
                    (bodyTemp - SimBalance.HotBandTemp) * SimBalance.HeatPressureSlope); // overheating
            }
            else
            {
                // Comfortable band. A body actively warmed by a strong heat
                // source (fire ring or indoors) sheds the accumulated cold much
                // faster — standing by the fire thaws you in a few ticks instead
                // of slowly bleeding the night's chill off at the ambient rate.
                var activelyWarmed = fireWarmth > 0f || isIndoor;
                pressure = -(activelyWarmed
                    ? SimBalance.FireThawRecovery
                    : SimBalance.ThermalComfyRecovery);
            }

            // Tier B: a sleeping body accrues cold/heat discomfort more slowly
            // (only the rising side is slowed; recovery in the comfy band stays
            // full). Lets her sleep through a mild night without spiralling.
            // §60: a comatose body counts as sleeping here too.
            var sleeping = npc.Execution.CurrentInteraction == InteractionType.Sleep ||
                npc.Mind.ComaCause != ComaCause.None;
            if (sleeping && pressure > 0f)
            {
                pressure *= Spec49.ThermalSleepFactor;
            }

            // §76: Hardiness — heat and cold press on her less. Only the RISING
            // side is scaled, exactly like the sleep factor above: the attribute
            // is a tolerance for the weather, not a faster thaw at the fire
            // (that is what the fire is for).
            if (pressure > 0f)
            {
                pressure *= AttributeMath.ThermalPressureMult(npc);
            }

            npc.Needs.ThermalDiscomfort = MathUtil.Clamp01(npc.Needs.ThermalDiscomfort + pressure);

            var magnitude = System.Math.Abs(signed);

            // Spec 29C.10: "the fire burns you if you stand in it" is DEFERRED
            // to the campfire-as-obstacle pass — any HP/comfort hit here
            // reshuffles the dog-fragile colony (NPCs constantly path across
            // the central fire tile) and wipes seeds. onFire is computed and
            // traced so the mechanic is ready to wire once nobody stands on
            // the flames by construction.
            if (onFire)
            {
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "FireBurn", "On the fire tile (no HP hit yet)");

                }
            }

            if (magnitude >= SimBalance.ThermalDamageGate)
            {
                // §45 r5: 0.02 -> 0.012. A rainy 6° night (rain also douses
                // the fire 4x) killed a near-naked girl from FULL health in
                // one night (~37 slow ticks x 0.02 = 0.74) — both 25-day
                // wipes (777, 42) started as day-4/5 hypothermia deaths.
                // At 0.012 a single bad night hurts (~0.44) but leaves dawn
                // to dress/warm up; two exposed nights still kill.
                // Tier B: a sleeping body takes the cold/heat HP hit at the same
                // slowed factor — this is what lets her sleep THROUGH a cold
                // night (re-arm) without it being a death sentence.
                var thermalHpHit = SimBalance.ThermalHpHit * (sleeping ? Spec49.ThermalSleepFactor : 1f);
                foreach (var part in AllTemperatureParts)
                {
                    // §82 r2: жара и холод не доламывают ВИТАЛЬНУЮ часть — тот
                    // же порог, что у солнца, и по той же причине.
                    //
                    // Здесь это опаснее, чем в ожоге: ожог бьёт одну случайную
                    // часть, а перегрев — ВСЕ СЕМЬ каждый медленный тик. Для
                    // забронированного целиком человека это смертный приговор:
                    // броня греет, снять он её не хочет, и голова тает вместе с
                    // остальным. Именно это, а не солнце, убивало чужака —
                    // девушки полураздеты, сидят в комфортной полосе и не
                    // получают вообще ничего.
                    //
                    // Порог, а не запрет: замёрзнуть до комы по-прежнему можно
                    // (§60 при здоровье 0.15), конечности по-прежнему до нуля.
                    // §105 r4: витальные зоны — по общему списку (голова, грудь,
                    // ТАЗ), а не по паре, вписанной руками. Без этого жара
                    // доламывала бы таз в ноль и убивала мимо окна §105.
                    var thermalFloor = BodyState.IsVital(part)
                        ? SimBalance.ThermalVitalFloor
                        : 0f;
                    npc.Body.Parts[part] = System.Math.Max(
                        thermalFloor, npc.Body.Parts[part] - thermalHpHit);
                }

                npc.Health = npc.Body.Mean();
                // §105: через общую развилку. (Порог ThermalVitalFloor выше
                // нуля, так что температура сама витальную зону не доламывает —
                // но ответ на «умерла или ещё умирает» в проекте один.)
                MortalityHelpers.ResolveTrauma(world, npc, thermalHpHit,
                    signed > 0f ? "heatstroke" : "hypothermia");

                DamageReactionSystemHelpers.GrantAdrenaline(world, npc, thermalHpHit, signed > 0f ? "Heatstroke" : "Hypothermia");

                Trace.Emit(world, npc.Id, signed > 0f ? "Heatstroke" : "Hypothermia",
                    $"ThermalComfort={signed:+0.00;-0.00} Health={npc.Health:F2}");
            }

            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "TemperatureUpdate",
                    $"Thermal={prevThermal:F3}->{npc.Needs.ThermalDiscomfort:F3} " +
                    $"EnvironmentTarget={environmentTarget:+0.00;-0.00} " +
                    $"Body={prevBody:+0.00;-0.00}->{signed:+0.00;-0.00} " +
                    $"Rate={bodyRate:F2} Source={recoverySource} " +
                    $"EffectiveTemp={baseTemp:F1} BodyTemp={bodyTemp:F1} " +
                    $"(Global={world.Environment.GlobalTemperature:F1} " +
                    $"Warmth={npc.EquippedWarmth:F2} Fire={fireWarmth:F1}) Pressure={pressure:+0.00;-0.00}");
            }

            // Spec 35.4: sun exposure and sunburn on uncovered parts.
            // §35.4 r2 (#167): солнце перекрывает КРЫША, а не флаг Indoor. Indoor
            // у нас значит «санктуарий» (§72.12) и стоит на стартовом дворе
            // колонии и на стоянке чужака — там нет никакого перекрытия, и
            // девушка, спящая на кровати под открытым небом, ловила ровный ноль
            // ультрафиолета. Тепловой бонус дома остаётся на Indoor: это про
            // стены и очаг, а не про тень.
            var effectiveUv = EffectiveUv(world, npc.Tile);
            var uncovered = CollectUncoveredParts(world, npc);
            if (effectiveUv > 0.5f && uncovered.Count > 0)
            {
                // Spec 40.7: bare skin under the sun slowly tans (weathered
                // survivor). effectiveUv already carries the shade penalty
                // (isShaded -> x0.2), so you tan LESS in shade. Rate tuned for
                // ~10 SUNNY GAME DAYS of open sun to a full tan — a per-DAY
                // pacing, so when DayLengthTicks moved 2400 → 24000 (10× more
                // sun ticks per day) TanRate/SunburnRate/SunExposureRate were
                // all cut ×10 to keep it.
                npc.Needs.TanLevel = MathUtil.Clamp01(
                    npc.Needs.TanLevel + (effectiveUv - 0.5f) * SimBalance.TanRate * uncovered.Count);
                // Spec 40.7: acute redness rises faster than the tan settles —
                // bare skin goes red first, then browns as it heals below.
                npc.Needs.Sunburn = MathUtil.Clamp01(
                    npc.Needs.Sunburn + (effectiveUv - 0.5f) * SimBalance.SunburnRate * uncovered.Count);
                // §82: счётчик ожогов тоже зависит от того, СКОЛЬКО кожи
                // открыто. Загар и краснота двумя строками выше умножаются на
                // число открытых частей, а он — не умножался, и это была не
                // мелочь: человек в полной броне набирал ожоги ровно с той же
                // скоростью, что голый, только все они летели в единственную
                // открытую часть. У чужака выходил 31 ожог головы за день
                // против 4-6 у полураздетых девушек.
                //
                // Доля, а не количество: голый (все части открыты) даёт
                // множитель 1.0 и ведёт себя ровно как раньше, а закрытый —
                // пропорционально меньше.
                npc.SunExposure += (effectiveUv - 0.5f) * SimBalance.SunExposureRate *
                    ((float)uncovered.Count / npc.Body.Parts.Count);
                if (npc.SunExposure > 0.5f)
                {
                    npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort - 0.02f);
                    npc.EffectImpacts.Record(
                        NeedKind.Comfort,
                        EffectKind.Sunstroke,
                        EffectImpactDirection.Negative,
                        EffectImpactCadence.Slow);
                }

                if (npc.SunExposure >= 1f)
                {
                    var pick = (int)(MathUtil.Hash01(world.Seed, world.Tick, npc.Id.Value, 2203) * uncovered.Count);
                    pick = System.Math.Min(pick, uncovered.Count - 1);
                    var burntPart = uncovered[pick];
                    // §82: солнце не убивает. Раньше ожог грыз часть до нуля, и
                    // для ВИТАЛЬНОЙ это была смерть — причём смерть без раны,
                    // которую нечем лечить и не на что посмотреть.
                    //
                    // Наружу это вылезло на чужаке: его забронировали целиком, у
                    // него осталась открытой ровно ОДНА часть — голова, которую
                    // в игре не закрывает ни одна вещь, — и все удары солнца
                    // пришли в неё. 29 ожогов головы за день против 4-6 у
                    // полураздетых девушек, смерть на 0.3-й день. Броня его и
                    // убивала.
                    //
                    // Порог, а не запрет: солнечный удар обязан быть страшным —
                    // он доводит до беспамятства (§60 кома при здоровье 0.15) и
                    // калечит конечности до нуля по-прежнему. Он просто не
                    // отрывает голову.
                    // §105 r4: то же и для ожога — см. BodyState.VitalParts.
                    var burnFloor = BodyState.IsVital(burntPart)
                        ? SimBalance.SunburnVitalFloor
                        : 0f;
                    npc.Body.Parts[burntPart] = System.Math.Max(
                        burnFloor,
                        npc.Body.Parts[burntPart] - SimBalance.SunburnBurnDamage);
                    npc.Health = npc.Body.Mean();
                    npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort - 0.15f);
                    npc.EffectImpacts.Record(
                        NeedKind.Comfort,
                        EffectKind.Sunstroke,
                        EffectImpactDirection.Negative,
                        EffectImpactCadence.Slow);
                    npc.SunExposure = 0.5f;
                    // §105: через общую развилку (SunburnVitalFloor так же не
                    // даёт солнцу оторвать голову — см. комментарий выше).
                    MortalityHelpers.ResolveTrauma(
                        world, npc, SimBalance.SunburnBurnDamage, "sunstroke");

                    DamageReactionSystemHelpers.GrantAdrenaline(world, npc, SimBalance.SunburnBurnDamage, "Sunburn");

                    Trace.Emit(world, npc.Id, "Sunburn",
                        $"{burntPart} burnt (UV={effectiveUv:F2}) Part={npc.Body.Parts[burntPart]:F2}");
                }
            }
            else
            {
                npc.SunExposure = System.Math.Max(0f, npc.SunExposure - 0.05f);
            }

            // Spec 40.7: out of the sun (or fully covered), the acute burn heals
            // and a fraction of it settles into permanent tan — red browns down.
            if (npc.Needs.Sunburn > 0f && (effectiveUv <= 0.5f || uncovered.Count == 0))
            {
                var heal = System.Math.Min(npc.Needs.Sunburn, 0.0025f);
                npc.Needs.Sunburn -= heal;
                npc.Needs.TanLevel = MathUtil.Clamp01(npc.Needs.TanLevel + heal * 0.4f);
            }
        }
    }

    private static readonly BodyPart[] AllTemperatureParts =
    {
        BodyPart.Head, BodyPart.Torso, BodyPart.Pelvis,
        BodyPart.ArmL, BodyPart.ArmR, BodyPart.LegL, BodyPart.LegR
    };

    internal static float MoveBodyTowards(float body, float target, float rate)
    {
        body = System.Math.Max(-1f, System.Math.Min(1f, body));
        target = System.Math.Max(-1f, System.Math.Min(1f, target));
        rate = System.Math.Max(0f, rate);
        if (body < target)
        {
            return System.Math.Min(target, body + rate);
        }

        return body > target ? System.Math.Max(target, body - rate) : body;
    }

    internal static float SignedFromTemperature(float temperature)
    {
        if (temperature < 16f)
        {
            return System.Math.Max(-1f, (temperature - 16f) / 12f);
        }

        return temperature > 22f
            ? System.Math.Min(1f, (temperature - 22f) / 12f)
            : 0f;
    }

    internal static float TemperatureFromSigned(float signed) =>
        signed < 0f ? 16f + signed * 12f :
        signed > 0f ? 22f + signed * 12f : 19f;

    internal const float IndoorFireOwnHexFactor = 1f;
    internal const float IndoorFireAdjacentFactor = 0.75f;
    internal const float IndoorFireOuterFactor = 0.50f;
    private const int FireSearchRadius = 2;

    // Spec 29C.10 / §120.4: warmth radiated by nearby LIT campfires. Search is
    // bounded to the nineteen tiles inside radius two through ObjectsByTile;
    // it never scans every object on the island. Max aggregation is order-free,
    // so no sorting or per-call allocation is required.
    //
    // A fire whose own tile is Indoor is room-confined: 100 % on its own hex,
    // 75 % on the adjacent ring and 50 % on the outer ring. Every tile in the
    // shortest chain must be Indoor, so heat cannot cross the street or jump
    // from one roofed island to another over an outdoor gap. Outdoor fires keep
    // their legacy +18/+11 profile.
    //
    // onFire still flags the same-tile stand for the deferred FireBurn
    // mechanic — flames themselves are unreachable by construction.
    internal static float NearbyFireWarmth(WorldState world, TileCoord tile, out bool onFire)
    {
        onFire = false;
        var warmth = 0f;
        for (var dq = -FireSearchRadius; dq <= FireSearchRadius; dq++)
        {
            var minDr = System.Math.Max(-FireSearchRadius, -dq - FireSearchRadius);
            var maxDr = System.Math.Min(FireSearchRadius, -dq + FireSearchRadius);
            for (var dr = minDr; dr <= maxDr; dr++)
            {
                var sourceTile = new TileCoord(tile.Q + dq, tile.R + dr);
                if (!world.Caches.ObjectsByTile.TryGetValue(sourceTile, out var objects)) continue;
                foreach (var objectId in objects)
                {
                    if (!world.Entities.Objects.TryGetValue(objectId, out var obj) ||
                        obj.ResourceAmount <= 0f ||
                        !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                        !definition.HasTag("Campfire"))
                    {
                        continue;
                    }

                    var distance = HexSpatialMath.HexDistance(tile, obj.Tile);
                    if (distance == 0) onFire = true;

                    var candidate = ShelterMath.IsIndoor(world, obj.Tile)
                        ? IndoorFireWarmth(world, obj.Tile, tile, distance)
                        : OutdoorFireWarmth(distance);
                    warmth = System.Math.Max(warmth, candidate);
                }
            }
        }

        return warmth;
    }

    private static float OutdoorFireWarmth(int distance) => distance switch
    {
        <= 1 => SimBalance.FireWarmthRange1,
        2 => SimBalance.FireWarmthRange2,
        _ => 0f
    };

    private static float IndoorFireWarmth(
        WorldState world, TileCoord source, TileCoord target, int distance)
    {
        if (distance > FireSearchRadius || !ShelterMath.IsIndoor(world, target)) return 0f;
        if (distance == 2 && !HasIndoorBridge(world, source, target)) return 0f;

        var factor = distance switch
        {
            0 => IndoorFireOwnHexFactor,
            1 => IndoorFireAdjacentFactor,
            2 => IndoorFireOuterFactor,
            _ => 0f
        };
        return SimBalance.FireWarmthRange1 * factor;
    }

    private static bool HasIndoorBridge(WorldState world, TileCoord source, TileCoord target)
    {
        foreach (var direction in HexDirection.All)
        {
            var bridge = new TileCoord(source.Q + direction.DQ, source.R + direction.DR);
            if (HexSpatialMath.HexDistance(bridge, target) == 1 &&
                ShelterMath.IsIndoor(world, bridge))
            {
                return true;
            }
        }

        return false;
    }

    // Spec 35.4: within 1 tile of a Shade-tagged object (big tree / palm).
    internal static bool IsShaded(WorldState world, TileCoord tile)
    {
        // Spec 43: real cast shadows — the map is rebuilt from the sun path
        // every medium tick (terrain silhouettes + canopy + hut walls), so
        // shade is directional now: long at dawn/dusk, tight at noon.
        return world.ShadedTiles.Contains(tile);
    }

    /// <summary>
    /// §35.4 r2 (#167): сколько солнца достаёт до кожи на этом тайле. Одна
    /// формула на всех — тик загара и панель персонажа читают её же. Раньше это
    /// были две копии выражения в разных файлах, и они уже разъезжались: сначала
    /// по флагу (Indoor против крыши), а разъехаться могли и по порядку
    /// умножений, что в этом мире тоже поведение.
    /// </summary>
    internal static float EffectiveUv(WorldState world, TileCoord tile)
    {
        world.Tiles.Items.TryGetValue(tile, out var state);
        // Крыша, а не санктуарий: Indoor стоит и на дворе колонии, и на стоянке
        // чужака — там открытое небо.
        var roofed = state is not null && state.Flags.HasFlag(TileFlags.Roofed);
        var water = state is not null && state.Flags.HasFlag(TileFlags.Water);
        return roofed || water
            ? 0f
            : world.Environment.UvIndex * (IsShaded(world, tile) ? 0.2f : 1f);
    }

    private static readonly System.Collections.Generic.List<BodyPart> _uncoveredScratch = new();

    private static System.Collections.Generic.List<BodyPart> CollectUncoveredParts(WorldState world, NPCState npc)
    {
        _uncoveredScratch.Clear();
        foreach (var part in npc.Body.Parts.Keys)
        {
            if (!EquipmentMath.IsPartCovered(world, npc, part))
            {
                _uncoveredScratch.Add(part);
            }
        }

        return _uncoveredScratch;
    }
}

}
