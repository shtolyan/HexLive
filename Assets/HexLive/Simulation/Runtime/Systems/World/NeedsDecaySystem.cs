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

public sealed class NeedsDecaySystem : ISimulationSystem
{
    public string Name => nameof(NeedsDecaySystem);

    public TickLayer Layer => TickLayer.Slow;

    private static void RecordImpact(
        NPCState npc,
        NeedKind need,
        EffectKind effect,
        bool positive)
    {
        npc.EffectImpacts.Record(
            need,
            effect,
            positive ? EffectImpactDirection.Positive : EffectImpactDirection.Negative,
            EffectImpactCadence.Slow);
    }

    // Balance knobs (SimBalance / HexTuningConfig). The old const names are
    // kept as live shims so every call site below is untouched.
    private static float HungerRate => SimBalance.HungerRate; // pond removal + hex-hop ceremony rebalance: water/food trips got longer
    private static float EnergyRate => SimBalance.EnergyRate; // spec 42: ~1 bar per 200 slow ticks (13 real min)
    private static float ComfortRate => SimBalance.ComfortRate;
    private static float SocialRate => SimBalance.SocialRate; // spec 28.15A
    // Spec 42.A: base eased 0.020 -> 0.018 as the compensating loosening for
    // the sweat multiplier below — same multi-dimensional-budget lesson as
    // §40.18 (0.020 + factor 0.25 broke seed 777; 0.05 alone was homeopathy).
    private static float ThirstRate => SimBalance.ThirstRate; // spec 29E.1; pond removal + §21.21B v5 clamp rebalance

    // Spec 42.A: extra thirst per unit of positive ThermalComfort (sweat).
    private static float SweatThirstFactor => SimBalance.SweatThirstFactor;

    // Bug #149: rain cleans exposed skin, but ten times more slowly than
    // immersion. This is deliberately derived from the canonical water wash
    // rate so tuning bathing cannot silently desynchronise the ratio.
    private const float RainHygieneWashFactor = 0.1f;

    // Spec §49 knobs (moved to HexTuningConfig in the tuning pass).
    private static float SickTorsoPerSlowTick => SimBalance.SickTorsoPerSlowTick;   // pace the budget pay-down (~0.08 over ~40 slow ticks)
    private static float SickTorsoFloor => SimBalance.SickTorsoFloor;          // sickness can't grind the torso below this
    private static float SickComfortPerSlowTick => SimBalance.SickComfortPerSlowTick; // feeling lousy while sick
    private static float AmbientSocialGain => Spec49.AmbientGain; // near company loneliness slowly reverses
    private static float AmbientSocialCap => SocialBalance.AmbientSocialCap;         // ...but only a real chat lifts you past this (§49 ambient social)
    private static float SleepComfortNightSlowTicks => SocialBalance.SleepComfortNightSlowTicks; // Evening+Night ≈ 75 slow ticks

    // Spec §49: comfort gained per slow tick while sleeping, from surface +
    // fireside + sun/rain. A full ~75-slow-tick night sums to the design
    // targets; penalties shave the gain but never invert it (a bed in the rain
    // still nets ~0.70, grass in the sun just nets ~0).
    private static float SleepComfortPerSlowTick(WorldState world, NPCState npc)
    {
        var perNight = Spec49.SleepComfortGrassNight;
        var onBed = false;
        if (npc.Execution.TargetObject is { } objId &&
            world.Entities.Objects.TryGetValue(objId, out var obj))
        {
            perNight = obj.DefinitionId switch
            {
                ContentIds.BedBasic => Spec49.SleepComfortBedNight,
                _ => perNight
            };
            onBed = obj.DefinitionId == ContentIds.BedBasic;
        }

        // A worn jacket/coat padding the bare ground beats sleeping on plain
        // dirt (bunched under the body). A bed already provides its own
        // surface, so this only sweetens the groundless case.
        if (!onBed && WearsJacketOrCoat(world, npc))
        {
            perNight += Spec49.SleepComfortJacketPadNight;
        }

        if (TemperatureSystem.NearbyFireWarmth(world, npc.Tile, out _) > 0f)
        {
            perNight += Spec49.SleepComfortFireBonusNight;
        }

        var roofed = world.Tiles.Items.TryGetValue(npc.Tile, out var tile) &&
            tile.Flags.HasFlag(TileFlags.Indoor);
        var daytime = world.Environment.Phase is DayPhase.Day or DayPhase.Morning;
        if (daytime && !roofed && !TemperatureSystem.IsShaded(world, npc.Tile))
        {
            perNight -= Spec49.SleepComfortSunPenaltyNight;
        }

        if (world.Environment.IsRaining && !roofed)
        {
            perNight -= Spec49.SleepComfortRainPenaltyNight;
        }

        if (perNight < 0f)
        {
            perNight = 0f;
        }

        return perNight / SleepComfortNightSlowTicks;
    }

    // A "jacket/coat" for padding = a torso-covering outer garment (the coat
    // and the leather/heavy jackets qualify; bikini tops, tees and boots do
    // not). Any future outer torso layer is picked up automatically.
    private static bool WearsJacketOrCoat(WorldState world, NPCState npc)
    {
        foreach (var item in npc.WornItems)
        {
            if (!world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var def) ||
                !def.Covers.Contains(BodyPart.Torso))
            {
                continue;
            }

            if (def.Layer == WearLayer.Outerwear || def.Id == ContentIds.Coat)
            {
                return true;
            }
        }

