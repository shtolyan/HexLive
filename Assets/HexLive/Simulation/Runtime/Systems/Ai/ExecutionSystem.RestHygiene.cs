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

public sealed partial class ExecutionSystem
{
    // Spec 29G: drive a ground rest plan — walk to the reserved spot (the
    // PathfindingSystem does the walking), then rest in place.
    private static void RunGroundRestPlan(WorldState world, NPCState npc, PlanStep step)
    {
        if (npc.Execution.Status != ExecutionStatus.InProgress)
        {
            if (npc.Movement.IsMoving)
            {
                return;
            }

            if (npc.Movement.Status == MovementStatus.Blocked)
            {
                PlanningSystem.SetGoalCooldown(world, npc, npc.Plan.Goal);
                PlanInterruption.Abort(world, npc, "Ground rest spot unreachable");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            var atTarget = npc.Plan.TargetJunctionId is { } target &&
                npc.CurrentJunction is { } current && current.Equals(target);
            if (!atTarget)
            {
                return;
            }
        }

        if (step.Type == PlanStepType.GroundSit)
        {
            RunGroundRest(world, npc, step, InteractionType.Sit, 70,
                step.TargetJunction is { } lg && PlanningSystem.IsLedgeId(world, lg)
                    ? SimBalance.GroundSitComfortLedge : SimBalance.GroundSitComfort,
                SimBalance.GroundSitEnergy);
        }
        else if (step.Type == PlanStepType.GroundCool)
        {
            // Spec 35.4: dwell in shade/water shedding heat — no comfort/energy
            // gain, the cooling is delivered for free by TemperatureSystem now
            // that she's standing on a genuinely cool tile.
            RunGroundCool(world, npc, step);
        }
        else
        {
            // Sleep restores as well as a bed (a night is a night) — the
            // bed's edge is comfort, not energy. +0.35 energy here produced
            // a poverty trap: 160 naps/soak and no time to live.
            RunGroundRest(world, npc, step, InteractionType.Sleep, 100, 0f, SimBalance.GroundSleepEnergy); // spec 42
        }
    }

    // Spec 29G: rest on the land — a timed in-place interaction with no
    // object. Lying claims the body's footprint so housemates path around.
    private static void RunGroundRest(
        WorldState world, NPCState npc, PlanStep step,
        InteractionType kind, int durationTicks, float comfort, float energy)
    {
        if (npc.Execution.Status == ExecutionStatus.None)
        {
            if (step.TargetJunction is { } reserved &&
                !SpatialMutations.TryReserveJunction(
                    world, reserved, npc.Id, world.Tick, durationTicks + 8))
            {
                PlanInterruption.Abort(world, npc, "Ground rest edge was claimed");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = kind;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + durationTicks;

            if (step.TargetJunction is { } spot)
            {
                SpatialMutations.OccupyJunction(world, spot, npc.Id);

                if (kind == InteractionType.Sit && world.Junctions.Items.TryGetValue(spot, out var ledge) &&
                    PlanningSystem.TryGetEdgeSeatGeometry(
                        world, ledge, waterOnly: false, out var standTile, out var facing))
                {
                    PlaceAtEdge(world, npc, ledge, standTile, facing);
                }

                if (kind == InteractionType.Sleep)
                {
                    ClaimLyingFootprint(world, npc, spot);
                }
            }

            Trace.Emit(world, npc.Id, "InteractionStarted",
                $"{kind} on the ground Duration={durationTicks}ticks");
            return;
        }

        if (npc.Execution.Status != ExecutionStatus.InProgress)
        {
            return;
        }

        // Spec 29C.9: comfort/energy recover gradually while she rests — the
        // whole point of "you can watch it fill", not a jump on standing up.
        var restShare = durationTicks > 0 ? 1f / durationTicks : 1f;
        npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort + comfort * restShare);
        npc.Needs.Energy = MathUtil.Clamp01(npc.Needs.Energy + energy * restShare);

        var interruptedSleep = kind == InteractionType.Sleep && HasSleepInterrupt(world, npc);
        if (!interruptedSleep && npc.Execution.EndTick - world.Tick > 0)
        {
            return;
        }

        if (interruptedSleep)
        {
            Trace.Emit(world, npc.Id, "SleepInterrupted",
                $"Hunger={npc.Needs.Hunger:F2} Thirst={npc.Needs.Thirst:F2} " +
                $"Danger={npc.Memory.Dangers.Count}");
        }

        // Spec §49: sleep the night in ONE continuous lie. Instead of ending the
        // block, standing (wake-grace + get-up clip), re-planning a spot and
        // dropping back down — the "empty get-up" churn that was 57% of night
        // get-ups — re-arm the block in place, holding the footprint claim. She
        // only truly wakes when rested enough, dawn breaks, or a real need
        // (hunger/thirst/cold/danger) crosses its threshold and the decision
        // system takes over.
        if (kind == InteractionType.Sleep && ShouldKeepSleeping(world, npc))
        {
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + durationTicks;
            Trace.Emit(world, npc.Id, "SleepContinued",
                $"Energy={npc.Needs.Energy:F2} Comfort={npc.Needs.Comfort:F2}");
            return;
        }

        ReleaseClaims(world, npc);
        if (step.TargetJunction is { } done)
        {
            SpatialMutations.FreeJunction(world, done, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, done, npc.Id);
        }

        Trace.Emit(world, npc.Id, kind == InteractionType.Sleep ? "GroundSleptWell" : "GroundSatDown",
            $"Comfort+{comfort:F2} Energy+{energy:F2}");

        // Spec 41.5: wake up standing still for a beat — no sprinting off
        // the grass; the get-up clip plays out during the grace.
        if (kind == InteractionType.Sleep)
        {
            npc.Mind.WakeGraceUntilTick = world.Tick + 12;
        }

        if (kind == InteractionType.Sit)
        {
            npc.Mind.Cooldowns.Add(new GoalCooldown
            {
                Goal = GoalType.Sit,
                EndTick = world.Tick + 240
            });
        }

        // Canonical cycle reset (same as InteractionCompleted): Execution
        // back to None or the next interaction's start gate never opens.
        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Plan.TargetItemDefinitionId = null;
        npc.Plan.TargetAgentId = null;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;

        Trace.Emit(world, npc.Id, "CycleReset",
            "Goal->None Plan->Completed Execution->Cleared (ground rest done)");
    }

    // Spec §49: should a finished sleep block re-arm in place (keep lying)
    // rather than stand and re-plan? Yes while she is still tired (nearly full
    // energy at night, or genuinely spent by day) AND no real need has crossed
    // its action threshold — the same thresholds at which Eat/Drink/Dress
    // become attractive, so she wakes exactly when there is something to do.
    private static float SleepWakeEnergyDay => SimBalance.SleepEnergyThreshold;

    private static float SleepInterruptHunger => SimBalance.SleepInterruptHunger;

    private static float SleepInterruptThirst => SimBalance.SleepInterruptThirst;

    // NOTE: cold is deliberately NOT a wake trigger — mild cold at night is the
    // norm and she usually can't fix it, so waking just produced the "empty
    // get-up" churn; sleeping through it is what a real body does (§49.1).
    private static bool ShouldKeepSleeping(WorldState world, NPCState npc)
    {
        if (!Spec49.Rearm)
        {
            return false;
        }

        // A real, actionable need or a threat ends the sleep — then the decision
        // system takes over. Everything else: keep lying. NOTE: cold is NOT a
        // wake trigger — a near-naked girl on a 6° night sits at max thermal
        // discomfort she usually can't fix, so waking her only produced the
        // "empty get-up" churn; the cold HP hit lands whether she's up or lying,
        // and lying still conserves. (A fire she could tend is a daytime chore.)
        if (HasSleepInterrupt(world, npc))
        {
            return false;
        }

        // Spec §49: sleep THROUGH the night in one lie (the user's ask — "let
        // them sleep more") — no energy cap after dark. By day, only nap while
        // genuinely tired.
        var night = world.Environment.Phase is DayPhase.Night or DayPhase.Evening;
        return night || npc.Needs.Energy < SleepWakeEnergyDay;
    }

    // §49-parity: the DECISION layer reads this too — going to sleep while an
    // interrupt condition is already true produced the lie-down/stand-up loop
    // (Molly, thirst 0.79 ≥ 0.6: the first sleep tick woke her, the auction
    // put her right back to bed, forever).
    internal static bool HasSleepInterrupt(WorldState world, NPCState npc) =>
        world.Tick < npc.Mind.AdrenalineUntilTick ||
        npc.Memory.Dangers.Count > 0 ||
        npc.Needs.Hunger >= SleepInterruptHunger ||
        npc.Needs.Thirst >= SleepInterruptThirst;

    // Spec 35.4: dwell in the shade / shallows shedding heat. This is the
    // cool-off twin of RunGroundRest — a timed in-place interaction with no
    // object and no comfort/energy payoff; the cooling itself is delivered by
    // TemperatureSystem because the plan parked her on a genuinely cool tile
    // (shaded or water). At the end of each beat it re-arms in place (like the
    // sleep re-arm) until she has actually cooled, a more urgent need crosses,
    // or the safety cap trips — instead of completing→None and re-winning the
    // goal at zero margin every tick (the old None→CoolOff churn, ~40% of all).
    private static void RunGroundCool(WorldState world, NPCState npc, PlanStep step)
    {
        var bathing = npc.Plan.Goal == GoalType.Bathe;
        if (npc.Execution.Status == ExecutionStatus.None)
        {
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.CoolOff;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + Spec49.CoolOffDwellTicks;

            if (step.TargetJunction is { } spot)
            {
                SpatialMutations.OccupyJunction(world, spot, npc.Id);
            }

            Trace.Emit(world, npc.Id, "InteractionStarted",
                $"{(bathing ? "Bathe" : "CoolOff")} Duration={Spec49.CoolOffDwellTicks}ticks");
            return;
        }

        if (npc.Execution.Status != ExecutionStatus.InProgress)
        {
            return;
        }

        if (npc.Execution.EndTick - world.Tick > 0)
        {
            return;
        }

        // Re-arm the dwell in place (hold the junction occupancy + reservation)
        // while still hot and nothing more urgent calls — bounded by CoolOffMaxRearms
        // so a fallback tile that never actually cools can't freeze her here forever.
        if (Spec49.CoolRearm &&
            npc.Mind.CoolRearmCount < Spec49.CoolOffMaxRearms &&
            (bathing ? ShouldKeepBathing(npc) : ShouldKeepCooling(world, npc)))
        {
            npc.Mind.CoolRearmCount++;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + Spec49.CoolOffDwellTicks;
            Trace.Emit(world, npc.Id, bathing ? "BatheContinued" : "CoolContinued",
                $"Rearm={npc.Mind.CoolRearmCount} Hygiene={npc.Needs.Hygiene:F2} " +
                $"ClothingDirt={EquipmentMath.AverageDirtiness(npc):F2}");
            return;
        }

        ReleaseClaims(world, npc);
        if (step.TargetJunction is { } done)
        {
            SpatialMutations.FreeJunction(world, done, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, done, npc.Id);
        }

        Trace.Emit(world, npc.Id, bathing ? "Bathed" : "CooledOff",
            $"Hygiene={npc.Needs.Hygiene:F2} ClothingDirt={EquipmentMath.AverageDirtiness(npc):F2} " +
            $"Rearms={npc.Mind.CoolRearmCount}");

        // A short refractory window so she doesn't instantly re-select CoolOff even
        // if discomfort still hovers just under the clear edge (mirrors Sit's 240t).
        npc.Mind.Cooldowns.Add(new GoalCooldown
        {
            Goal = bathing ? GoalType.Bathe : GoalType.CoolOff,
            EndTick = world.Tick + SimBalance.CoolOffSettleTicks
        });

        // Canonical cycle reset (same as RunGroundRest / InteractionCompleted).
        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Plan.TargetItemDefinitionId = null;
        npc.Plan.TargetAgentId = null;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Mind.CoolRearmCount = 0;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;

        Trace.Emit(world, npc.Id, "CycleReset",
            "Goal->None Plan->Completed Execution->Cleared (cool-off done)");
    }

    private static bool ShouldKeepBathing(NPCState npc) =>
        npc.Needs.Hygiene < 0.95f || EquipmentMath.AverageDirtiness(npc) > 0.05f;

    private static void RunPrepareBathe(WorldState world, NPCState npc, PlanStep step)
    {
        if (npc.Movement.IsMoving || npc.CurrentJunction is not { } current ||
            step.TargetJunction is not { } shore || !current.Equals(shore))
        {
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.InProgress &&
            npc.Execution.CurrentInteraction == InteractionType.Undress)
        {
            var garment = npc.Execution.HeldGarment;
            if (garment is null && npc.Plan.TargetItemDefinitionId is { } definitionId)
            {
                for (var i = npc.WornItems.Count - 1; i >= 0; i--)
                {
                    if (npc.WornItems[i].DefinitionId == definitionId)
                    {
                        garment = npc.WornItems[i];
                        break;
                    }
                }
            }

            if (garment is null)
            {
                PlanInterruption.Abort(world, npc, "Bathe: active garment disappeared");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            var total = npc.Execution.EndTick - npc.Execution.StartTick;
            var progress = total > 0 ? (float)(world.Tick - npc.Execution.StartTick) / total : 1f;
            if (npc.Execution.HeldGarment is null && progress >= WardrobeHandoffFraction)
            {
                npc.WornItems.Remove(garment);
                npc.Execution.HeldGarment = garment;
                EquipmentMath.Recalculate(world, npc);
            }

            if (world.Tick < npc.Execution.EndTick)
            {
                return;
            }

            npc.WornItems.Remove(garment);
            DropGarmentWithContents(world, npc, garment);
            npc.Execution.HeldGarment = null;
            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.CurrentInteraction = null;
            npc.Execution.StartTick = 0;
            npc.Execution.EndTick = 0;
            npc.Plan.TargetItemDefinitionId = null;
            EquipmentMath.Recalculate(world, npc);
            return;
        }

        if (npc.WornItems.Count > 0)
        {
            var garment = npc.WornItems[npc.WornItems.Count - 1];
            npc.Plan.TargetItemDefinitionId = garment.DefinitionId;
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Undress;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + UndressDurationTicks;
            npc.Execution.HeldGarment = null;
            return;
        }

        Junction swim = null;
        var bestDistance = float.MaxValue;
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                !world.Tiles.Items.TryGetValue(junction.Tiles[0], out var tile) ||
                !tile.Flags.HasFlag(TileFlags.Water) ||
                !Connectivity.Reachable(world, shore, junction.Id))
            {
                continue;
            }

            var distance = HexSpatialMath.Distance(junction.WorldPosition, npc.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                swim = junction;
            }
        }

        if (swim is null)
        {
            PlanInterruption.Abort(world, npc, "Bathe: no reachable water junction");
            npc.Mind.CurrentGoal = GoalType.None;
            return;
        }

        SpatialMutations.ReleaseJunctionReservation(world, shore, npc.Id);
        npc.Plan.TargetJunctionId = swim.Id;
        npc.Plan.TargetTile = swim.Tiles[0];
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = swim.Id });
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.SwimBathe, TargetJunction = swim.Id });
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;
        npc.Movement.IsMoving = false;
        Trace.Emit(world, npc.Id, "BatheReady", $"Naked; swimming to {swim.Id.Value}");
    }

    private static void RunSwimBathe(WorldState world, NPCState npc, PlanStep step)
    {
        if (npc.Movement.IsMoving || npc.CurrentJunction is not { } current ||
            step.TargetJunction is not { } target || !current.Equals(target))
        {
            return;
        }

        if (npc.WornItems.Count > 0)
        {
            PlanInterruption.Abort(world, npc, "Bathe requires complete undressing");
            npc.Mind.CurrentGoal = GoalType.None;
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.CoolOff;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + SimBalance.BatheDurationTicks;
            Trace.Emit(world, npc.Id, "BatheStarted",
                $"Duration={SimBalance.BatheDurationTicks} ticks (one game hour)");
            return;
        }

        npc.Needs.Hygiene = MathUtil.Clamp01(npc.Needs.Hygiene +
            1f / SimBalance.BatheDurationTicks);
        if (world.Tick < npc.Execution.EndTick)
        {
            return;
        }

        npc.Needs.Hygiene = 1f;
        FinishPersonalCare(world, npc, target, GoalType.Bathe, "Bathed");
    }

    // §40.6 r2 (laundry-in-hand): the piece is washed IN THE HAND, never in
    // place. A worn source plays the doff beat first (off the body into the
    // hand, warmth drops); a ground source is picked up off the shore (the
    // world object despawns into the hand, pockets carried through). The held
    // instance loses dirt/blood over the wash window and is laid back down at
    // the edge fully clean and soaked.
    private static void RunWashClothes(WorldState world, NPCState npc, PlanStep step)
    {
        if (npc.Movement.IsMoving || npc.CurrentJunction is not { } current ||
            step.TargetJunction is not { } target || !current.Equals(target))
        {
            return;
        }

        if (!world.Junctions.Items.TryGetValue(target, out var edge) ||
            !PlanningSystem.TryGetEdgeSeatGeometry(
                world, edge, waterOnly: true, out var standTile, out var facing))
        {
            SpatialMutations.FreeJunction(world, target, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, target, npc.Id);
            PlanInterruption.Abort(world, npc, "WashClothes edge disappeared");
            npc.Mind.CurrentGoal = GoalType.None;
            return;
        }

        // Hold the exact edge midpoint and its water-facing normal throughout
        // the gathering clip; other systems cannot slowly turn the washer away.
        PlaceAtEdge(world, npc, edge, standTile, facing);

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            if (!SpatialMutations.TryReserveJunction(world, target, npc.Id, world.Tick,
                    UndressDurationTicks + SimBalance.WashClothesDurationTicks + 8))
            {
                PlanInterruption.Abort(world, npc, "WashClothes edge was claimed");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            SpatialMutations.OccupyJunction(world, target, npc.Id);

            if (step.TargetObject is { } objectId)
            {
                // Ground source: up off the shore and into the hand.
                if (!TryPickGarmentIntoHand(world, npc, objectId))
                {
                    SpatialMutations.FreeJunction(world, target, npc.Id);
                    PlanInterruption.Abort(world, npc, "WashClothes garment disappeared");
                    npc.Mind.CurrentGoal = GoalType.None;
                    return;
                }

                StartWashBeat(world, npc);
                return;
            }

            // Worn source: the doff beat first — same two-beat undress window
            // the wardrobe verbs use, so the view shows her taking it off.
            if (npc.Plan.TargetItemDefinitionId is not { } wornId ||
                !npc.WornItems.Contains(wornId))
            {
                SpatialMutations.FreeJunction(world, target, npc.Id);
                PlanInterruption.Abort(world, npc, "WashClothes worn piece disappeared");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Undress;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + UndressDurationTicks;
            npc.Execution.HeldGarment = null;
            npc.Execution.HeldGarmentContents.Clear();
            return;
        }

        if (npc.Execution.CurrentInteraction == InteractionType.Undress)
        {
            var itemId = npc.Plan.TargetItemDefinitionId;
            var garment = npc.Execution.HeldGarment;
            if (garment is null && itemId is not null)
            {
                garment = npc.WornItems.Find(i => i.DefinitionId == itemId);
            }

            if (garment is null)
            {
                SpatialMutations.FreeJunction(world, target, npc.Id);
                PlanInterruption.Abort(world, npc, "WashClothes worn piece disappeared");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            var total = npc.Execution.EndTick - npc.Execution.StartTick;
            var progress = total > 0 ? (float)(world.Tick - npc.Execution.StartTick) / total : 1f;
            if (npc.Execution.HeldGarment is null && progress >= WardrobeHandoffFraction)
            {
                npc.WornItems.Remove(garment);
                npc.Execution.HeldGarment = garment;
                EquipmentMath.Recalculate(world, npc);
                Trace.Emit(world, npc.Id, "GarmentInHand",
                    $"Wash {garment.DefinitionId} doffed to hand " +
                    $"Warmth={npc.EquippedWarmth:F2} Armor={npc.EquippedArmor:F2}");
            }

            if (world.Tick < npc.Execution.EndTick)
            {
                return;
            }

            if (npc.Execution.HeldGarment is null)
            {
                npc.WornItems.Remove(garment);
                npc.Execution.HeldGarment = garment;
                EquipmentMath.Recalculate(world, npc);
            }

            StartWashBeat(world, npc);
            return;
        }

        if (npc.Execution.HeldGarment is not { } held)
        {
            SpatialMutations.FreeJunction(world, target, npc.Id);
            PlanInterruption.Abort(world, npc, "WashClothes lost the held garment");
            npc.Mind.CurrentGoal = GoalType.None;
            return;
        }

        // Dirtiness is the combined contamination score. Bloodiness remains a
        // separate visual layer, but fades over the same washing progress.
        var remainingTicks = System.Math.Max(0, npc.Execution.EndTick - world.Tick);
        var retainedContamination = remainingTicks / (remainingTicks + 1f);
        held.Dirtiness = MathUtil.Clamp01(held.Dirtiness * retainedContamination);
        held.Bloodiness = MathUtil.Clamp01(held.Bloodiness * retainedContamination);
        held.Wetness = 1f;
        if (world.Tick < npc.Execution.EndTick)
        {
            return;
        }

        held.Dirtiness = 0f;
        held.Bloodiness = 0f;
        held.Wetness = 1f;
        var laid = DropItemAtFeet(world, npc, held);
        if (laid != null && npc.Execution.HeldGarmentContents.Count > 0)
        {
            laid.Contents.AddRange(npc.Execution.HeldGarmentContents);
        }

        npc.Execution.HeldGarmentContents.Clear();
        npc.Execution.HeldGarment = null;
        SpatialMutations.FreeJunction(world, target, npc.Id);
        // Jul 2026: the TYPE must stay the constant "ClothesWashed" — the
        // garment id used to be interpolated into it, so every wash produced
        // a unique event type that no whitelist/counter could match.
        FinishPersonalCare(world, npc, step.TargetJunction, GoalType.WashClothes,
            "ClothesWashed", $"{held.DefinitionId} (fully wet)");
    }

    // §40.6 r2: lift a ground garment into the washer's hand — the world
    // object despawns for the duration; its pocket contents ride along in the
    // execution state and are restored when the piece is laid back down.
    private static bool TryPickGarmentIntoHand(WorldState world, NPCState npc, ObjectId objectId)
    {
        if (!world.Entities.Objects.TryGetValue(objectId, out var garment))
        {
            return false;
        }

        var held = new ItemInstance(garment.DefinitionId)
        {
            Wetness = garment.Wetness,
            Durability = garment.Durability,
            Dirtiness = garment.Dirtiness,
            Bloodiness = garment.Bloodiness,
            ResourceAmount = garment.ResourceAmount
        };
        npc.Execution.HeldGarmentContents.Clear();
        npc.Execution.HeldGarmentContents.AddRange(garment.Contents);
        WorldObjectMutations.DespawnObject(world, objectId);
        npc.Plan.TargetObjectId = null;
        npc.Execution.HeldGarment = held;
        Trace.Emit(world, npc.Id, "GarmentInHand",
            $"Wash {held.DefinitionId} picked up (Dirt={held.Dirtiness:F2} Blood={held.Bloodiness:F2})");
        return true;
    }

    private static void StartWashBeat(WorldState world, NPCState npc)
    {
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.CurrentInteraction = InteractionType.WashClothes;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = world.Tick;
        npc.Execution.EndTick = world.Tick + SimBalance.WashClothesDurationTicks;
        if (npc.Execution.HeldGarment is { } held)
        {
            held.Wetness = 1f;
        }
    }

    private static void FinishPersonalCare(WorldState world, NPCState npc, JunctionId? junction,
        GoalType goal, string trace, string detail = null)
    {
        if (junction is { } occupied)
        {
            SpatialMutations.ReleaseJunctionReservation(world, occupied, npc.Id);
        }

        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Plan.TargetItemDefinitionId = null;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Mind.Cooldowns.Add(new GoalCooldown { Goal = goal, EndTick = world.Tick + 40 });
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;
        Trace.Emit(world, npc.Id, trace,
            $"{(detail is null ? string.Empty : detail + " ")}Hygiene={npc.Needs.Hygiene:F2}");
    }

    // Spec 35.4: should the finished cool-off beat re-arm in place? Yes while she
    // is still hot AND no more-urgent need/threat has crossed its threshold —
    // reusing the sleep-interrupt thresholds so she leaves the shade exactly when
    // there's something better to do. Stops once cooled below the clear edge.
    private static bool ShouldKeepCooling(WorldState world, NPCState npc)
    {
        if (!Spec49.CoolRearm)
        {
            return false;
        }

        // A real, actionable need or a threat ends the dwell — the decision system
        // then takes over (she'll re-pick CoolOff only if still overheated and the
        // settle cooldown has lapsed).
        if (npc.Memory.Dangers.Count > 0 ||
            npc.Needs.Hunger >= SleepInterruptHunger ||
            npc.Needs.Thirst >= SleepInterruptThirst)
        {
            return false;
        }

        // Cooled enough: both the heat discomfort and the sun-exposure meter have
        // fallen below their clear edges (hysteresis vs the 0.35 entry).
        if (npc.Needs.ThermalDiscomfort < SimBalance.CoolOffClearThreshold &&
            npc.SunExposure < SimBalance.CoolOffSunClear)
        {
            return false;
        }

        return true;
    }
}

}
