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
    private static void RunPlayerInventoryTransfer(WorldState world, NPCState looter)
    {
        // §128 r2 (#164): тот же предикат, что принял приказ, — иначе «разрешили
        // спящих» дошло бы только до половины пути.
        if (looter.Plan.Steps.Count == 0 ||
            looter.Plan.TargetAgentId is not { } otherId ||
            !PlayerLootTargets.TryResolve(
                world, looter, otherId, out var other, out var carriedBySelf))
        {
            FailPlayerInventoryTransfer(world, looter, "PersonNotAvailable");
            return;
        }

        if (looter.Movement.IsMoving ||
            (looter.Movement.Status != MovementStatus.Arrived &&
             looter.Movement.JunctionPath.Count > 0))
        {
            return;
        }

        // §128: тело в руках — дистанция ноль по построению, станция у ног
        // неопределима (у несомого нет CurrentJunction).
        if (!carriedBySelf)
        {
            var transferSlot = LyingStations.SlotFor(world, looter, other);
            if (looter.Movement.Status == MovementStatus.Blocked ||
                !InteractionReach.CheckPersonStart(
                    world, looter, other, LyingStations.Point(other, transferSlot),
                    LyingStations.Reach(transferSlot),
                    $"Player inventory transfer with NPC{other.Id.Value}"))
            {
                FailPlayerInventoryTransfer(world, looter, "OutOfReach");
                return;
            }
        }

        // Like manual person pickup, the final action owns the whole plan while
        // an optional MoveToJunction remains at index zero.
        var step = looter.Plan.Steps[^1];
        PlayerInventoryTransferMath.UnpackCursor(
            step.TimeoutEndTick ?? 0, out var index, out var count);
        var take = step.Type is PlanStepType.PlayerTakeCarried or
            PlanStepType.PlayerTakeWorn;
        var itemSource = step.Type is PlanStepType.PlayerTakeWorn or
            PlanStepType.PlayerGiveWorn
                ? InventoryItemSource.Worn
                : InventoryItemSource.Carried;
        var source = take ? other : looter;
        var destination = take ? looter : other;
        var itemRef = new InventoryItemRef(
            itemSource, index, looter.Plan.TargetItemDefinitionId ?? string.Empty);

        if (!PlayerInventoryTransferMath.TryResolveTransfer(
                world, source, itemRef, count, out var moving, out var contents))
        {
            FailPlayerInventoryTransfer(world, looter, "StaleItem");
            return;
        }

        if (!PlayerInventoryTransferMath.FitsAfter(
                world, source, destination, itemRef, count))
        {
            FailPlayerInventoryTransfer(world, looter, "InsufficientSpace");
            return;
        }

        PlayerInventoryTransferMath.MoveResolved(
            world, source, destination, itemRef, moving, contents);

        if (take && FactionRelations.AreHostile(looter.Faction, other.Faction))
        {
            CombatHelpSystem.RallyLootWitnesses(world, other, looter.Id);
            SocialCueSignals.StampItem(
                world, looter, "LootHelplessTook", itemRef.ExpectedDefinitionId);
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, looter.Id, "PlayerInventoryTransferred",
                $"Direction={(take ? "Take" : "Give")} Other=NPC{other.Id.Value} " +
                $"Source={itemSource} Index={index} Count={moving.Count} " +
                $"Contents={contents.Count} " +
                $"Def={itemRef.ExpectedDefinitionId}");
        }

        FinishPlayerInventoryTransfer(world, looter, PlanStatus.Completed);
    }

    private static void FailPlayerInventoryTransfer(
        WorldState world, NPCState looter, string reason)
    {
        Trace.Emit(world, looter.Id, "ManualOrderRejected",
            $"Order=TransferInventory Reason={reason}");
        FinishPlayerInventoryTransfer(world, looter, PlanStatus.Failed);
    }

    private static void FinishPlayerInventoryTransfer(
        WorldState world, NPCState looter, PlanStatus status)
    {
        if (looter.Plan.TargetJunctionId is { } approach)
        {
            SpatialMutations.ReleaseJunctionReservation(world, approach, looter.Id);
        }

        looter.Execution.Status = ExecutionStatus.None;
        looter.Execution.CurrentInteraction = null;
        looter.Execution.TargetObject = null;
        looter.Plan.Status = status;
        looter.Plan.Steps.Clear();
        looter.Plan.TargetAgentId = null;
        looter.Plan.TargetItemDefinitionId = null;
        looter.Plan.TargetJunctionId = null;
        looter.Plan.TargetTile = null;
        looter.Mind.CurrentGoal = GoalType.None;
        looter.Movement.JunctionPath.Clear();
        looter.Movement.PathIndex = 0;
        looter.Movement.IsMoving = false;
        looter.Movement.SetStatus(MovementStatus.Idle);
    }
}

}