        return false;
    }

    // Spec §49: is there an awake, settled housemate within perception right
    // now? Powers the passive ambient-social trickle.
    private static bool HasNearbyCompanion(NPCState npc)
    {
        foreach (var agent in npc.Perception.Agents)
        {
            if (agent.IsReachable && !agent.IsMoving)
            {
                return true;
            }
        }

        return false;
    }

    private static readonly BodyPart[] AllBodyParts =
    {
        BodyPart.Head, BodyPart.Torso, BodyPart.Pelvis,
        BodyPart.ArmL, BodyPart.ArmR, BodyPart.LegL, BodyPart.LegR
    };

    // Spec §60: drop the body into a coma. The full plan teardown mirrors the
    // stamina faint (spec 40.13); idempotent while already out.
    internal static void EnterComa(WorldState world, NPCState npc, ComaCause cause)
    {
        // §105: умирание глубже комы и уже держит тело на земле — кома поверх
        // него только подменила бы условие пробуждения на своё.
        if (npc.Mind.ComaCause != ComaCause.None || npc.Health <= 0f || npc.IsDying)
        {
            return;
        }

        npc.Mind.ComaCause = cause;
        npc.Mind.CryingUntilTick = 0; // §110: кома глубже слёз и вытесняет их
        // §105.14: и глубже притворства — WakeFromComa переспросит на выходе.
        npc.Mind.PlayDeadUntilTick = 0;
        npc.Mind.PlayDeadSinceTick = 0;
        PlanInterruption.TryAbort(world, npc, InterruptionCause.BodyComa, "Collapsed — coma");
        npc.Mind.CurrentGoal = GoalType.None;
        npc.IsFighting = false; // a body that just switched off holds no stance

        // §60: lie down like a ground sleeper — always at the EXACT centre of
        // the hex where she ACTUALLY collapsed, never across a tile rim and
        // never on a neighbouring "safe" tile. A body may change tiles only
        // through the explicit rescue/carry scene (§118.4); collapse itself is
        // a posture transition, not movement (bugs #53/#54).
        // §105: сам примитив переехал в MortalityHelpers — умирание кладёт тело
        // на землю ровно тем же способом, и двух редакций §60.2a быть не должно.
        MortalityHelpers.AnchorLyingBody(world, npc, allowNearbyBed: true);

        // §60 r2: exhaustion reads as SLEEP (she crashed dead-tired), only
        // blood loss reads as unconsciousness — the "coma" framing is gone.
        Trace.Emit(world, npc.Id,
            cause == ComaCause.Exhaustion ? "FellAsleepExhausted" : "FaintedBloodLoss",
            $"Cause={cause} Energy={npc.Needs.Energy:F2} Blood={npc.Needs.Blood:F2} " +
            $"Health={npc.Health:F2}");
    }

    // Spec §60: the coma ends the moment the stat that felled the body climbs
    // back over the wake threshold — she comes to like waking from a bed
    // (same wake grace as an ordinary morning, so the get-up plays out).
    private static void TryWakeFromComa(WorldState world, NPCState npc)
    {
        // §60 r2: the exhausted sleeper sleeps THROUGH to a rested line (a
        // wake at the old 0.15 re-drained to zero within hours — the chain
        // of micro-collapses WAS the chronic energy pit).
        var recovered = npc.Mind.ComaCause switch
        {
            ComaCause.Exhaustion => npc.Needs.Energy >= SimBalance.ExhaustedSleepWakeEnergy,
            ComaCause.BloodLoss => npc.Needs.Blood >= SimBalance.ComaBloodWakeThreshold,
            _ => false
        };

        if (!recovered)
        {
            return;
        }

        WakeFromComa(world, npc, npc.Mind.ComaCause.ToString());
    }

    // §60 r2: shared wake path — the recovery wake and the pain wake (a wound
    // landing on an exhausted sleeper) both release the lying footprint and
    // the junction the body held (mirrors the ground-rest wake path).
    internal static void WakeFromComa(WorldState world, NPCState npc, string cause)
    {
        LyingSpot.ReleaseRestSurfaceOnRise(world, npc);
        npc.Mind.ComaCause = ComaCause.None;
        npc.Mind.WakeGraceUntilTick = world.Tick + AiBalance.WakeGraceTicks; // spec 41.5

        ExecutionSystem.ReleaseClaims(world, npc);
        if (npc.CurrentJunction is { } lay)
        {
            SpatialMutations.FreeJunction(world, lay, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, lay, npc.Id);
        }

        Trace.Emit(world, npc.Id, "WokeUp",
            $"Cause={cause} Energy={npc.Needs.Energy:F2} Blood={npc.Needs.Blood:F2}");

        // §105.14: очнулась — а враг рядом. Тогда не вставать: притвориться
        // мёртвой. Болевое пробуждение (WoundMath) проходит этой же дверью.
        MortalityHelpers.TryStartPlayDead(world, npc);
    }

    // §60.7: утопление. Тело БЕЗ СОЗНАНИЯ (кома/обморок/умирание — весь
    // IsUnconscious) на глубокой воде (тот же предикат, что и у §106:
    // Water && !Walkable по тайлу) держит воду SimBalance.DrownDeathTicks
    // тиков, потом умирает. Очнулась или её вытащили на сушу (тайл больше не
    // плавательный) — таймер сбрасывается без следа. Смерть идёт каноническим
    // путём §105: Health = 0, событие-причина в трассу, тело подберёт свип
    // MobSystem следующим Medium-проходом.
    private static void TickDrowning(WorldState world, NPCState npc)
    {
        if (npc.Health <= 0f ||
            !npc.IsUnconscious(world.Tick) ||
            !CombatMedium.IsNpcSwimming(world, npc))
        {
            npc.Mind.DrowningSinceTick = 0;
            return;
        }

        if (npc.Mind.DrowningSinceTick == 0)
        {
            npc.Mind.DrowningSinceTick = world.Tick;
            return;
        }

        if (world.Tick - npc.Mind.DrowningSinceTick < SimBalance.DrownDeathTicks)
        {
            return;
        }

        // Захлебнулась. Событие эмитится ДО обнуления состояния, чтобы в
        // сообщении осталось, из какого бессознательного она не выплыла.
        Trace.Emit(world, npc.Id, "Drowned",
            $"Unconscious in deep water for {world.Tick - npc.Mind.DrowningSinceTick} ticks " +
            $"(Coma={npc.Mind.ComaCause} Dying={npc.Mind.DyingCause} " +
            $"Blood={npc.Needs.Blood:F2} Energy={npc.Needs.Energy:F2})");

        npc.Mind.DrowningSinceTick = 0;
        npc.Mind.ComaCause = ComaCause.None;
        npc.Mind.FaintedUntilTick = 0;
        npc.Mind.DyingCause = DyingCause.None;
        npc.Mind.DyingReserve = 0f;
        npc.Mind.DyingTickStamp = 0;
        npc.Health = 0f;
    }

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            // Health==0 is a terminal latch. The next Medium MobSystem sweep
            // transfers the NPC to Corpses; slow recovery must not resurrect it.
            if (npc.Health <= 0f)
            {
                continue;
            }

            // §48.7: NeedsDecay owns the beginning of the slow influence
            // frame. TemperatureSystem runs later in the same layer and adds
            // its own row without erasing these.
            npc.EffectImpacts.Clear(EffectImpactCadence.Slow);

            // §52: definitions may cap how many physical instances one NPC can
            // carry. Normal acquisition enforces this before pickup; this pass
            // repairs old saves and legacy direct-add paths without deleting
            // property — extras land at the owner's feet.
            InventoryMath.SpillCarriedLimitExcess(world, npc);

            var prevHunger = npc.Needs.Hunger;
            var prevEnergy = npc.Needs.Energy;
            var prevComfort = npc.Needs.Comfort;
            var prevSocial = npc.Needs.Social;
            // §50: лежала ли она в начале тика. Всё, что ниже, умеет вернуть
            // ноге функцию — естественная регенерация, бинт, таблетка, — и
            // тогда тело поднимается с земли клипом вставания. Замер стоит
            // ЗДЕСЬ, один на весь тик, а не у каждого места лечения: так ни
            // один будущий источник заживления не сможет его забыть.
            var wasProne = npc.Body.IsProne;

            // Spec §60: coma wake check — the body comes to the moment the
            // stat that felled it climbs back over the threshold. Checked
            // before this tick's decay so a recovered body never oversleeps
            // its own wake line.
            TryWakeFromComa(world, npc);

            // §105.14: пока враг в радиусе, окно притворства перевзводится —
            // это и есть гистерезис (Slow 16 тиков против Hold 240), без него
            // она вскакивала бы и падала на каждом шаге зверя туда-сюда.
            // Потолок PlayDeadMaxTicks считается от старта: волк, поселившийся
            // у лагеря, иначе уложил бы её навсегда.
            if (npc.Mind.PlayDeadSinceTick != 0 &&
                world.Tick < npc.Mind.PlayDeadUntilTick &&
                MortalityHelpers.HostileNearby(world, npc))
            {
                npc.Mind.PlayDeadUntilTick = Spec118.Enabled
                    ? world.Tick + Spec105.PlayDeadHoldTicks
                    : System.Math.Min(
                        world.Tick + Spec105.PlayDeadHoldTicks,
                        npc.Mind.PlayDeadSinceTick + Spec105.PlayDeadMaxTicks);
            }

            // Spec 31C.7A: a sleeping body burns less — hour-long sleep
            // blocks must not guarantee a starving wake-up.
            // Spec §60: a comatose body IS a sleeping body for every recovery
            // rule — same low metabolism, same energy/comfort restore.
            var sleeping = npc.Execution.CurrentInteraction == InteractionType.Sleep ||
                npc.Mind.ComaCause != ComaCause.None;
            // §76: Hardiness — "она может дольше не есть и не пить". Folded into
            // the metabolism term rather than into HungerRate/ThirstRate so the
            // sleeping discount and the attribute compose the obvious way, and
            // so there is exactly ONE per-agent factor on the food clock.
            // §85: спящее тело почти ничего не тратит. Было 0.4 — за ночь это
            // съедало заметную долю сытости, и вставали они уже голодными, то
            // есть ночь работала против них дважды: и холодом, и желудком.
            var metabolism =
                (sleeping ? Spec85.SleepMetabolismFactor : 1f) * AttributeMath.MetabolismMult(npc);
            // Spec 42.A: sweating burns water — overheating scales thirst by
            // up to +25% at heatstroke-level heat (ThermalComfort +1). Reads
            // the previous slow tick's signed comfort; cold side is free (a
            // shivering body does not sweat). SOFT knob: SweatThirstFactor.
            var sweat = 1f + SweatThirstFactor * System.Math.Max(0f, npc.Needs.ThermalComfort);
            var hungerDrift = HungerRate * metabolism;
            var thirstDrift = ThirstRate * metabolism;
            npc.Needs.Hunger = MathUtil.Clamp01(npc.Needs.Hunger + hungerDrift);
            npc.Needs.Thirst = MathUtil.Clamp01(npc.Needs.Thirst + thirstDrift * sweat);
            RecordImpact(npc, NeedKind.Hunger, EffectKind.NaturalDecay, positive: false);
            RecordImpact(npc, NeedKind.Thirst, EffectKind.NaturalDecay, positive: false);
            if (sweat > 1f)
            {
                RecordImpact(npc, NeedKind.Thirst, EffectKind.Hot, positive: false);
            }
            // §76: Endurance is the "can stay up" half of the stat — she runs
            // down toward sleep slower. (Fighting still suspends the drain
            // entirely, as before.)
            // §54.11 r2: sleep recovery is a NET game-hour contract. Charging
            // awake EnergyRate while simultaneously adding a sleep bonus made
            // the duration depend on two unrelated dials and hid a second
            // restore stream in the timed interaction. An asleep body neither
            // spends waking energy nor fights; the single recovery block below
            // owns the clock.
            var energyDrain = sleeping || npc.IsFighting
                ? 0f
                : EnergyRate * AttributeMath.EnergyDrainMult(npc);

            // §76.13: Hardiness has only one teacher — going without. Counted
            // while she is genuinely in the red on food, water or temperature,
            // not merely peckish, so a comfortable colony never trains it.
            if (npc.Needs.Hunger >= Spec76.AttributeHardshipGate ||
                npc.Needs.Thirst >= Spec76.AttributeHardshipGate ||
                npc.Needs.ThermalDiscomfort >= Spec76.AttributeHardshipGate)
            {
                AttributeMath.Train(npc, AttributeKind.Hardiness,
                    Spec76.AttributeTrainPerHardshipTick);
            }
            npc.Needs.Energy = MathUtil.Clamp01(npc.Needs.Energy - energyDrain);
            if (energyDrain > 0f)
            {
                RecordImpact(npc, NeedKind.Energy, EffectKind.NaturalDecay, positive: false);
            }

            // §54.11 r2: the ONE sleep-energy channel. Ground/coma use the
            // 8-hour base; bed.basic adds the matching increment for 4 hours.
            // Legacy interaction and fire energy are zeroed in live balance;
            // fire still improves warmth and comfort, just not this clock.
            if (sleeping)
            {
                var wake = SimBalance.SleepEnergyBaseBonus;
                if (SimBalance.SleepEnergyBaseBonus > 0f)
                {
                    RecordImpact(npc, NeedKind.Energy, EffectKind.Sleeping, positive: true);
                }
                if (TemperatureSystem.NearbyFireWarmth(world, npc.Tile, out _) > 0f)
                {
                    wake += SimBalance.SleepEnergyFireBonus;
                    if (SimBalance.SleepEnergyFireBonus > 0f)
                    {
                        RecordImpact(npc, NeedKind.Energy, EffectKind.Cozy, positive: true);
                    }
                }

                if (npc.Execution.TargetObject is { } bedId &&
                    world.Entities.Objects.TryGetValue(bedId, out var bedObj))
                {
                    var bedBonus = bedObj.DefinitionId switch
                    {
                        ContentIds.BedBasic => SimBalance.SleepEnergyBasicBedBonus,
                        _ => 0f
                    };
                    wake += bedBonus;
                    if (bedBonus > 0f)
                    {
                        RecordImpact(npc, NeedKind.Energy, EffectKind.Snug, positive: true);
                    }
                }

                npc.Needs.Energy = MathUtil.Clamp01(npc.Needs.Energy + wake);
            }

            // Spec §60: energy drained to nothing on her feet — the body
            // simply switches off where it stands. (Asleep she is already
            // recovering; only an awake body can burn to the collapse line.)
            if (!sleeping && npc.Needs.Energy <= 0f)
            {
                EnterComa(world, npc, ComaCause.Exhaustion);
            }

            // Spec §49: unified sleep-comfort. Asleep, comfort no longer drains —
            // the surface + fire + sun + rain formula fills it (bare grass
            // ~0.05/night, +fire ~0.05, a bed ~1.0 minus sun/rain penalties).
            // Awake, the usual slow drain applies — UNLESS she's by a lit fire,
            // whose cosy warmth reverses the drain into a small comfort gain
            // (spec §49.8; drives the "Cozy" status chip too).
            if (sleeping && Spec49.SleepComfort)
            {
                var sleepComfort = SleepComfortPerSlowTick(world, npc);
                npc.Needs.Comfort = MathUtil.Clamp01(
                    npc.Needs.Comfort + sleepComfort);
                if (sleepComfort > 0f)
                {
                    RecordImpact(npc, NeedKind.Comfort, EffectKind.Sleeping, positive: true);
                }
            }
            else if (TemperatureSystem.NearbyFireWarmth(world, npc.Tile, out _) > 0f)
            {
                npc.Needs.Comfort = MathUtil.Clamp01(
                    npc.Needs.Comfort + Spec49.AwakeFireComfortGain);
                RecordImpact(npc, NeedKind.Comfort, EffectKind.Cozy, positive: true);
            }
            else
            {
                npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort - ComfortRate);
                RecordImpact(npc, NeedKind.Comfort, EffectKind.NaturalDecay, positive: false);
            }

            // §49.7: wet clothes cling — being soaked shaves a little comfort on
            // top (wet underwear counts here too: it doesn't slow you, but it's
            // still miserable). Just the fact of being wet. Spec 35.5: a bare
            // wet body counts as well, so a near-naked survivor in the rain is
            // miserable even with no garment to soak.
            var maxWornWet = npc.BodyWetness;
            foreach (var worn in npc.WornItems)
            {
                if (worn.Wetness > maxWornWet)
                {
                    maxWornWet = worn.Wetness;
                }
            }
            if (maxWornWet > 0.5f)
            {
                npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort - Spec49.WetComfortPenalty);
                RecordImpact(npc, NeedKind.Comfort, EffectKind.Soaked, positive: false);
            }


            var dirtyClothing = EquipmentMath.AverageDirtiness(npc);
            var clothingComfortDelta = EquipmentMath.ClothingDirtComfortDelta(dirtyClothing);
            if (clothingComfortDelta > 0f && npc.WornItems.Count > 0)
            {
                npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort + clothingComfortDelta);
                RecordImpact(npc, NeedKind.Comfort, EffectKind.CleanClothes, positive: true);
            }
            else if (clothingComfortDelta < 0f)
            {
                npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort + clothingComfortDelta);
                RecordImpact(npc, NeedKind.Comfort, EffectKind.DirtyClothes, positive: false);
            }

            // Spec §49: passive "second action" socialising — being near an
            // awake, settled housemate while you do your own thing (eat, sit,
            // tend the fire) eases loneliness a touch, Sims-style. A trickle, not
            // a substitute: it can't lift Social past a modest cap, so a real
            // chat is still wanted to feel truly social.
            // §85: во сне одиночество почти не копится. Раньше оно текло
            // ПОЛНОСТЬЮ — человек ложился общительным, а вставал одиноким, хотя
            // во сне не с кем и не поговорить. Для чужака это било вдвойне: его
            // Social и так не закрывается ничем, кроме насилия.
            var socialDrift = SocialRate * (sleeping ? Spec85.SleepSocialFactor : 1f);
            npc.Needs.Social = MathUtil.Clamp01(npc.Needs.Social - socialDrift);
            RecordImpact(npc, NeedKind.Social, EffectKind.NaturalDecay, positive: false);
            if (Spec49.AmbientSocial && !sleeping && npc.Needs.Social < AmbientSocialCap && HasNearbyCompanion(npc))
            {
                npc.Needs.Social = System.Math.Min(
                    AmbientSocialCap, npc.Needs.Social + AmbientSocialGain);
                RecordImpact(npc, NeedKind.Social, EffectKind.NearbyCompany, positive: true);
            }

            // Spec §53: compassion is SPENT witnessing un-helped suffering nearby
            // — the drain scales with the worst reachable neighbour's plight and
            // this girl's own CompassionTrait — and it recovers toward full when
            // the colony around her is well. (A completed aid tops it up directly
            // in RunAid.) Uses the perception snapshot's per-neighbour Suffering,
            // refreshed earlier this tick.
            if (Spec53.Enabled && !sleeping)
            {
                var worstNearby = 0f;
                foreach (var agent in npc.Perception.Agents)
                {
                    if (agent.IsReachable && agent.Suffering > worstNearby)
                    {
                        worstNearby = agent.Suffering;
                    }
                }
                if (worstNearby >= Spec53.SufferingThreshold)
                {
                    npc.Needs.Compassion = MathUtil.Clamp01(npc.Needs.Compassion -
                        Spec53.CompassionRate * worstNearby * npc.CompassionTrait);
                    RecordImpact(npc, NeedKind.Compassion, EffectKind.WitnessingSuffering, positive: false);
                }
                else
                {
                    npc.Needs.Compassion = MathUtil.Clamp01(
                        npc.Needs.Compassion + Spec53.RecoverRate);
                    RecordImpact(npc, NeedKind.Compassion, EffectKind.EveryoneSafe, positive: true);
                }
            }

            // Spec §49: raw-water gut-rot damage-over-time — pay down the bounded
            // sickness budget a little each slow tick, floored at SickTorsoFloor
            // (sickness alone still can't kill; it leaves you fragile). Comfort
            // malaise rides the visible window so being ill feels bad.
            if (npc.Mind.SicknessDamageRemaining > 0f)
            {
                if (npc.Body.Parts[BodyPart.Torso] > SickTorsoFloor)
                {
                    var dock = System.Math.Min(SickTorsoPerSlowTick, npc.Mind.SicknessDamageRemaining);
                    npc.Body.Parts[BodyPart.Torso] =
                        System.Math.Max(SickTorsoFloor, npc.Body.Parts[BodyPart.Torso] - dock);
                    npc.Mind.SicknessDamageRemaining -= dock;
                    npc.Health = npc.Body.Mean();
                    // §105: через общую развилку, как и всякий другой урон по
                    // телу. (Практически недостижимо — SickTorsoFloor 0.15 не
                    // даёт болезни доломать грудь; ветка живёт ради того, чтобы
                    // ни один сайт урона не остался со своим ответом.)
                    MortalityHelpers.ResolveTrauma(world, npc, dock, "sickness");

                    DamageReactionSystemHelpers.GrantAdrenaline(world, npc, dock, "Sickness");
                }
                else
                {
                    // torso already at the floor — the rest of the budget is a
                    // no-op (sickness can't push a mauled body under), drain it.
                    npc.Mind.SicknessDamageRemaining = 0f;
                }
            }

            if (world.Tick < npc.Mind.SickUntilTick)
            {
                npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort - SickComfortPerSlowTick);
                RecordImpact(npc, NeedKind.Comfort, EffectKind.Sick, positive: false);
            }

            // Spec 40.1: stamina. Its ceiling is how fed/rested/comfortable the
            // body is (you can't be spry starving). It drains while working or
            // moving, recovers fast while resting (sit/sleep), slowly while
            // idle — and moves toward that ceiling either way. Soft in v1: it
            // does NOT gate actions (that would collapse the economy); it only
            // colours the UI and nudges the rest goals (below).
            // §76: Endurance raises the roof on that ceiling. The polynomial is
            // untouched — the attribute scales its result, so the "you can't be
            // spry starving" shape is preserved and only the height moves.
            var staminaCeiling = MathUtil.Clamp01(
                (0.30f + 0.35f * (1f - npc.Needs.Hunger) + 0.25f * npc.Needs.Energy +
                 0.10f * npc.Needs.Comfort) * AttributeMath.StaminaCeilingMult(npc));
            var resting = npc.Execution.CurrentInteraction is
                InteractionType.Sit or InteractionType.Sleep or InteractionType.Rest ||
                npc.Mind.ComaCause != ComaCause.None || // §60: a coma rests the body too
                npc.IsDying;                            // §105: и лежащая на грани тоже
            var working = npc.Execution.Status == ExecutionStatus.InProgress && !resting;
            // §76: and she spends it slower at the job. Only the WORK drain is
            // scaled — the rest/idle gains are the body's clock, not hers.
            // §105: «едва живая» после спасения — восстанавливается втрое
            // медленнее, тратит вдвое быстрее. Множители сидят ровно там же,
            // где §76-й: одна цепочка на стамину, а не вторая ветка рядом.
            var convalescing = Spec105.DyingEnabled && world.Tick < npc.Mind.ConvalescentUntilTick;
            var staminaRegen = convalescing ? Spec105.ConvalescentStaminaRegenFactor : 1f;
            var staminaDrain = convalescing ? Spec105.ConvalescentStaminaDrainFactor : 1f;
            var staminaDelta = resting ? SimBalance.StaminaRestGain * staminaRegen
                : working
                    ? -SimBalance.StaminaWorkDrain * AttributeMath.StaminaDrainMult(npc) * staminaDrain
                    : SimBalance.StaminaIdleGain * staminaRegen;
            // Bug #152: the dynamic ceiling limits how much reserve a hungry,
            // exhausted body can BUILD, but a positive rest tick must never
            // make the bar run backwards. If the ceiling fell below an already
            // accumulated reserve, hold that reserve until metabolism catches
            // up; work can still spend it normally.
            var staminaUpper = resting
                ? System.MathF.Max(staminaCeiling, npc.Needs.Stamina)
                : staminaCeiling;
            npc.Needs.Stamina = MathUtil.Clamp(
                npc.Needs.Stamina + staminaDelta, 0f, staminaUpper);
            RecordImpact(
                npc,
                NeedKind.Stamina,
                resting ? EffectKind.Resting : working ? EffectKind.Working : EffectKind.Resting,
                positive: staminaDelta >= 0f);
            if (convalescing)
            {
                RecordImpact(npc, NeedKind.Stamina, EffectKind.Convalescent, positive: false);
            }

            // §110.10/§146.12: stress responds to danger NOW, not to the long-lived
            // spatial notebook used for route avoidance. Far-spotted and old
            // Memory.Dangers are common even during an ordinary workday; using
            // their Count here pinned healthy colonists at 100% indefinitely.
            // Fighting, fleeing and the post-hit adrenaline window remain full
            // threats. A visible human is separate: Outsiders/open war are 1.0,
            // while personal dislike toward the observer scales the rise from
            // zero to one. Merely seeing a neutral camp therefore costs nothing.
            var fullThreat = npc.IsFighting || npc.Mind.CurrentGoal == GoalType.Flee ||
                DamageReactionSystemHelpers.IsAdrenalineActive(world, npc);
            var bodyCrisis =
                npc.Health < 0.6f || npc.Needs.Hunger >= 0.85f || npc.Needs.Thirst >= 0.85f;
            var humanThreat = CampDiplomacyMath.VisibleHumanThreatFactor(world, npc);
            var stressDelta = fullThreat || bodyCrisis
                ? SimBalance.StressUpRate
                : humanThreat > 0f
                    ? SimBalance.StressUpRate * humanThreat
                    : -SimBalance.StressDownRate;
            npc.Needs.Stress = MathUtil.Clamp01(npc.Needs.Stress + stressDelta);
            RecordImpact(
                npc,
                NeedKind.Stress,
                fullThreat ? EffectKind.Threatened
                    : bodyCrisis ? EffectKind.BodyCrisis
                    : humanThreat > 0f ? EffectKind.Threatened
                    : EffectKind.Calm,
                positive: stressDelta < 0f);

            // §28/§146.12: friendship is active support, including across camp
            // borders. RunTalk owns only the initiator's interaction state, so
            // the shared helper also recognises the passive listener. Relief is
            // directed: each side receives exactly her own positive affinity.
            var friendshipRelief =
                CampDiplomacyMath.ActiveConversationReliefFactor(world, npc);
            if (friendshipRelief > 0f)
            {
                npc.Needs.Stress = MathUtil.Clamp01(
                    npc.Needs.Stress - SimBalance.StressDownRate * friendshipRelief);
                RecordImpact(npc, NeedKind.Stress, EffectKind.FriendlyTalk, positive: true);
            }

            // §110: слёзы отпускают САМИ — это и есть смысл разрядки, поэтому
            // облегчение идёт поверх формулы выше, а не вместо неё. Без него
            // рыдающая лежит с неубывающим стрессом всякий раз, когда причина
            // ещё при ней: память об опасности живёт 2400 тиков, а весь плач —
            // 240, так что stressUp держал бы полосу на максимуме до конца и
            // она срывалась бы снова сразу, как встанет. Ставка НА МЕДЛЕННЫЙ
            // тик, как и обе соседние: за плач набегает около −0.6, то есть
            // «потихонечку приходит в норму», а не обвал в ноль за секунды.
            if (npc.IsCrying(world.Tick))
            {
                npc.Needs.Stress = MathUtil.Clamp01(
                    npc.Needs.Stress - SimBalance.CryingStressRelief);
                RecordImpact(npc, NeedKind.Stress, EffectKind.Crying, positive: true);
            }

            // Spec 40.13: collapse. Utterly spent stamina AND a body pushed to
            // the edge (starving or bleeding) drops the NPC unconscious — it
            // lies helpless for ~80 ticks, then rises. Rare by construction,
            // so it barely perturbs the colony.
            if (world.Tick >= npc.Mind.FaintedUntilTick &&
                world.Tick >= npc.Mind.WakeGraceUntilTick &&
                npc.Mind.ComaCause == ComaCause.None && !npc.IsDying &&
                npc.Needs.Stamina <= 0.01f &&
                (npc.Needs.Hunger >= 0.9f || npc.Needs.Blood < 0.25f) &&
                npc.Health > 0f)
            {
                var faintedUntilTick = world.Tick + 80;
                PlanInterruption.TryAbort(world, npc, InterruptionCause.Faint, "Collapsed — unconscious");
                npc.Mind.CurrentGoal = GoalType.None;
                // §113 r2: choose the nearest sub-grid pose whose whole body is
                // supported; a furnished tile may fall back to a free bed that
                // is already at hand, but never to an intersecting old point.
                if (ExecutionSystem.TryLieDownForCollapse(
                        world, npc, faintedUntilTick))
                {
                    npc.Mind.FaintedUntilTick = faintedUntilTick;
                    Trace.Emit(world, npc.Id, "Fainted",
                        $"Stamina={npc.Needs.Stamina:F2} Hunger={npc.Needs.Hunger:F2} Blood={npc.Needs.Blood:F2}");
                }
                else if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "LieDownSpot",
                        "State=Collapse Outcome=Deferred no collision-free ground rectangle or nearby free bed");
                }
            }
            // Spec §110: the STRESS arm of the same collapse is not a faint —
            // she is conscious, she just can't go on. She lies down and CRIES:
            // the body rests exactly like the faint, but pain snaps her out and
            // a consoling friend (§53) shortens it. Split from the branch above
            // so hunger/blood keep the old unconscious faint.
            // Пока её бьют — не ложится: адреналин (§29C, свежая боль/испуг) и
            // стойка боя держат её на ногах. Без этого гейта соак на сиде 7
            // поймал цикл «легла → удар оборвал слёзы → легла снова» с шагом в
            // 16 тиков: боль обнуляет CryingUntilTick, а стресс и стамина под
            // избиением остаются на своих концах шкалы. Слёзы приходят ПОСЛЕ.
            else if (world.Tick >= npc.Mind.CryingUntilTick && npc.Needs.Stamina <= 0.01f &&
                npc.Needs.Stress >= 0.95f && npc.Health > 0f &&
                npc.Mind.ComaCause == ComaCause.None && !npc.IsDying &&
                !npc.IsFighting && !DamageReactionSystemHelpers.IsAdrenalineActive(world, npc))
            {
                var cryingUntilTick = world.Tick + SimBalance.CryingBreakdownTicks;
                PlanInterruption.TryAbort(world, npc, InterruptionCause.Crying, "Broke down crying");
                npc.Mind.CurrentGoal = GoalType.None;
                if (ExecutionSystem.TryLieDownForCrying(
                        world, npc, cryingUntilTick))
                {
                    npc.Mind.CryingUntilTick = cryingUntilTick;
                    Trace.Emit(world, npc.Id, "CryingBreakdown",
                        $"Stamina={npc.Needs.Stamina:F2} Stress={npc.Needs.Stress:F2}");
                }
                else if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "LieDownSpot",
                        "State=Crying Outcome=Deferred no collision-free ground rectangle or nearby free bed");
                }

                // The posture solver may legitimately find no free rectangle
                // in a crowded furnished room. The breakdown still happened:
                // hold the conscious crying state while standing instead of
                // reselecting and re-aborting Socialize/Aid every slow tick.
                if (npc.Mind.CryingUntilTick < cryingUntilTick)
                {
                    npc.Mind.CryingUntilTick = cryingUntilTick;
                    Trace.Emit(world, npc.Id, "CryingBreakdown",
                        $"Stamina={npc.Needs.Stamina:F2} Stress={npc.Needs.Stress:F2} Surface=Standing");
                }
            }

            // §40.6 r10 (bug #18): water itself washes the body, regardless of
            // WHY she entered it. Previously HygieneWashGain was only consumed
            // by the authored Bathe interaction, so a swimmer, a wader filling
            // a bottle, or an unconscious body in water kept getting dirtier.
            // TileFlags.Water deliberately includes both shallows and deep swim
            // tiles — the same contract the view uses for body wetness.
            var standingInWater = world.Tiles.Items.TryGetValue(npc.Tile, out var hygieneTile) &&
                hygieneTile.Flags.HasFlag(TileFlags.Water);
            var washingInRain = !standingInWater && ShelterMath.RainReaches(world, npc.Tile);
            var hygieneDelta = standingInWater
                ? SimBalance.HygieneWashGain
                : washingInRain
                    ? SimBalance.HygieneWashGain * RainHygieneWashFactor
                    : -SimBalance.HygieneDriftLoss;
            npc.Needs.Hygiene = MathUtil.Clamp01(npc.Needs.Hygiene +
                hygieneDelta);
            RecordImpact(
                npc,
                NeedKind.Hygiene,
                standingInWater ? EffectKind.Washing
                    : washingInRain ? EffectKind.RainWashed
                    : EffectKind.NaturalDecay,
                positive: hygieneDelta > 0f);
            // §40.8-H r12: тот же физический wash-path смывает кровяную
            // подложку. Дождь делает это с тем же десятикратным замедлением,
            // что и общую гигиену; сухая погода ничего не очищает.
            if (standingInWater || washingInRain)
            {
                var bloodSoilWashFactor = standingInWater
                    ? 1f
                    : RainHygieneWashFactor;
                WoundMath.WashBloodSoil(npc,
                    SimBalance.BloodSoilWashPerTick * bloodSoilWashFactor);
            }
            foreach (var worn in npc.WornItems)
            {
                worn.Dirtiness = MathUtil.Clamp01(worn.Dirtiness + SimBalance.ClothingDirtGain);
            }

            // Spec 40.2: blood. A badly wounded part (< 0.4) bleeds — the worse
            // the wound, the faster; blood refills slowly while fed and rested.
            // Gentle rates so the healthy colony is unaffected: only a mauled
            // NPC bleeds, and it's survivable if the wounds close. At zero the
            // NPC dies of blood loss.
            // §50: a severed zone is 0 forever and unbandageable — without a
            // floor it pins the bleed at maximum for the whole clotting
            // window and every amputation bleeds out. The stump's trauma is
            // already charged as the one-off LimbSeverBloodLoss.
            // Spec 44: clotting — only a FRESH wound (heal01 < 0.3) bleeds;
            // once it starts closing the blood stops, so the deadly window is
            // the first hours after the mauling, not the whole two-day heal.
            // §105: обе половины гейта переехали в MortalityHelpers — умирание
            // спрашивает ровно этот вопрос («отпустило ли кровотечение?»), и
            // вторая редакция условия разошлась бы с этой на первой правке.
            if (Spec118.Enabled && Spec118.MedicalEnabled)
            {
                KenshiMedicalMath.Tick(world, npc);
                if (npc.Health <= 0f)
                {
                    // Bug #54: do not continue into starvation/healing after a
                    // fatal wound-degeneration result in this same slow pass.
                    continue;
                }
            }
            else
            {
            var worstPart = MortalityHelpers.WorstBleedPart(npc);
            var freshWound = MortalityHelpers.HasFreshWound(npc);

            if (worstPart < 0.4f && freshWound)
            {
                // Spec 40.3: a bandage in the pack dresses the worst wound —
                // patch it up, stem the blood, and it's consumed. First aid
                // that turns a mauling from fatal into survivable.
                // Last resort: only when actually bleeding out (blood < 0.35),
                // so it saves a life without re-shuffling the colony over
                // every scratch (every mauling survivor would otherwise shift
                // the deterministic dog-dance and tip fragile seeds).
                if (MedicalSupplyMath.BandageCount(npc) > 0 &&
                    npc.Needs.Blood < SimBalance.BandageBloodThreshold)
                {
                    // Spec 44: spend the pre-made medkit bandages (spec 40.3)
                    // first; only a HERBAL dressing — crafted from gathered
                    // plantain leaves — leaves the leaf-wrap decal, so the
                    // plantain visual always means she actually gathered the
                    // leaves. When all remaining bandages are herbal, this one is.
                    MedicalSupplyMath.TrySpendBandage(npc, out var herbal);
                    foreach (var part in AllBodyParts)
                    {
                        if (npc.Body.IsSevered(part)) continue; // §50: a severed zone can't be dressed or healed
                        if (npc.Body.Parts[part] < 0.4f)
                        {
                            npc.Body.Parts[part] = MathUtil.Clamp01(npc.Body.Parts[part] + 0.15f); // spec 42
                            // Spec 44: herbal -> leaf-wrap decal (gathered plantain);
                            // medkit -> plain gauze decal. A zone shows one or the
                            // other, never both.
                            if (herbal)
                            {
                                npc.BandagedZones.Add(part);
                                npc.GauzeZones.Remove(part);
                            }
                            else
                            {
                                npc.GauzeZones.Add(part);
                                npc.BandagedZones.Remove(part);
                            }
                        }
                    }

                    npc.Health = npc.Body.Mean();
                    npc.Needs.Blood = MathUtil.Clamp01(npc.Needs.Blood + 0.25f); // spec 42
                    RecordImpact(npc, NeedKind.Blood, EffectKind.Bandaged, positive: true);
                    Trace.Emit(world, npc.Id, "Bandaged",
                        $"Dressed the wounds (Health={npc.Health:F2})");
                }
                else if (MedicalSupplyMath.PillCount(npc) > 0 && npc.Health < 0.3f)
                {
                    // Spec 40.3: pills — the last-resort backup to the bandage.
                    // Only at the brink (Health < 0.3, no bandage fired): spend
                    // a pill to lift the wounded parts and HP a step and stem
                    // the blood a little. Fires only for an NPC about to die, so
                    // it can save a life without shifting the healthy colony.
                    MedicalSupplyMath.TrySpendPill(npc);
                    foreach (var part in AllBodyParts)
                    {
                        if (npc.Body.IsSevered(part)) continue; // §50: a severed zone can't be healed
                        if (npc.Body.Parts[part] < 0.4f)
                        {
                            npc.Body.Parts[part] = MathUtil.Clamp01(npc.Body.Parts[part] + 0.10f); // spec 42
                        }
                    }

                    npc.Health = npc.Body.Mean();
                    npc.Needs.Blood = MathUtil.Clamp01(npc.Needs.Blood + 0.15f); // spec 42
                    RecordImpact(npc, NeedKind.Blood, EffectKind.BloodRecovery, positive: true);
                    Trace.Emit(world, npc.Id, "Medicated",
                        $"Took a pill at the brink (Health={npc.Health:F2})");
                }
                else
                {
                    // §46 difficulty pass: 0.06 -> 0.09. With the r4/r5
                    // survival fixes the colony won 6/6 — the game needs
                    // teeth back, and bleeding is the "sharp" death channel
                    // (dramatic, fightable with bandages) rather than the
                    // slow-grind ones we deliberately softened.
                    // Spec §60: in a blood-loss coma the wound still bleeds, but
                    // the coma's deep rest knits some of it back (same fed gate
                    // as spec 44) — a race between the open wound and the
                    // healing sleep. Reaching 0 is still death (spec 40.2).
                    // §76: Toughness clots faster — she bleeds out slower from
                    // the same wound. This is half of what "more HP" means in a
                    // game with no max HP (§76.3); the other half is the damage
                    // reduction in EquipmentMath.Mitigate.
                    var bleed = (0.4f - worstPart) * SimBalance.BleedRateFactor *
                        AttributeMath.BleedMult(npc);
                    if (npc.Mind.ComaCause == ComaCause.BloodLoss &&
                        npc.Needs.Hunger < SimBalance.HealHungerGate)
                    {
                        bleed -= SimBalance.BloodRefillPerTick * 3f;
                    }

                    npc.Needs.Blood = MathUtil.Clamp01(npc.Needs.Blood - bleed);
                    RecordImpact(
                        npc,
                        NeedKind.Blood,
                        bleed > 0f ? EffectKind.Bleeding : EffectKind.BloodRecovery,
                        positive: bleed <= 0f);
                    if (npc.Needs.Blood <= 0f)
                    {
                        // §105: кровь на нуле больше не убивает В ТОТ ЖЕ ТИК —
                        // тело падает и умирает, пока запас не вытечет. Кил-свитч
                        // возвращает мгновенную смерть слово в слово.
                        if (Spec105.DyingEnabled)
                        {
                            MortalityHelpers.EnterDying(world, npc, DyingCause.BloodLoss);
                        }
                        else
                        {
                            npc.Health = 0f;
                            Trace.Emit(world, npc.Id, "BledOut", $"Worst part {worstPart:F2} — blood loss");
                        }
                    }
                    else
                    {
                        Trace.Emit(world, npc.Id, "Bleeding",
                            $"Worst={worstPart:F2} Blood={npc.Needs.Blood:F2}");
                    }
                }
            }
            else if (npc.Needs.Blood < 1f && npc.Needs.Hunger < SimBalance.HealHungerGate)
            {
                // Spec 44: bed rest — sleeping knits blood x3, huddling by a
                // burning fire x2; a fed girl who lies low pulls through.
                // Spec §60: a coma counts as the deepest bed rest there is.
                var bloodPace = npc.Execution.CurrentInteraction == InteractionType.Sleep ||
                    npc.Mind.ComaCause != ComaCause.None ? 3f
                    : TemperatureSystem.NearbyFireWarmth(world, npc.Tile, out _) > 0f ? 2f
                    : 1f;
                npc.Needs.Blood = MathUtil.Clamp01(npc.Needs.Blood + SimBalance.BloodRefillPerTick * bloodPace); // spec 44
                RecordImpact(npc, NeedKind.Blood, EffectKind.BloodRecovery, positive: true);
            }

            // Spec §60: blood at the coma line — whatever drained it (the
            // bleed above, a heavy hit, a severed limb) — drops the body.
            // At 0 she is already dead (EnterComa guards Health), so this
            // only catches the razor's-edge band above the death line.
            if (npc.Needs.Blood <= SimBalance.ComaBloodEnterThreshold)
            {
                EnterComa(world, npc, ComaCause.BloodLoss);
            }
            }

            // Spec 28.15B: post-quarrel embarrassment fades with time.
            npc.Social.Embarrassment = MathUtil.Clamp01(npc.Social.Embarrassment - 0.02f);

            // Spec 28.15B: affinity drifts toward neutral asymmetrically —
            // grudges fade fast, friendships cool slowly (a symmetric drift
            // would outrun talk gains and cap warmth at ~+0.2).
            foreach (var relationship in npc.Social.Relationships.Values)
            {
                // §94: скорости затухания стали РУЧКАМИ. Зашитые числа делали
                // отношения слишком быстрыми: дружба доходила до максимума за
                // день, а обида таяла быстрее, чем копилась, — из-за чего
                // неприязнь чужака намертво застревала около −0.17 и он
                // никогда не добирался до порога, за которым берётся за нож.
                var driftRate = relationship.Affinity < 0f
                    ? Spec94.GrudgeDriftPerTick
                    : Spec94.WarmthDriftPerTick;
                relationship.Affinity = MathUtil.MoveTowards(relationship.Affinity, 0f, driftRate);
            }

            // §45 r5: emergency unload — the raft/hearth stockpile must never
            // cost a life. getFoodAvail requires inventory SPACE, and §45 r3
            // fills packs (3 raft logs + leaves + tools) that nothing ever
            // empties: on the r5 25-day soak 6 of 8 starvation deaths died at
            // Hunger=1.00 with 10/10 slots of logs/leaves and ZERO food —
            // coconuts abundant (25-39 on the ground, producers at cap) but
            // un-pick-up-able. A genuinely starving girl with a full pack and
            // no food in it now drops one carried resource per slow tick
            // (wood, then leaves, then stone — never tools) at her feet, so
            // GetFood can fire again. Last-resort by construction (hunger
            // >= 0.8), like food-sharing/theft — the healthy colony never
            // sees it.
            // Jul 2026: the same trap kills via THIRST — a pack full of
            // logs/sticks blocks GetWater/forage exactly like it blocked
            // GetFood, and the girl stood at Goal=None x362 cycles until the
            // thirst threshold death (seed 42 d5-6, whole colony at one tile).
            // Same last-resort construction, same junk order.  §63: the one
            // stick + one stone reserved for an emergency coconut knife are
            // excluded.  Dropping either created a literal pick-up/drop loop;
            // the goal-aware pickup path may instead shed an ordinary tool.
            var packStarved = npc.Needs.Hunger >= 0.8f &&
                npc.Inventory.FindFirstFood(world.Content) is null;
            var packParched = npc.Needs.Thirst >= 0.8f &&
                npc.Inventory.FindFirstDrink(world.Content) is null &&
                !npc.Inventory.Items.Contains(ContentIds.Coconut);
            if ((packStarved || packParched) && !npc.Inventory.HasSpace)
            {
                foreach (var junk in new[] { ContentIds.Log, ContentIds.Stick, ContentIds.PalmLeaf, ContentIds.Stone })
                {
                    if (InventoryMath.IsReservedEmergencyKnifeMaterial(world, npc, junk))
                    {
                        continue;
                    }

                    var idx = npc.Inventory.Items.FindIndex(i => i.DefinitionId == junk);
                    if (idx >= 0)
                    {
                        var item = npc.Inventory.Items[idx];
                        npc.Inventory.Items.RemoveAt(idx);
                        ExecutionSystem.DropItemAtFeet(world, npc, item);
                        Trace.Emit(world, npc.Id, "EmergencyUnload",
                            $"Dropped {junk} (Hunger={npc.Needs.Hunger:F2}, full pack, no food)");
                        break;
                    }
                }
            }

            // Spec 29C.2: starvation / dehydration cost HP. Without this an
            // NPC whose needs maxed out (food/water unreachable) hangs forever
            // — Health never falls, it never dies, its slot never frees. The
            // 0.95 gate sits above the 0.85 starving appraisal, so healthy
            // colonies that briefly spike lose nothing; only a truly stuck
            // agent drains to death.
            var starved = npc.Needs.Hunger >= SimBalance.StarveDeathThreshold;
            var parched = npc.Needs.Thirst >= SimBalance.StarveDeathThreshold;

            // Spec 40.5: emergent cooperation — a well-fed housemate already
            // standing beside someone starving at the death-brink hands over a
            // spare food item. Passive last-resort (no goal, no reroute): it
            // fires only on this exact adjacency, so like the pill it saves a
            // life without disturbing the healthy colony's routine.
            if (starved && npc.CurrentJunction is { } hungryJct)
            {
                foreach (var other in world.Entities.Npcs.Values)
                {
                    if (other.Id.Equals(npc.Id) || other.Health <= 0f ||
                        // §146.12: a friend from another solo camp can share too;
                        // the directed care predicate still seals Outsiders off.
                        !CampDiplomacyMath.CanProvideCare(world, other, npc) ||
                        other.Needs.Hunger >= 0.4f || other.IsFighting ||
                        other.Mind.CurrentGoal == GoalType.Flee ||
                        other.CurrentJunction is not { } giverJct)
                    {
                        continue;
                    }

                    var adjacent = giverJct.Equals(hungryJct) ||
                        (world.Junctions.Items.TryGetValue(giverJct, out var gj) &&
                         gj.Neighbors.Contains(hungryJct));
                    if (!adjacent)
                    {
                        continue;
                    }

                    var food = other.Inventory.FindFirstFood(world.Content);
                    if (food is null)
                    {
                        continue;
                    }

                    other.Inventory.Items.Remove(food);
                    npc.Needs.Hunger = MathUtil.Clamp01(npc.Needs.Hunger - 0.5f);
                    starved = npc.Needs.Hunger >= SimBalance.StarveDeathThreshold;
                    Trace.Emit(world, npc.Id, "FoodShared",
                        $"Given {food} by NPC{other.Id.Value} (Hunger={npc.Needs.Hunger:F2})");
                    break;
                }
            }

            // Spec 40.5: theft — if still starving at the brink and nobody
            // shared, take food from an adjacent housemate who has some (a hard
            // choice under scarcity; the victim loses the meal). Mirrors the
            // food-sharing block but takes regardless of the victim's own state.
            if (starved && npc.CurrentJunction is { } thiefJct)
            {
                foreach (var victim in world.Entities.Npcs.Values)
                {
                    if (victim.Id.Equals(npc.Id) || victim.Health <= 0f ||
                        victim.CurrentJunction is not { } victimJct)
                    {
                        continue;
                    }

                    // §81: кража стала ВНУТРИФРАКЦИОННОЙ. Раньше она нарочно
                    // не гейтилась — «голодный чужак, ворующий у девушек, это
                    // ровно то трение, которое нам нужно», — но теперь у него
                    // есть настоящая сцена с требованием, ударами и эмодзи, а
                    // тихая кража мимо неё только мешает: он молча уносит еду,
                    // пока идёт эту же еду отжимать, и сцена оказывается ни к
                    // чему. Осталось то, чем она и должна была быть: отчаявшаяся
                    // соседка забирает у соседки — зеркало блока раздачи выше,
                    // тоже гейтованного по своим.
                    if (Spec81.AbuseSupersedesPassiveTheft &&
                        !FactionRelations.AreAllies(npc.Faction, victim.Faction))
                    {
                        continue;
                    }

                    var adjacent = victimJct.Equals(thiefJct) ||
                        (world.Junctions.Items.TryGetValue(victimJct, out var vj) &&
                         vj.Neighbors.Contains(thiefJct));
                    if (!adjacent)
                    {
                        continue;
                    }

                    var loot = victim.Inventory.FindFirstFood(world.Content);
                    if (loot is null)
                    {
                        continue;
                    }

                    victim.Inventory.Items.Remove(loot);
                    npc.Needs.Hunger = MathUtil.Clamp01(npc.Needs.Hunger - 0.5f);
                    starved = npc.Needs.Hunger >= SimBalance.StarveDeathThreshold;
                    Trace.EmitSystem(world, "FoodStolen",
                        $"NPC{npc.Id.Value} stole {loot} from NPC{victim.Id.Value}");
                    break;
                }
            }

            // §105: пока идёт окно умирания, истощение больше НЕ грызёт зоны —
            // часы теперь отсчитывает запас, и вторая шкала поверх него просто
            // ломала бы пол витальных зон каждый тик. Умирающая от кровопотери
            // тем самым получает поблажку по голоду: она и так умирает, и
            // одного обратного отсчёта на тело достаточно.
            if ((starved || parched) && !npc.IsDying)
            {
                // §45 r5: attrition eased 0.03/0.05 -> 0.02/0.035. The 25-day
                // baseline showed every colony losing 1-2 girls to ACUTE
                // starvation episodes (a pinned need grinds a full body in
                // ~220 ticks / 55 real seconds — faster than the recovery loop
                // can respond).
                // Death stays certain for a truly stuck agent; a girl who
                // reaches food/water mid-episode now lives to eat it.
                var damage = starved && parched ? SimBalance.StarveDamageBoth : SimBalance.StarveDamageOne; // §46 difficulty: restored to pre-r5 — safe now that sickness/fire/cold are fixed; at 0.025/0.045 the colony still won 10/12
                foreach (var part in AllBodyParts)
                {
                    npc.Body.Parts[part] = MathUtil.Clamp01(npc.Body.Parts[part] - damage);
                }

                npc.Health = npc.Body.Mean();
                if (npc.Body.VitalDestroyed(out _))
                {
                    // §105: тело сдалось — но не умерло в этот тик. Причина
                    // берётся по тому, чего именно не хватило, потому что она
                    // же выбирает, ЧЕМ её спасать: голодной нужна еда, а не
                    // бинт. Витальную зону здесь не разбирают на голову и
                    // грудь: истощение точит все семь разом, и «выстрел в
                    // голову» от голода был бы бессмыслицей.
                    if (Spec105.DyingEnabled)
                    {
                        // §105.3 r2: attrition subtracts from every body part
                        // at once. When the last small remainder reached zero
                        // on all seven parts in this very pass, Mean() became
                        // zero before EnterDying and its corpse guard rejected
                        // the transition. Keep the entity structurally alive
                        // for the entry call; EnterDying immediately pins the
                        // actual vital parts to BodyFloor and owns the normal
                        // starvation/dehydration window from there.
                        npc.Health = System.Math.Max(npc.Health, Spec105.BodyFloor);
                        MortalityHelpers.EnterDying(world, npc,
                            starved ? DyingCause.Starvation : DyingCause.Dehydration);
                    }
                    else
                    {
                        npc.Health = 0f;
                    }
                }

                DamageReactionSystemHelpers.GrantAdrenaline(world, npc, damage, "Starvation");

                Trace.Emit(world, npc.Id, npc.Health <= 0f ? "StarvedToDeath" : "StarvationDamage",
                    $"Hunger={npc.Needs.Hunger:F2} Thirst={npc.Needs.Thirst:F2} " +
                    $"Damage=-{damage:F2} Health={npc.Health:F2}");
            }
            // Spec 29C.2/19.3C + 40.8B: eat and rest to heal — but only damage
            // NOT held by open wounds. Each wound keeps its Severity "hostage":
            // the zone can regen up to (1 − open wound damage) and no further,
            // so a couple of coconuts never insta-heals a mauling.
            else if (npc.Health < 1f && npc.Needs.Hunger < SimBalance.HealHungerGate)
            {
                // §45 r5: the gate eased 0.5 -> 0.6 — the long-run colony
                // hovers at hunger ~0.5-0.6, so the old gate barely ever
                // opened and bodies never recovered between sickness/cold/
                // hunger episodes.
                // §118.8: the rate itself is day-scale now (1/3000 of a part
                // per slow tick) and rides the SAME rest ladder as the §118
                // recovery channels — a bed doubles it, standing does not.
                var restMultiplier = KenshiMedicalMath.RestHealMultiplier(world, npc);
                foreach (var part in AllBodyParts)
                {
                    if (npc.Body.IsSevered(part)) continue; // §50: severed zones never regen
                    var condition = npc.Body.Condition(part);
                    var heldDamage = WoundMath.OpenWoundDamage(npc, part) +
                        (Spec118.Enabled ? condition.BluntDamage : 0f);
                    var ceiling = MathUtil.Clamp01(1f - heldDamage);
                    if ((!Spec118.Enabled || condition.CriticalTrauma <= 0f) &&
                        npc.Body.Parts[part] < ceiling)
                    {
                        // §76: a tough body knits faster. The CEILING is
                        // untouched — an open wound still caps what can come
                        // back, grit only decides how quickly she gets there.
                        npc.Body.Parts[part] = System.Math.Min(ceiling,
                            npc.Body.Parts[part] +
                            SimBalance.HealthRegenPerTick * restMultiplier *
                            AttributeMath.HealRateMult(npc));
                    }

                    // Spec 44: the dressing (leaf wrap or gauze) comes off once
                    // the zone has healed.
                    if (npc.Body.Parts[part] > 0.7f)
                    {
                        npc.BandagedZones.Remove(part);
                        npc.GauzeZones.Remove(part);
                    }
                }

                npc.Health = npc.Body.Mean();
            }

            // Spec 40.8B: every wound closes on its own clock, PACED BY
            // ACTIVITY — sleeping knits flesh twice as fast, marching halves
            // it. Each healed slice returns its share of the zone's HP, so
            // health comes back exactly as the wounds close, wound by wound.
            if (!Spec118.Enabled && npc.Wounds.Count > 0)
            {
                // §60: a coma knits flesh at the sleeping pace too.
                // §76: Toughness rides on top of the activity pace — the same
                // wound closes sooner on a hardy body.
                //
                // §71.3: ⭐ темп читает НЕ Status, а легаси-латч. Честный статус
                // (Moving на каждом тике трансляции) менял бы экономику ран для
                // всех бегунов разом — а на старой, залипающей семантике
                // оттюнена вся дуга §81→§108: с честным штрафом гопник истекает
                // кровью и умирает (сид 313, тик ~15900), не дожив ~700 тиков
                // до сговора, который раньше выигрывал эту гонку. Подробности и
                // условия снятия — у поля WoundPaceMoving.
                var pace = (npc.Execution.CurrentInteraction == InteractionType.Sleep ||
                    npc.Mind.ComaCause != ComaCause.None ? 2f
                    : npc.Movement.WoundPaceMoving ? 0.5f
                    : 1f) * AttributeMath.HealRateMult(npc);

                for (var wi = npc.Wounds.Count - 1; wi >= 0; wi--)
                {
                    var wound = npc.Wounds[wi];
                    var slice = System.Math.Min(WoundMath.HealPerSlowTick * pace, 1f - wound.Heal01);
                    wound.Heal01 += slice;
                    // §50: the stump wound on a severed zone still clots (heal01
                    // climbs so the bleed eventually stops), but its HP never
                    // returns — the limb is gone, not mending.
                    if (!npc.Body.IsSevered(wound.Zone))
                    {
                        npc.Body.Parts[wound.Zone] = MathUtil.Clamp01(
                            npc.Body.Parts[wound.Zone] + wound.Severity * slice);
                    }

                    if (wound.Heal01 >= 1f)
                    {
                        if (SimTrace.Enabled)
                        {
                            Trace.Debug(world, npc.Id, "WoundHealed",
                                $"{wound.Zone} wound #{wound.Id} closed");
                        }
                        npc.Wounds.RemoveAt(wi);
                    }
                }

                npc.Health = npc.Body.Mean();
            }

            // Spec 40.2/§60: blood at 0 IS death — pin it after every branch
            // above, because the fed-heal and wound-close paths recompute
            // Health from the body parts and would otherwise "resurrect" a
            // bled-out body in the same pass (parts stay > 0 when the death
            // came from the drained blood, not from destroyed zones).
            // §105: пока идёт окно умирания, пин ОТМЕНЁН — иначе он убивал бы
            // её тем же тиком, в котором она упала, и всё окно свелось бы к
            // одному кадру. Обнуляет здоровье теперь только истёкший запас.
            if (npc.Needs.Blood <= 0f && !npc.IsDying)
            {
                if (Spec105.DyingEnabled)
                {
                    MortalityHelpers.EnterDying(world, npc, DyingCause.BloodLoss);
                }
                else
                {
                    npc.Health = 0f;
                }
            }

            // §105: обратный отсчёт — последним, по итогам всего, что этот тик
            // сделал с телом (кровь вытекла или, наоборот, рана закрылась и
            // крови набралось). Здесь же наступает и смерть, когда запас
            // кончился: Health падает в ноль, и свип MobSystem уносит тело.
            MortalityHelpers.TickDying(world, npc);

            // §57.10: умирающая (или разбитая лежащая) в сознании стонет о
            // помощи — после отсчёта, чтобы стонала живая, а не труп этого
            // тика. Кулдаун и все гейты внутри.
            CombatHelpSystem.TryMoanForHelp(world, npc);

            // §60.7: без сознания в глубокой воде — тонет. Стоит ПОСЛЕДНИМ,
            // рядом с TickDying, и по той же причине, что и пин крови выше:
            // ветки fed-heal/закрытия ран пересчитывают Health из зон тела, а
            // у утонувшей зоны целы — смерть, объявленная раньше по проходу,
            // "воскресала" бы тем же тиком.
            TickDrowning(world, npc);

            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "NeedsDecay",
                    $"Hunger={prevHunger:F3}->{npc.Needs.Hunger:F3}(+{HungerRate}) " +
                    $"Energy={prevEnergy:F3}->{npc.Needs.Energy:F3}(-{energyDrain}) " +
                    $"Comfort={prevComfort:F3}->{npc.Needs.Comfort:F3}(-{ComfortRate}) " +
                    $"Social={prevSocial:F3}->{npc.Needs.Social:F3}(-{SocialRate}) " +
                    $"Sweat={sweat:F2}");
            }

            // §50/§41.5: встала за этот тик — дать доиграть клип подъёма,
            // иначе сим повезёт её сразу и ноги поедут по земле (баг #1).
            MortalityHelpers.GrantStandUpGrace(world, npc, wasProne);
        }

        // Spec 40.16: joint-plan advisor trigger. On the rising edge of a
        // colony-wide crisis, consult the advisor (a null-object by default, so
        // this is inert) and trace the onset. Formalizes the trigger + I/O; a
        // host swaps DireStraits.Advisor for an LLM-backed one to act on it.
        var crisis = AI.DireStraits.Assess(world);
        if (crisis is not null && !world.ColonyInDireStraits)
        {
            world.ColonyInDireStraits = true;
            var advice = AI.DireStraits.Advisor.Advise(crisis);
            Trace.EmitSystem(world, "DireStraits",
                $"starving={crisis.StarvingCount} wounded={crisis.WoundedCount}/{crisis.LivingCount}" +
                (string.IsNullOrEmpty(advice) ? "" : $" advice={advice}"));
        }
        else if (crisis is null)
        {
            world.ColonyInDireStraits = false;
        }
    }
}

}
