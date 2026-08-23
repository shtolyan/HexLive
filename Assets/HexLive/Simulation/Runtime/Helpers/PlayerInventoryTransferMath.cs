using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>§128 non-mutating validation for one player drag-transfer.</summary>
internal static class PlayerInventoryTransferMath
{
    private const string BottleDefinitionId = "tool.bottle";

    internal static int PackCursor(int index, int count) =>
        ((System.Math.Min(System.Math.Max(count, 1), 0x7fff) << 16) |
         (index & 0xffff));

    internal static void UnpackCursor(int packed, out int index, out int count)
    {
        index = packed & 0xffff;
        count = (packed >> 16) & 0x7fff;
        if (count <= 0) count = 1;
    }

    internal static bool TryResolve(
        NPCState source,
        InventoryItemRef itemRef,
        int requestedCount,
        out List<ItemInstance> items)
    {
        items = new List<ItemInstance>();
        var sourceItems = itemRef.Source == InventoryItemSource.Carried
            ? source.Inventory.Items
            : source.WornItems;
        if (itemRef.Index < 0 || itemRef.Index >= sourceItems.Count ||
            sourceItems[itemRef.Index].DefinitionId != itemRef.ExpectedDefinitionId)
        {
            return false;
        }

        var count = InventoryState.IsStackable(itemRef.ExpectedDefinitionId)
            ? System.Math.Max(1, requestedCount)
            : 1;
        if (itemRef.Source == InventoryItemSource.Worn && count != 1)
        {
            return false;
        }

        for (var i = itemRef.Index; i < sourceItems.Count && items.Count < count; i++)
        {
            if (sourceItems[i].DefinitionId == itemRef.ExpectedDefinitionId)
            {
                items.Add(sourceItems[i]);
            }
        }

        return items.Count == count;
    }

    internal static bool TryResolveTransfer(
        WorldState world,
        NPCState source,
        InventoryItemRef itemRef,
        int requestedCount,
        out List<ItemInstance> items,
        out List<ItemInstance> contents)
    {
        contents = new List<ItemInstance>();
        if (!TryResolve(source, itemRef, requestedCount, out items))
        {
            return false;
        }

        return itemRef.Source != InventoryItemSource.Worn ||
               InventoryLayoutBuilder.TryCollectOwnedContents(
                   world, source, itemRef.Index, out contents);
    }

    internal static bool FitsAfter(
        WorldState world,
        NPCState source,
        NPCState destination,
        InventoryItemRef itemRef,
        int count)
    {
        if (!TryResolveTransfer(
                world, source, itemRef, count, out var moving, out var contents))
        {
            return false;
        }

        // Bottle contents still live on NPCState rather than ItemInstance. Until that
        // legacy representation is migrated, keep its one-bottle invariant explicit:
        // otherwise a transfer could duplicate or silently replace the stored water.
        if ((ContainsDefinition(moving, BottleDefinitionId) ||
             ContainsDefinition(contents, BottleDefinitionId)) &&
            (CountDefinition(source.Inventory.Items, BottleDefinitionId) != 1 ||
             CountDefinition(destination.Inventory.Items, BottleDefinitionId) != 0))
        {
            return false;
        }

        var sourceCarried = new List<ItemInstance>(source.Inventory.Items);
        var sourceWorn = new List<ItemInstance>(source.WornItems);
        var sourceList = itemRef.Source == InventoryItemSource.Carried
            ? sourceCarried
            : sourceWorn;
        foreach (var item in moving)
        {
            RemoveReference(sourceList, item);
        }
        foreach (var item in contents)
        {
            RemoveReference(sourceCarried, item);
        }

        var destinationCarried = new List<ItemInstance>(destination.Inventory.Items);
        var destinationWorn = new List<ItemInstance>(destination.WornItems);
        if (itemRef.Source == InventoryItemSource.Worn)
        {
            // §128.2: a free compatible body slot wins. If it is occupied, the
            // transferred garment is folded into ordinary carry space instead
            // of making the whole loot gesture fail. Its former pocket
            // contents are separate physical items and must fit there too.
            if (HasWearConflict(world, destination, moving[0]))
            {
                destinationCarried.Add(moving[0]);
            }
            else
            {
                destinationWorn.Add(moving[0]);
            }
            destinationCarried.AddRange(contents);
        }
        else
        {
            destinationCarried.AddRange(moving);
        }

        return PlayerInventoryMath.FitsProjected(
                   world, source, sourceCarried, sourceWorn) &&
               PlayerInventoryMath.FitsProjected(
                   world, destination, destinationCarried, destinationWorn);
    }

    internal static void MoveResolved(
        WorldState world,
        NPCState source,
        NPCState destination,
        InventoryItemRef itemRef,
        IReadOnlyList<ItemInstance> moving,
        IReadOnlyList<ItemInstance> contents)
    {
        var movesBottle = ContainsDefinition(moving, BottleDefinitionId) ||
                          ContainsDefinition(contents, BottleDefinitionId);
        var bottleWater = source.BottleWater;
        var bottleCharges = source.BottleCharges;
        if (itemRef.Source == InventoryItemSource.Worn)
        {
            RemoveReference(source.WornItems, moving[0]);
            if (HasWearConflict(world, destination, moving[0]))
            {
                destination.Inventory.Items.Add(moving[0]);
            }
            else
            {
                destination.WornItems.Add(moving[0]);
            }
            foreach (var item in contents)
            {
                RemoveReference(source.Inventory.Items, item);
                destination.Inventory.Items.Add(item);
            }

            EquipmentMath.Recalculate(world, source);
            EquipmentMath.Recalculate(world, destination);
        }
        else
        {
            foreach (var item in moving)
            {
                RemoveReference(source.Inventory.Items, item);
                destination.Inventory.Items.Add(item);
            }
        }

        if (movesBottle)
        {
            destination.BottleWater = bottleWater;
            destination.BottleCharges = bottleCharges;
            source.BottleWater = WaterKind.None;
            source.BottleCharges = 0;
        }
    }

    private static bool HasWearConflict(
        WorldState world, NPCState destination, ItemInstance garment)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(
                garment.DefinitionId, out var incoming) || incoming.Layer is null)
        {
            return true;
        }

        foreach (var worn in destination.WornItems)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(
                    worn.DefinitionId, out var existing) &&
                WearSlotCatalog.Occupies(incoming, existing))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsDefinition(
        IReadOnlyList<ItemInstance> items, string definitionId)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].DefinitionId == definitionId) return true;
        }

        return false;
    }

    private static int CountDefinition(
        IReadOnlyList<ItemInstance> items, string definitionId)
    {
        var count = 0;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].DefinitionId == definitionId) count++;
        }

        return count;
    }

    private static void RemoveReference(List<ItemInstance> items, ItemInstance sought)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (!ReferenceEquals(items[i], sought)) continue;
            items.RemoveAt(i);
            return;
        }
    }
}

}
