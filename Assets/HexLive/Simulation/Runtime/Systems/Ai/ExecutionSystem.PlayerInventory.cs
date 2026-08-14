using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{
    public sealed partial class ExecutionSystem
    {
        private static void RunPlayerInventory(WorldState world, NPCState npc)
        {
            if (npc.Plan.Steps.Count == 0) return;
            var step = npc.Plan.Steps[0];
            var index = step.TimeoutEndTick ?? -1;
            var definitionId = npc.Plan.TargetItemDefinitionId;
            var source = step.Type is PlanStepType.PlayerWearInventory or
                PlanStepType.PlayerDropCarried
                ? InventoryItemSource.Carried
                : InventoryItemSource.Worn;
            var action = step.Type switch
            {
                PlanStepType.PlayerWearInventory => InventoryAction.Wear,
                PlanStepType.PlayerStowWorn => InventoryAction.Stow,
                _ => InventoryAction.Drop
            };

            var list = source == InventoryItemSource.Carried
                ? npc.Inventory.Items
                : npc.WornItems;
            if (index < 0 || index >= list.Count ||
                string.IsNullOrEmpty(definitionId) || list[index].DefinitionId != definitionId)
            {
                FailPlayerInventory(world, npc, "StaleItem");
                return;
            }

            var item = list[index];
            if (npc.Execution.Status == ExecutionStatus.None)
            {
                npc.Execution.Status = ExecutionStatus.InProgress;
                npc.Execution.TargetObject = null;
                npc.Execution.StartTick = world.Tick;
                var clothing = step.Type != PlanStepType.PlayerDropCarried;
                npc.Execution.EndTick = world.Tick + (clothing ? UndressDurationTicks : 1);
                npc.Execution.CurrentInteraction = step.Type == PlanStepType.PlayerWearInventory
                    ? InteractionType.Dress
                    : clothing ? InteractionType.Undress : null;
                npc.Execution.HeldGarment = null;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "InteractionStarted",
                        $"PlayerInventory Action={action} Def={definitionId} " +
                        $"Duration={npc.Execution.EndTick - world.Tick}ticks");
                }
                return;
            }

            var duration = npc.Execution.EndTick - npc.Execution.StartTick;
            var progress = duration > 0
                ? (float)(world.Tick - npc.Execution.StartTick) / duration
                : 1f;
            if (npc.Execution.HeldGarment is null &&
                step.Type != PlanStepType.PlayerDropCarried &&
                progress >= WardrobeHandoffFraction)
            {
                // Visual handoff only. The item remains in its authoritative
                // list until completion, which makes a mid-animation save fully
                // reconstructible without extending the blob shape.
                npc.Execution.HeldGarment = item;
            }

            if (world.Tick < npc.Execution.EndTick) return;

            var reference = new InventoryItemRef(source, index, definitionId);
            if (!PlayerInventoryMath.FitsAfter(world, npc, reference, action))
            {
                FailPlayerInventory(world, npc, "InsufficientSpace");
                return;
            }

            // §123.5: спавн у ног проверяется ДО удаления из инвентаря —
            // DropItemAtFeet возвращает null, когда рядом нет свободной точки,
            // и порядок «сначала RemoveAt, потом спавн» тихо УНИЧТОЖАЛ предмет.
            switch (step.Type)
            {
                case PlanStepType.PlayerWearInventory:
                    WearCarriedItem(world, npc, item);
                    break;
                case PlanStepType.PlayerStowWorn:
                    npc.WornItems.RemoveAt(index);
                    npc.Inventory.Items.Add(item);
                    EquipmentMath.Recalculate(world, npc);
                    break;
                case PlanStepType.PlayerDropCarried:
                    if (DropItemAtFeet(world, npc, item) is null)
                    {
                        FailPlayerInventory(world, npc, "NoDropSpot");
                        return;
                    }

                    npc.Inventory.Items.RemoveAt(index);
                    break;
                case PlanStepType.PlayerDropWorn:
                    if (DropItemAtFeet(world, npc, item) is null)
                    {
                        FailPlayerInventory(world, npc, "NoDropSpot");
                        return;
                    }

                    npc.WornItems.RemoveAt(index);
                    EquipmentMath.Recalculate(world, npc);
                    break;
            }

            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlayerInventoryCompleted",
                    $"Action={action} Source={source} Index={index} Def={definitionId}");
            }
            FinishPlayerInventory(npc, PlanStatus.Completed);
        }

        private static void WearCarriedItem(WorldState world, NPCState npc, ItemInstance item)
        {
            if (!world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var newDefinition))
                return;
            npc.Inventory.Items.Remove(item);
            for (var i = npc.WornItems.Count - 1; i >= 0; i--)
            {
                var worn = npc.WornItems[i];
                if (!world.Content.ObjectDefinitions.TryGetValue(
                        worn.DefinitionId, out var wornDefinition) ||
                    !WearSlotCatalog.Occupies(newDefinition, wornDefinition)) continue;
                npc.WornItems.RemoveAt(i);
                npc.Inventory.Items.Add(worn);
            }
            npc.WornItems.Add(item);
            EquipmentMath.Recalculate(world, npc);
        }

        private static void FailPlayerInventory(WorldState world, NPCState npc, string reason)
        {
            Trace.Emit(world, npc.Id, "ManualOrderRejected",
                $"Order=Inventory Reason={reason}");
            FinishPlayerInventory(npc, PlanStatus.Failed);
        }

        private static void FinishPlayerInventory(NPCState npc, PlanStatus status)
        {
            npc.Execution.HeldGarment = null;
            npc.Execution.HeldGarmentContents.Clear();
            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.CurrentInteraction = null;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = 0;
            npc.Execution.EndTick = 0;
            npc.Plan.Status = status;
            npc.Plan.Steps.Clear();
            npc.Plan.TargetItemDefinitionId = null;
            npc.Mind.CurrentGoal = GoalType.None;
        }
    }
}
