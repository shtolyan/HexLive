using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>The visual kind of a derived inventory container.</summary>
public enum InventoryContainerKind
{
    HandLeft,
    HandRight,
    Carry,
    Garment,
    Holster,

    // Defensive only: a valid simulation never exports this because overflow is
    // spilled by InventoryMath. Keeping the cells visible makes a broken
    // invariant diagnosable instead of silently hiding player-owned items.
    Overflow
}

/// <summary>One primary body point for a worn item; never one label per bone.</summary>
public enum InventoryBodyAnchor
{
    None,
    Head,
    Chest,
    Back,
    Pelvis,
    ArmLeft,
    ArmRight,
    ThighLeft,
    ThighRight,
    Legs,
    Feet
}

public sealed class InventorySlotLayout
{
    public int Index { get; set; }

    /// <summary>
    /// Index of the first physical <see cref="ItemInstance"/> represented by
    /// this cell in <see cref="InventoryState.Items"/>. Empty cells use -1.
    /// A stacked cell couples this with <see cref="StackCount"/>.
    /// </summary>
    public int SourceIndex { get; set; } = -1;

    public string ItemDefinitionId { get; set; } = string.Empty;

    public int StackCount { get; set; }

    /// <summary>Non-empty only for a typed holster slot.</summary>
    public string AcceptedItemDefinitionId { get; set; } = string.Empty;
}

public sealed class InventoryContainerLayout
{
    public string Id { get; set; } = string.Empty;

    public InventoryContainerKind Kind { get; set; }

    /// <summary>
    /// The worn item represented by this container. For Carry this is the first
    /// worn backpack (empty when the panel is the body's plain carry space).
    /// </summary>
    public string OwnerItemDefinitionId { get; set; } = string.Empty;

    /// <summary>Physical index in NPCState.WornItems, or -1 for body containers.</summary>
    public int OwnerSourceIndex { get; set; } = -1;

    public InventoryBodyAnchor BodyAnchor { get; set; }

    public int Capacity { get; set; }

    public int BaseCapacity { get; set; }

    public int StrengthBonus { get; set; }

    public int BackpackCapacity { get; set; }

    public List<InventorySlotLayout> Slots { get; } = new();
}

public sealed class InventoryLayout
{
    public List<InventoryContainerLayout> Containers { get; } = new();

    public string FavoriteWeaponId { get; set; } = string.Empty;

    public int RegularCapacity { get; set; }

    public int UsedRegularSlots { get; set; }
}

/// <summary>
/// Builds the deterministic, read-only view of <see cref="InventoryState.Items"/>.
/// The result is never persisted and never becomes a second item store.
/// </summary>
public static class InventoryLayoutBuilder
{
    private sealed class Cell
    {
        public string ItemId = string.Empty;
        public int Count;
        public int SourceIndex = -1;
    }

    public static InventoryLayout Build(WorldState world, NPCState npc)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (npc is null) throw new ArgumentNullException(nameof(npc));

        var result = new InventoryLayout
        {
            FavoriteWeaponId = ItemAffinity.FavoriteWeapon(npc.Id.Value, npc.Inventory.Items) ?? string.Empty,
            RegularCapacity = npc.Inventory.Capacity,
            UsedRegularSlots = npc.Inventory.UsedSlots
        };

        var holsteredInstances = new List<ItemInstance>();
        var claimedHolsterIds = new HashSet<string>();
        AddHolsters(world, npc, result, holsteredInstances, claimedHolsterIds);

        var regular = new List<InventoryContainerLayout>();
        AddGarmentsAndCarry(world, npc, regular);
        AddHands(npc, regular);

        var cells = BuildCells(npc.Inventory.Items, holsteredInstances);
        var overflow = FillRegularContainers(regular, cells);

        foreach (var container in regular)
        {
            result.Containers.Add(container);
        }

        if (overflow is not null)
        {
            result.Containers.Add(overflow);
        }

