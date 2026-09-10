using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §123.5: a player's own inventory is management state, not an AI goal.
/// Applying it atomically lets an AI-controlled colonist keep walking or
/// working while the player changes equipment. The owner-permission scene is
/// intentionally bypassed: explicit player authority is the permission.
/// </summary>
internal static class PlayerInventoryCommandExecutor
{
    internal static bool TryApply(
        WorldState world,
        NPCState npc,
        InventoryItemRef reference,
        InventoryAction action,
        out string reason,
        int count = 1)
    {
        var source = reference.Source == InventoryItemSource.Carried
            ? npc.Inventory.Items
            : npc.WornItems;
        if (reference.Index < 0 || reference.Index >= source.Count ||
            source[reference.Index].DefinitionId != reference.ExpectedDefinitionId)
        {
            reason = "StaleItem";
            return false;
        }

        var item = source[reference.Index];
        if (count <= 0 ||
            count != 1 &&
            (action != InventoryAction.Drop || reference.Source != InventoryItemSource.Carried))
        {
            reason = "InvalidCount";
            return false;
        }

        if (npc.Mind.OutfitLocked &&
            (action == InventoryAction.Wear || reference.Source == InventoryItemSource.Worn))
        {
            reason = "OutfitLocked";
            return false;
        }

        if (action == InventoryAction.Wear &&
            (reference.Source != InventoryItemSource.Carried ||
             !world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var wearDef) ||
             wearDef.Layer is null) ||
            action == InventoryAction.Stow && reference.Source != InventoryItemSource.Worn ||
            action is not (InventoryAction.Wear or InventoryAction.Stow or InventoryAction.Drop))
        {
            reason = "InvalidAction";
            return false;
        }

        if (!PlayerInventoryMath.FitsAfter(world, npc, reference, action))
        {
            reason = "InsufficientSpace";
            return false;
        }

        if (action == InventoryAction.Drop &&
            reference.Source == InventoryItemSource.Carried)
        {
            return PlayerInventoryDropExecutor.TryApply(
                world, npc, reference, count, out reason);
        }

        switch (action)
        {
            case InventoryAction.Wear:
                ExecutionSystem.WearCarriedItem(world, npc, item);
                break;
            case InventoryAction.Stow:
                npc.WornItems.RemoveAt(reference.Index);
                npc.Inventory.Items.Add(item);
                EquipmentMath.Recalculate(world, npc);
                break;
            case InventoryAction.Drop:
                var dropped = ExecutionSystem.DropItemAtFeet(world, npc, item, underFoot: true);
                if (dropped is null)
                {
                    reason = "NoDropSpot";
                    return false;
                }

                npc.WornItems.RemoveAt(reference.Index);
                EquipmentMath.Recalculate(world, npc);
                ExecutionSystem.StashOverflowInDroppedGarment(world, npc, item, dropped);
                break;
        }

        reason = string.Empty;
        return true;
    }
}

}
