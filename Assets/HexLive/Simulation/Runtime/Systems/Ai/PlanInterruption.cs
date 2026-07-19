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

// Spec 23.17 interrupt semantics: abort an active plan cleanly, releasing
// everything the plan owns (object occupancy, junction occupancy/reservation)
// so the next decision pass can replan without leaks.
public static class PlanInterruption
{
    public static void Abort(WorldState world, NPCState npc, string reason)
    {
        ExecutionSystem.ReleaseClaims(world, npc);
        if (npc.Execution.Status == ExecutionStatus.InProgress &&
            npc.Plan.TargetObjectId is { } objId &&
            world.Entities.Objects.TryGetValue(objId, out var worldObject) &&
            worldObject.CurrentUser == npc.Id)
        {
            worldObject.IsOccupied = false;
            worldObject.CurrentUser = null;
        }

        // Release a talk invitation this plan placed on its target (spec 28.8).
        if (npc.Plan.TargetAgentId is { } invitedId &&
            world.Entities.Npcs.TryGetValue(invitedId, out var invited) &&
            invited.Mind.PendingTalkFrom is { } inviter && inviter.Equals(npc.Id))
        {
            invited.Mind.PendingTalkFrom = null;
        }

        if (npc.Plan.TargetJunctionId is { } jId)
        {
            SpatialMutations.FreeJunction(world, jId, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, jId, npc.Id);
        }

        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;

        npc.Plan.Status = PlanStatus.Invalid;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Plan.TargetItemDefinitionId = null;
        npc.Plan.TargetAgentId = null;

        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;
        npc.Movement.IsMoving = false;
        npc.Movement.Status = MovementStatus.Idle;
        npc.Movement.StopReason = reason;
        npc.Movement.ClimbPauseTimer = 0f;
        npc.Movement.HopTimer = 0f;
        npc.Movement.HopArmed = false;
        npc.Movement.HopCrossed = false;
        npc.Movement.HopPathIndex = -1;
        npc.Movement.HopLandingIndex = 0;

        Trace.Emit(world, npc.Id, "GoalInterrupted", reason);
    }
}

}
