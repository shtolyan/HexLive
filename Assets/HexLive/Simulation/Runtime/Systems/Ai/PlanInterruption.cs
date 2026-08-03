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

        // §84: the craft layout claims its pieces (ground-claimed fiber and
        // pack-laid inputs alike are marked occupied against mid-work theft) —
        // an aborted craft leaves them LYING, takeable by anyone again.
        foreach (var laidId in npc.Execution.CraftLayout)
        {
            if (world.Entities.Objects.TryGetValue(laidId, out var laidPiece) &&
                laidPiece.CurrentUser == npc.Id)
            {
                laidPiece.IsOccupied = false;
                laidPiece.CurrentUser = null;
            }
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

        // §40.6 r4: multi-leg plans hold reservations beyond the current walk
        // target (the wash edge stays reserved while she fetches the pile) —
        // release everything the remaining steps point at, owner-guarded.
        foreach (var planStep in npc.Plan.Steps)
        {
            if (planStep.TargetJunction is { } stepJunction)
            {
                SpatialMutations.ReleaseJunctionReservation(world, stepJunction, npc.Id);
            }
        }

        // A garment mid-carry (doffed for an undress, or picked up for a wash)
        // must not vanish with the plan — lay it at her feet, pockets intact.
        if (npc.Execution.HeldGarment is { } held)
        {
            var dropped = ExecutionSystem.DropItemAtFeet(world, npc, held);
            if (dropped != null && npc.Execution.HeldGarmentContents.Count > 0)
            {
                dropped.Contents.AddRange(npc.Execution.HeldGarmentContents);
            }

            npc.Execution.HeldGarment = null;
        }

        npc.Execution.HeldGarmentContents.Clear();

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
        npc.Movement.SetStatus(MovementStatus.Idle);
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
