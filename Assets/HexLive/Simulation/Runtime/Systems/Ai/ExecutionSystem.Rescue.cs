using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;

namespace HexLive.Simulation.Runtime
{

public sealed partial class ExecutionSystem
{
    private static void RunRescue(WorldState world, NPCState helper)
    {
        // #99: RescueSystem auctions on Medium, while an already-authored plan
        // can execute on Fast (including immediately after loading a save).
        // Re-check the posture here so a crying/sleeping carrier cannot pick a
        // patient up during that layer gap. DropSafely also tears down an
        // already established carry before any movement step is consumed.
        if (helper.IsLyingDown(world.Tick))
        {
            KenshiRescueMath.DropSafely(world, helper, "carrier is lying down");
            return;
        }

        if (helper.Plan.TargetAgentId is not { } patientId ||
            !world.Entities.Npcs.TryGetValue(patientId, out var patient) ||
            !FactionRelations.AreAllies(helper, patient))
        {
            KenshiRescueMath.DropSafely(world, helper, "patient missing or hostile");
            return;
        }

        if (!helper.IsCarryingPerson &&
            helper.Execution.Status == ExecutionStatus.None &&
            helper.Movement.IsMoving && helper.Movement.BlockedWaitTicks >= 40)
        {
            PlanningSystem.SetGoalCooldown(world, helper, GoalType.Rescue);
            PlanInterruption.Abort(world, helper,
                $"patient approach occupied too long (Junction={helper.Plan.TargetJunctionId?.Value.ToString() ?? "-"})");
            helper.Mind.CurrentGoal = GoalType.None;
            patient.Mind.PendingAidFrom = null;
            return;
        }

        if (helper.IsCarryingPerson)
        {
            if (helper.Movement.IsMoving)
            {
                return;
            }

            if (helper.Movement.Status == MovementStatus.Blocked)
            {
                // PathfindingSystem owns a four-attempt transient retry budget.
                // Its first miss sets Blocked, but that is not yet a lost route:
                // dropping here turned rescue into pick-up/drop every tick and
                // prevented attempts 2..4 from ever happening. On the terminal
                // miss PathfindingSystem calls PlanInterruption.Abort, whose
                // carry teardown safely puts the patient down.
                return;
            }

            if (helper.Plan.TargetJunctionId is { } wanted && helper.CurrentJunction != wanted)
            {
                return;
            }

            helper.Execution.CurrentInteraction = InteractionType.PutInBed;
            KenshiRescueMath.PutDownAtDestination(world, helper, patient);
            return;
        }

        if (!KenshiRescueMath.NeedsRescue(world, patient))
        {
            PlanInterruption.Abort(world, helper, "patient recovered before pickup");
            helper.Mind.CurrentGoal = GoalType.None;
            patient.Mind.PendingAidFrom = null;
            return;
        }

        if (helper.Movement.IsMoving ||
            (helper.Movement.Status != MovementStatus.Arrived &&
             helper.Movement.JunctionPath.Count > 0))
        {
            return;
        }

        if (helper.Movement.Status == MovementStatus.Blocked)
        {
            PlanningSystem.SetGoalCooldown(world, helper, GoalType.Rescue);
            PlanInterruption.Abort(world, helper, "patient approach blocked");
            helper.Mind.CurrentGoal = GoalType.None;
            patient.Mind.PendingAidFrom = null;
            return;
        }

        if (helper.Execution.Status == ExecutionStatus.InProgress &&
            helper.Execution.CurrentInteraction == InteractionType.TreatOther)
        {
            if (world.Tick < helper.Execution.EndTick)
            {
                return;
            }

            if (!AidSupply.TrySpend(world, helper, AidKind.Treat, out var dressing))
            {
                helper.Execution.Status = ExecutionStatus.None;
                helper.Execution.CurrentInteraction = null;
            }
            else
            {
                WoundMath.StabilizeMostDangerous(patient, dressing.Herbal, out _);
                SkillTrace.Award(world, helper, InteractionType.TreatOther,
                    System.Math.Max(1, helper.Execution.EndTick - helper.Execution.StartTick));
                helper.Execution.Status = ExecutionStatus.None;
                helper.Execution.CurrentInteraction = null;
                Trace.Emit(world, helper.Id, "RescueBandaged",
                    $"NPC{patient.Id.Value} before pickup");
            }
        }

        var immediateEvacuation = KenshiRescueMath.NeedsImmediateEvacuation(world, patient);
        if (!immediateEvacuation && KenshiRescueMath.HasOpenBleeding(patient) &&
            AidSupply.Has(world, helper, AidKind.Treat))
        {
            helper.Execution.Status = ExecutionStatus.InProgress;
            helper.Execution.CurrentInteraction = InteractionType.TreatOther;
            helper.Execution.StartTick = world.Tick;
            helper.Execution.EndTick = world.Tick + WoundMath.BandageTicks(helper);
            return;
        }

        if (!KenshiRescueMath.TryFindDestination(
                world, helper, patient, out var destination, out var destinationJunction,
                out var destinationTile, out var route))
        {
            PlanningSystem.SetGoalCooldown(world, helper, GoalType.Rescue);
            PlanInterruption.Abort(world, helper,
                "no safe bed or camp ground route " +
                $"(searches={KenshiRescueMath.DestinationPathSearchesLastCall})");
            helper.Mind.CurrentGoal = GoalType.None;
            patient.Mind.PendingAidFrom = null;
            return;
        }

        helper.Execution.CurrentInteraction = InteractionType.PickUpPerson;
        KenshiRescueMath.BeginCarry(
            world, helper, patient, destination, destinationJunction, destinationTile, route);
        helper.Execution.CurrentInteraction = null;
    }
}

}
