using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>§123.5: one atomic explicit drop of carried physical instances.</summary>
internal static class PlayerInventoryDropExecutor
{
    internal static bool TryApply(
        WorldState world,
        NPCState npc,
        InventoryItemRef reference,
        int requestedCount,
        out string reason)
    {
        if (reference.Source != InventoryItemSource.Carried || requestedCount <= 0 ||
            requestedCount > InventoryState.StackSizeFor(reference.ExpectedDefinitionId) ||
            (!InventoryState.IsStackable(reference.ExpectedDefinitionId) && requestedCount != 1) ||
            !PlayerInventoryTransferMath.TryResolve(
                npc, reference, requestedCount, out var items))
        {
            reason = "InvalidCount";
            return false;
        }

        if (!GroundItemPlacement.CanPlaceBatch(world,npc,items,out reason)) return false;

        var originalItems = npc.Inventory.Items.ToArray();
        var droppedIds = new List<ObjectId>(items.Count);
        using var eventScope = world.Events.DeferPublication();
        try
        {
            foreach (var item in items)
            {
                var dropped = ExecutionSystem.DropItemAtFeet(world, npc, item);
                if (dropped is null)
                {
                    Rollback(world, npc, originalItems, droppedIds);
                    reason = "NoDropSpot";
                    return false;
                }

                droppedIds.Add(dropped.Id);
            }

            foreach (var item in items)
            {
                if (!InventoryMath.RemoveReference(npc.Inventory.Items, item))
                    throw new InvalidOperationException("Resolved inventory item disappeared during atomic drop.");
            }

            eventScope.Commit();
            reason = string.Empty;
            return true;
        }
        catch
        {
            Rollback(world, npc, originalItems, droppedIds);
            throw;
        }
    }

    private static void Rollback(
        WorldState world,
        NPCState npc,
        IReadOnlyList<ItemInstance> originalItems,
        IReadOnlyList<ObjectId> droppedIds)
    {
        for (var i = droppedIds.Count - 1; i >= 0; i--)
            WorldObjectMutations.DespawnObject(world, droppedIds[i]);
        npc.Inventory.Items.Clear();
        npc.Inventory.Items.AddRange(originalItems);
    }
}

}