        return result;
    }

    /// <summary>
    /// Resolves the physical carried instances currently shown inside one worn
    /// item's derived containers. A backpack owns only the bonus tail of the
    /// merged Carry panel; body carry and hands stay with the body.
    /// </summary>
    internal static bool TryCollectOwnedContents(
        WorldState world,
        NPCState npc,
        int wornIndex,
        out List<ItemInstance> contents)
    {
        contents = new List<ItemInstance>();
        if (wornIndex < 0 || wornIndex >= npc.WornItems.Count)
        {
            return false;
        }

        var layout = Build(world, npc);
        foreach (var container in layout.Containers)
        {
            if (container.OwnerSourceIndex != wornIndex)
            {
                continue;
            }

            var firstOwnedSlot = container.Kind == InventoryContainerKind.Carry
                ? container.BaseCapacity + container.StrengthBonus
                : 0;
            foreach (var slot in container.Slots)
            {
                if (slot.Index < firstOwnedSlot ||
                    string.IsNullOrEmpty(slot.ItemDefinitionId) ||
                    slot.StackCount <= 0)
                {
                    continue;
                }

                if (!TryCollectSlotInstances(
                        npc.Inventory.Items, slot, contents))
                {
                    contents.Clear();
                    return false;
                }
            }
        }

        return true;
    }

    public static InventoryBodyAnchor AnchorFor(
        string definitionId,
        ObjectDefinition definition = null)
    {
        if (definition?.Layer == WearLayer.Bags)
        {
            return InventoryBodyAnchor.Back;
        }

        if (HolsterCatalog.IsHolster(definitionId))
        {
            return InventoryBodyAnchor.ThighRight;
        }

        var slots = WearSlotCatalog.For(definitionId);
        if (HasAny(slots, WearSlot.Head, WearSlot.EarL, WearSlot.EarR))
            return InventoryBodyAnchor.Head;
        if (HasAny(slots, WearSlot.Chest, WearSlot.Neck, WearSlot.ShoulderL, WearSlot.ShoulderR))
            return InventoryBodyAnchor.Chest;
        if (HasAny(slots, WearSlot.Belly, WearSlot.Pelvis))
            return InventoryBodyAnchor.Pelvis;
        if (HasAny(slots, WearSlot.ThighR)) return InventoryBodyAnchor.ThighRight;
        if (HasAny(slots, WearSlot.ThighL)) return InventoryBodyAnchor.ThighLeft;
        if (HasAny(slots, WearSlot.ForearmR, WearSlot.WristR, WearSlot.HandR))
            return InventoryBodyAnchor.ArmRight;
        if (HasAny(slots, WearSlot.ForearmL, WearSlot.WristL, WearSlot.HandL))
            return InventoryBodyAnchor.ArmLeft;
        if (HasAny(slots, WearSlot.ShinL, WearSlot.ShinR)) return InventoryBodyAnchor.Legs;
        if (HasAny(slots, WearSlot.FootL, WearSlot.FootR)) return InventoryBodyAnchor.Feet;

        if (definition is not null)
        {
            if (definition.Covers.Contains(BodyPart.Head)) return InventoryBodyAnchor.Head;
            if (definition.Covers.Contains(BodyPart.Torso)) return InventoryBodyAnchor.Chest;
            if (definition.Covers.Contains(BodyPart.Pelvis)) return InventoryBodyAnchor.Pelvis;
            if (definition.Covers.Contains(BodyPart.ArmR)) return InventoryBodyAnchor.ArmRight;
            if (definition.Covers.Contains(BodyPart.ArmL)) return InventoryBodyAnchor.ArmLeft;
            if (definition.Covers.Contains(BodyPart.LegR) || definition.Covers.Contains(BodyPart.LegL))
                return InventoryBodyAnchor.Legs;
        }

        return InventoryBodyAnchor.None;
    }

    private static void AddHolsters(
        WorldState world,
        NPCState npc,
        InventoryLayout result,
        List<ItemInstance> holsteredInstances,
        HashSet<string> claimedHolsterIds)
    {
        var ordinal = 0;
        for (var wornIndex = 0; wornIndex < npc.WornItems.Count; wornIndex++)
        {
            var worn = npc.WornItems[wornIndex];
            var acceptedIds = HolsterCatalog.SlotsFor(worn.DefinitionId);
            if (acceptedIds.Count == 0)
            {
                continue;
            }

            world.Content.ObjectDefinitions.TryGetValue(worn.DefinitionId, out var definition);
            var container = NewContainer(
                $"holster:{ordinal++}:{worn.DefinitionId}",
                InventoryContainerKind.Holster,
                worn.DefinitionId,
                AnchorFor(worn.DefinitionId, definition),
                acceptedIds.Count);
            container.OwnerSourceIndex = wornIndex;

            for (var i = 0; i < acceptedIds.Count; i++)
            {
                var acceptedId = acceptedIds[i];
                ItemInstance item = null;

                // InventoryState.HolsterSlotIds is a set, so duplicate typed ids
                // across two worn holsters still free only the first instance.
                if (claimedHolsterIds.Add(acceptedId))
                {
                    item = FirstUnclaimed(npc.Inventory.Items, holsteredInstances, acceptedId);
                    if (item is not null)
                    {
                        holsteredInstances.Add(item);
                    }
                }

                container.Slots.Add(new InventorySlotLayout
                {
                    Index = i,
                    SourceIndex = item is null ? -1 : IndexOfReference(npc.Inventory.Items, item),
                    ItemDefinitionId = item?.DefinitionId ?? string.Empty,
                    StackCount = item is null ? 0 : 1,
                    AcceptedItemDefinitionId = acceptedId
                });
            }

            result.Containers.Add(container);
        }
    }

    private static void AddGarmentsAndCarry(
        WorldState world,
        NPCState npc,
        List<InventoryContainerLayout> regular)
    {
        var garmentOrdinal = 0;
        var backpackCapacity = 0;
        var backpackId = string.Empty;
        var backpackSourceIndex = -1;

        for (var wornIndex = 0; wornIndex < npc.WornItems.Count; wornIndex++)
        {
            var worn = npc.WornItems[wornIndex];
            if (!world.Content.ObjectDefinitions.TryGetValue(worn.DefinitionId, out var definition))
            {
                continue;
            }

            if (definition.Layer == WearLayer.Bags)
            {
                if (definition.InventoryCapacity > 0)
                {
                    backpackCapacity += definition.InventoryCapacity;
                    if (backpackId.Length == 0)
                    {
                        backpackId = worn.DefinitionId;
                        backpackSourceIndex = wornIndex;
                    }
                }

                continue;
            }

            if (HolsterCatalog.IsHolster(worn.DefinitionId) || definition.InventoryCapacity <= 0)
            {
                continue;
            }

            var garment = NewContainer(
                $"garment:{garmentOrdinal++}:{worn.DefinitionId}",
                InventoryContainerKind.Garment,
                worn.DefinitionId,
                AnchorFor(worn.DefinitionId, definition),
                definition.InventoryCapacity);
            garment.OwnerSourceIndex = wornIndex;
            regular.Add(garment);
        }

        var baseCapacity = npc.Body.IntactHands > 0 ? SimBalance.BaseCarrySlots : 0;
        var strengthBonus = npc.Body.IntactHands > 0 ? AttributeMath.CarrySlotBonus(npc) : 0;
        var capacity = baseCapacity + strengthBonus + backpackCapacity;
        if (capacity > 0)
        {
            var carry = NewContainer(
                "carry",
                InventoryContainerKind.Carry,
                backpackId,
                backpackId.Length == 0 ? InventoryBodyAnchor.Pelvis : InventoryBodyAnchor.Back,
                capacity);
            carry.BaseCapacity = baseCapacity;
            carry.StrengthBonus = strengthBonus;
            carry.BackpackCapacity = backpackCapacity;
            carry.OwnerSourceIndex = backpackSourceIndex;
            regular.Add(carry);
        }
    }

    private static void AddHands(NPCState npc, List<InventoryContainerLayout> regular)
    {
        var remaining = Math.Min(npc.Body.IntactHands, SimBalance.HandSlots);
        if (remaining <= 0)
        {
            return;
        }

        var hasLeft = npc.Body.LimbFunction(BodyPart.ArmL) >= 0.20f;
        var hasRight = npc.Body.LimbFunction(BodyPart.ArmR) >= 0.20f;
        if (remaining >= 2 && hasLeft && hasRight)
        {
            regular.Add(NewContainer(
                "hand:left", InventoryContainerKind.HandLeft, string.Empty,
                InventoryBodyAnchor.ArmLeft, 1));
            regular.Add(NewContainer(
                "hand:right", InventoryContainerKind.HandRight, string.Empty,
                InventoryBodyAnchor.ArmRight, 1));
            return;
        }

        // Prefer retaining the dominant/right hand when the global hand-slot
        // tuning is below the number of functional hands.
        if (hasRight && remaining-- > 0)
        {
            regular.Add(NewContainer(
                "hand:right", InventoryContainerKind.HandRight, string.Empty,
                InventoryBodyAnchor.ArmRight, 1));
        }

        if (hasLeft && remaining-- > 0)
        {
            regular.Add(NewContainer(
                "hand:left", InventoryContainerKind.HandLeft, string.Empty,
                InventoryBodyAnchor.ArmLeft, 1));
        }
    }

    private static List<Cell> BuildCells(
        IReadOnlyList<ItemInstance> items,
        IReadOnlyList<ItemInstance> holsteredInstances)
    {
        var cells = new List<Cell>();
        var emittedStacks = new HashSet<string>();

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (ContainsReference(holsteredInstances, item) ||
                InventoryState.IsPersonalEffect(item.DefinitionId))
            {
                continue;
            }

            if (!InventoryState.IsStackable(item.DefinitionId))
            {
                cells.Add(new Cell
                {
                    ItemId = item.DefinitionId,
                    Count = 1,
                    SourceIndex = i
                });
                continue;
            }

            if (!emittedStacks.Add(item.DefinitionId))
            {
                continue;
            }

            var sourceIndices = new List<int>();
            for (var j = 0; j < items.Count; j++)
            {
                var candidate = items[j];
                if (candidate.DefinitionId == item.DefinitionId &&
                    !ContainsReference(holsteredInstances, candidate))
                {
                    sourceIndices.Add(j);
                }
            }

            var stackSize = InventoryState.StackSizeFor(item.DefinitionId);
            var sourceOffset = 0;
            while (sourceOffset < sourceIndices.Count)
            {
                var chunk = Math.Min(stackSize, sourceIndices.Count - sourceOffset);
                cells.Add(new Cell
                {
                    ItemId = item.DefinitionId,
                    Count = chunk,
                    SourceIndex = sourceIndices[sourceOffset]
                });
                sourceOffset += chunk;
            }
        }

        return cells;
    }

    private static InventoryContainerLayout FillRegularContainers(
        List<InventoryContainerLayout> regular,
        List<Cell> cells)
    {
        var cellIndex = 0;
        foreach (var container in regular)
        {
            for (var i = 0; i < container.Capacity; i++)
            {
                var cell = cellIndex < cells.Count ? cells[cellIndex++] : null;
                container.Slots.Add(new InventorySlotLayout
                {
                    Index = i,
                    SourceIndex = cell?.SourceIndex ?? -1,
                    ItemDefinitionId = cell?.ItemId ?? string.Empty,
                    StackCount = cell?.Count ?? 0
                });
            }
        }

        if (cellIndex >= cells.Count)
        {
            return null;
        }

        var overflow = NewContainer(
            "overflow", InventoryContainerKind.Overflow, string.Empty,
            InventoryBodyAnchor.None, cells.Count - cellIndex);
        while (cellIndex < cells.Count)
        {
            var cell = cells[cellIndex];
            overflow.Slots.Add(new InventorySlotLayout
            {
                Index = overflow.Slots.Count,
                SourceIndex = cell.SourceIndex,
                ItemDefinitionId = cell.ItemId,
                StackCount = cell.Count
            });
            cellIndex++;
        }

        return overflow;
    }

    private static InventoryContainerLayout NewContainer(
        string id,
        InventoryContainerKind kind,
        string ownerItemId,
        InventoryBodyAnchor anchor,
        int capacity) => new()
    {
        Id = id,
        Kind = kind,
        OwnerItemDefinitionId = ownerItemId ?? string.Empty,
        BodyAnchor = anchor,
        Capacity = capacity
    };

    private static ItemInstance FirstUnclaimed(
        IReadOnlyList<ItemInstance> items,
        IReadOnlyList<ItemInstance> claimed,
        string definitionId)
    {
        foreach (var item in items)
        {
            if (item.DefinitionId == definitionId && !ContainsReference(claimed, item))
            {
                return item;
            }
        }

        return null;
    }

    private static bool ContainsReference(IReadOnlyList<ItemInstance> items, ItemInstance sought)
    {
        foreach (var item in items)
        {
            if (ReferenceEquals(item, sought))
            {
                return true;
            }
        }

        return false;
    }

    private static int IndexOfReference(IReadOnlyList<ItemInstance> items, ItemInstance sought)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], sought))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryCollectSlotInstances(
        IReadOnlyList<ItemInstance> carried,
        InventorySlotLayout slot,
        List<ItemInstance> collected)
    {
        if (slot.SourceIndex < 0 || slot.SourceIndex >= carried.Count ||
            carried[slot.SourceIndex].DefinitionId != slot.ItemDefinitionId)
        {
            return false;
        }

        var remaining = slot.StackCount;
        for (var i = slot.SourceIndex; i < carried.Count && remaining > 0; i++)
        {
            var item = carried[i];
            if (item.DefinitionId != slot.ItemDefinitionId ||
                ContainsReference(collected, item))
            {
                continue;
            }

            collected.Add(item);
            remaining--;
        }

        return remaining == 0;
    }

    private static bool HasAny(IReadOnlyList<WearSlot> slots, params WearSlot[] sought)
    {
        foreach (var slot in slots)
        {
            foreach (var candidate in sought)
            {
                if (slot == candidate)
                {
                    return true;
                }
            }
        }

        return false;
    }
}

}
