using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

public sealed partial class ExecutionSystem
{
    private static void RunManualPersonPickup(WorldState world, NPCState carrier)
    {
        // §118.4 r2 (#166): тот же предикат, что принял приказ.
        if (carrier.Plan.TargetAgentId is not { } personId ||
            !KenshiRescueMath.TryGetPerson(world, personId, out var person, out var dead) ||
            !ManualCarryTargets.CanCarry(world, carrier, person, dead))
        {
            PlanInterruption.TryAbort(world, carrier, InterruptionCause.ExecutionFailure, "цель ручного переноса недоступна");
            carrier.Mind.CurrentGoal = GoalType.None;
            return;
        }

        if (carrier.Movement.IsMoving ||
            (carrier.Movement.Status != MovementStatus.Arrived &&
             carrier.Movement.JunctionPath.Count > 0))
        {
            return;
        }

        if (carrier.Movement.Status == MovementStatus.Blocked ||
            (carrier.Plan.TargetJunctionId is { } wanted &&
             carrier.CurrentJunction != wanted))
        {
            PlanInterruption.TryAbort(world, carrier, InterruptionCause.ExecutionFailure, "не удалось подойти к лежащему человеку");
            carrier.Mind.CurrentGoal = GoalType.None;
            return;
        }

        if (carrier.Plan.TargetJunctionId is { } approach)
        {
            SpatialMutations.ReleaseJunctionReservation(world, approach, carrier.Id);
        }

        carrier.Execution.CurrentInteraction = InteractionType.PickUpPerson;
        KenshiRescueMath.BeginManualCarry(world, carrier, person, dead);
        carrier.Execution.CurrentInteraction = null;
        carrier.Execution.Status = ExecutionStatus.None;
        carrier.Plan.Status = PlanStatus.Completed;
        carrier.Plan.Steps.Clear();
        carrier.Plan.TargetAgentId = null;
        carrier.Plan.TargetJunctionId = null;
        carrier.Plan.TargetTile = null;
        carrier.Movement.JunctionPath.Clear();
        carrier.Movement.IsMoving = false;
        carrier.Movement.SetStatus(MovementStatus.Idle);
    }
}

}
