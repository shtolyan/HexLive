using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

public sealed class TemperatureSystem : ISimulationSystem
{
    public string Name => nameof(TemperatureSystem);

    public TickLayer Layer => TickLayer.Slow;

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            var prevThermal = npc.Needs.ThermalDiscomfort;

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
            // Spec 42 (WarmUp era): the campfire is a REAL heat source now —
            // +8° at range 1, +4° at range 2, clamped so it only chases away
            // cold, never overheats. The girls start near-naked and the
            // wardrobe is scarce; the designed loop is light the fire, huddle
            // by it, and let rain douse it (iter-31's display-only caution is
            // retired together with the knife-edge balance).
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
            // Spec 42: realistic cold — 10°C in underwear (warmth ~0.02) is
            // genuinely cold; pressure bites below 14°C (a merely-cool girl at
            // ~15° doesn't accumulate — no wardrobe-circling), naked at 10°
            // racks up 0.11+/slow tick unless she's warming by the fire.
            float pressure;
            if (baseTemp < SimBalance.ColdBandTemp)
            {
                pressure = System.Math.Min(SimBalance.ThermalPressureCap,
                    (SimBalance.ColdBandTemp - baseTemp) * SimBalance.ColdPressureSlope); // cold
            }
            else if (baseTemp > SimBalance.HotBandTemp)
            {
                pressure = System.Math.Min(SimBalance.ThermalPressureCap,
                    (baseTemp - SimBalance.HotBandTemp) * SimBalance.HeatPressureSlope); // overheating
            }
            else
            {
                pressure = -SimBalance.ThermalComfyRecovery; // comfortable band
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

            npc.Needs.ThermalDiscomfort = MathUtil.Clamp01(npc.Needs.ThermalDiscomfort + pressure);

            // Signed comfort for the UI — fire already folded into baseTemp.
            var effectiveTemp = baseTemp;

            // Spec 42: signed comfort for the UI — 0 in the ideal [16,22]
            // band (matches the decision pressure above, so the bar never
            // shows "fine" while the body is freezing), -1 over a ~12 span.
            float signed;
            if (effectiveTemp < 16f)
            {
                signed = System.Math.Max(-1f, (effectiveTemp - 16f) / 12f);
            }
            else if (effectiveTemp > 22f)
            {
                signed = System.Math.Min(1f, (effectiveTemp - 22f) / 12f);
            }
            else
            {
                signed = 0f;
            }

            npc.Needs.ThermalComfort = signed;
            var magnitude = System.Math.Abs(signed);

            // Spec 29C.10: "the fire burns you if you stand in it" is DEFERRED
            // to the campfire-as-obstacle pass — any HP/comfort hit here
            // reshuffles the dog-fragile colony (NPCs constantly path across
            // the central fire tile) and wipes seeds. onFire is computed and
            // traced so the mechanic is ready to wire once nobody stands on
            // the flames by construction.
            if (onFire)
            {
                Trace.Emit(world, npc.Id, "FireBurn", "On the fire tile (no HP hit yet)");
            }

            if (magnitude >= SimBalance.ThermalDamageGate && !isInWater)
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
                    npc.Body.Parts[part] = System.Math.Max(0f, npc.Body.Parts[part] - thermalHpHit);
                }

                npc.Health = npc.Body.Mean();
                if (npc.Body.VitalDestroyed(out _))
                {
                    npc.Health = 0f;
                }

                DamageReactionSystemHelpers.GrantAdrenaline(world, npc, thermalHpHit, signed > 0f ? "Heatstroke" : "Hypothermia");

                Trace.Emit(world, npc.Id, signed > 0f ? "Heatstroke" : "Hypothermia",
                    $"ThermalComfort={signed:+0.00;-0.00} Health={npc.Health:F2}");
            }

            Trace.Emit(world, npc.Id, "TemperatureUpdate",
                $"Thermal={prevThermal:F3}->{npc.Needs.ThermalDiscomfort:F3} " +
                $"Signed={signed:+0.00;-0.00} " +
                $"EffectiveTemp={effectiveTemp:F1} (Global={world.Environment.GlobalTemperature:F1} " +
                $"Warmth={npc.EquippedWarmth:F2} Fire={fireWarmth:F1}) Pressure={pressure:+0.00;-0.00}");

            // Spec 35.4: sun exposure and sunburn on uncovered parts.
            var effectiveUv = isIndoor || isInWater
                ? 0f
                : world.Environment.UvIndex * (isShaded ? 0.2f : 1f);
            var uncovered = CollectUncoveredParts(world, npc);
            if (effectiveUv > 0.5f && uncovered.Count > 0)
            {
                // Spec 40.7: bare skin under the sun slowly tans (weathered
                // survivor). effectiveUv already carries the shade penalty
                // (isShaded -> x0.2), so you tan LESS in shade. Rate tuned for
                // ~10 game days to full tan at open-sun exposure.
                npc.Needs.TanLevel = MathUtil.Clamp01(
                    npc.Needs.TanLevel + (effectiveUv - 0.5f) * SimBalance.TanRate * uncovered.Count);
                // Spec 40.7: acute redness rises faster than the tan settles —
                // bare skin goes red first, then browns as it heals below.
                npc.Needs.Sunburn = MathUtil.Clamp01(
                    npc.Needs.Sunburn + (effectiveUv - 0.5f) * SimBalance.SunburnRate * uncovered.Count);
                npc.SunExposure += (effectiveUv - 0.5f) * SimBalance.SunExposureRate;
                if (npc.SunExposure > 0.5f)
                {
                    npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort - 0.02f);
                }

                if (npc.SunExposure >= 1f)
                {
                    var pick = (int)(MathUtil.Hash01(world.Seed, world.Tick, npc.Id.Value, 2203) * uncovered.Count);
                    pick = System.Math.Min(pick, uncovered.Count - 1);
                    var burntPart = uncovered[pick];
                    npc.Body.Parts[burntPart] = System.Math.Max(0f, npc.Body.Parts[burntPart] - SimBalance.SunburnBurnDamage);
                    npc.Health = npc.Body.Mean();
                    npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort - 0.15f);
                    npc.SunExposure = 0.5f;
                    if (npc.Body.VitalDestroyed(out var burntVital))
                    {
                        npc.Health = 0f;
                        Trace.Emit(world, npc.Id, "VitalPartDestroyed",
                            $"{burntVital} destroyed by sunstroke");
                    }

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

    // Spec 29C.10: warmth radiated by nearby LIT campfires. On the fire's own
    // tile it is agony (onFire = true); a tile or two away it gently warms.
    internal static float NearbyFireWarmth(WorldState world, TileCoord tile, out bool onFire)
    {
        onFire = false;
        var warmth = 0f;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.ResourceAmount <= 0f ||
                !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                !definition.Tags.Contains("Campfire"))
            {
                continue;
            }

            var dist = HexSpatialMath.HexDistance(tile, obj.Tile);
            if (dist == 0)
            {
                onFire = true;
            }
            else if (dist <= 2)
            {
                warmth = System.Math.Max(warmth, dist == 1 ? SimBalance.FireWarmthRange1 : SimBalance.FireWarmthRange2);
            }
        }

        return warmth;
    }

    // Spec 35.4: within 1 tile of a Shade-tagged object (big tree / palm).
    internal static bool IsShaded(WorldState world, TileCoord tile)
    {
        // Spec 43: real cast shadows — the map is rebuilt from the sun path
        // every medium tick (terrain silhouettes + canopy + hut walls), so
        // shade is directional now: long at dawn/dusk, tight at noon.
        return world.ShadedTiles.Contains(tile);
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
