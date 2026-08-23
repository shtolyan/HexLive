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
    /// <summary>
    /// §128.5: перенос одной ячейки между колонисткой и ВЕЩЬЮ — истлевшим
    /// телом, снятым рюкзаком, аптечкой. Зеркало человеческого обмена, но
    /// второй стороной стоит объект мира, а не второй NPC.
    /// </summary>
    private static void RunPlayerContainerTransfer(WorldState world, NPCState looter)
    {
        if (looter.Plan.Steps.Count == 0 ||
            looter.Plan.TargetObjectId is not { } containerId ||
            !world.Entities.Objects.TryGetValue(containerId, out var container) ||
            !ContainerLootMath.IsLootable(world, container))
        {
            FailContainerTransfer(world, looter, "ContainerGone");
            return;
        }

        if (looter.Movement.IsMoving ||
            (looter.Movement.Status != MovementStatus.Arrived &&
             looter.Movement.JunctionPath.Count > 0))
        {
            return;
        }

        if (!world.Content.ObjectDefinitions.TryGetValue(
                container.DefinitionId, out var definition))
        {
            FailContainerTransfer(world, looter, "ContainerGone");
            return;
        }

        // Тот же гейт дистанции, что у любого взаимодействия с объектом
        // (§26.6A): стоять надо ВПЛОТНУЮ, а не «где-то в том же гексе», и по
        // эту сторону стены или обрыва.
        if (looter.Movement.Status == MovementStatus.Blocked ||
            !InteractionReach.CheckObjectStart(
                world, looter, container, definition.ObstacleRadius))
        {
            FailContainerTransfer(world, looter, "OutOfReach");
            return;
        }

        var step = looter.Plan.Steps[^1];
        PlayerInventoryTransferMath.UnpackCursor(
            step.TimeoutEndTick ?? 0, out var slotIndex, out var count);
        var take = step.Type == PlanStepType.PlayerTakeFromContainer;
        var expected = looter.Plan.TargetItemDefinitionId ?? string.Empty;

        if (take)
        {
            if (!ContainerLootMath.TryResolve(
                    world, container, slotIndex, expected, count,
                    out var moving, out var groundSources))
            {
                FailContainerTransfer(world, looter, "StaleItem");
                return;
            }

            if (!ContainerLootMath.FitsInLooter(world, looter, moving))
            {
                FailContainerTransfer(world, looter, "InsufficientSpace");
                return;
            }

            ContainerLootMath.TakeFromContainer(
                world, container, looter, moving, groundSources);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, looter.Id, "ContainerTransferred",
                    $"Direction=Take Container={container.Id.Value} " +
                    $"Def={expected} Count={moving.Count}");
            }
        }
        else
        {
            // Отдаём из карманов: ячейка называется индексом в ЕЁ раскладке,
            // поэтому разрешаем ровно тем же кодом, что и человеческий обмен.
            var itemRef = new InventoryItemRef(
                InventoryItemSource.Carried, slotIndex, expected);
            if (!PlayerInventoryTransferMath.TryResolveTransfer(
                    world, looter, itemRef, count, out var moving, out _))
            {
                FailContainerTransfer(world, looter, "StaleItem");
                return;
            }

            if (!ContainerLootMath.CanAccept(world, container, moving))
            {
                FailContainerTransfer(world, looter, "InsufficientSpace");
                return;
            }

            ContainerLootMath.GiveToContainer(world, container, looter, moving);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, looter.Id, "ContainerTransferred",
                    $"Direction=Give Container={container.Id.Value} " +
                    $"Def={expected} Count={moving.Count}");
            }
        }

        EquipmentMath.Recalculate(world, looter);
        FinishContainerTransfer(world, looter, PlanStatus.Completed);
    }

    private static void FailContainerTransfer(
        WorldState world, NPCState looter, string reason)
    {
        Trace.Emit(world, looter.Id, "ManualOrderRejected",
            $"Order=TransferContainer Reason={reason}");
        FinishContainerTransfer(world, looter, PlanStatus.Failed);
    }

    private static void FinishContainerTransfer(
        WorldState world, NPCState looter, PlanStatus status)
    {
        if (looter.Plan.TargetJunctionId is { } reserved)
        {
            SpatialMutations.ReleaseJunctionReservation(world, reserved, looter.Id);
        }

        looter.Plan.Status = status;
        looter.Plan.Steps.Clear();
        looter.Plan.CurrentStepIndex = 0;
        looter.Plan.TargetObjectId = null;
        looter.Plan.TargetItemDefinitionId = null;
        looter.Plan.TargetJunctionId = null;
        looter.Plan.TargetTile = null;
        looter.Plan.Goal = GoalType.None;
        looter.Mind.CurrentGoal = GoalType.None;
        looter.Execution.Status = ExecutionStatus.None;
        looter.Execution.CurrentInteraction = null;
        looter.Movement.JunctionPath.Clear();
        looter.Movement.IsMoving = false;
        looter.Movement.SetStatus(MovementStatus.Idle);
    }
}

}
