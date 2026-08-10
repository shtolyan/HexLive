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
        CancelInterruptedRescue(world, npc);
        AbortCore(world, npc, reason, keepCarriedPerson: false);
    }

    /// <summary>§124: новый приказ движения/остановки не роняет тело из рук.
    /// Все владения старого плана освобождаются, но двусторонняя carry-ссылка
    /// остаётся до явного PutDown или опасного прерывания.</summary>
    public static void AbortKeepingCarriedPerson(WorldState world, NPCState npc, string reason)
    {
        CancelInterruptedRescue(world, npc);
        AbortCore(world, npc, reason, keepCarriedPerson: true);
    }

    // §118.4: combat is a temporary interruption, not permission to keep a
    // patient glued to the fighter or to forget her. Put her down before any
    // swing can start, reserve that same patient, then let RescueSystem resume
    // the evacuation once the fight/scene releases the rescuer.
    public static void AbortForCombat(WorldState world, NPCState npc, string reason)
    {
        var remembered = npc.Mind.InterruptedRescuePatientId;
        var dropped = AbortCore(world, npc, reason, keepCarriedPerson: false);
        var resumePatientId = dropped ?? remembered;
        if (resumePatientId is not { } patientId ||
            !world.Entities.Npcs.TryGetValue(patientId, out var patient) ||
            !FactionRelations.AreAllies(npc, patient) ||
            !KenshiRescueMath.NeedsRescue(world, patient))
        {
            CancelInterruptedRescue(world, npc);
            return;
        }

        npc.Mind.InterruptedRescuePatientId = patientId;
        patient.Mind.PendingAidFrom = npc.Id;
        patient.Mind.PendingAidSinceTick = world.Tick;
        Trace.Emit(world, npc.Id, "RescuePausedForCombat",
            $"NPC{patientId.Value} Reason={reason}");
    }

    private static EntityId? AbortCore(
        WorldState world, NPCState npc, string reason, bool keepCarriedPerson)
    {
        var droppedPatientId = keepCarriedPerson
            ? KenshiRescueMath.DetachRescueDestinationForManualCarry(world, npc)
            : KenshiRescueMath.PutDownForPlanInterruption(
                world, npc, $"Plan interrupted: {reason}");

        CraftProjectMath.ReleaseWorker(world, npc);
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
            // §123 player inventory animation keeps the authoritative item in
            // its source list until the final tick so a mid-action save is
            // self-contained. Abort must therefore not duplicate that visual
            // hand reference onto the ground.
            var stillOwned = npc.Mind.CurrentGoal == GoalType.PlayerInventory &&
                (npc.Inventory.Items.Exists(item => ReferenceEquals(item, held)) ||
                 npc.WornItems.Exists(item => ReferenceEquals(item, held)));
            var dropped = stillOwned ? null : ExecutionSystem.DropItemAtFeet(world, npc, held);
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
        npc.Movement.HopArmed = false;
        npc.Movement.HopPathIndex = -1;

        // §21.21B v17: a hop ALREADY IN THE AIR is not interruptible. Killing the
        // window here left her hanging between two levels — position half-way,
        // npc.Tile still the takeoff tile — and since the view draws her at the
        // ground height of npc.Tile, she snapped back onto the ledge she had just
        // jumped off ("спрыгнула, развернулась — телепнуло наверх") or back onto
        // the bank after a dive ("прыгнула в воду без плюха, отшвырнуло назад").
        // The window keeps running (MovementSystem.RunHopWindow is called before
        // any path check), lands her properly, and the new plan starts from solid
        // ground. Only the pre-flight commitment is cancelled, above.
        if (npc.Movement.HopTimer <= 0f)
        {
            npc.Movement.HopCrossed = false;
            npc.Movement.HopLandingIndex = 0;
        }

        Trace.Emit(world, npc.Id, "GoalInterrupted", reason);
        return droppedPatientId;
    }

    private static void CancelInterruptedRescue(WorldState world, NPCState npc)
    {
        if (npc.Mind.InterruptedRescuePatientId is { } patientId &&
            world.Entities.Npcs.TryGetValue(patientId, out var patient) &&
            patient.Mind.PendingAidFrom == npc.Id)
        {
            patient.Mind.PendingAidFrom = null;
        }

        npc.Mind.InterruptedRescuePatientId = null;
    }
}

}
