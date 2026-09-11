using System;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;

namespace HexLive.Server.Mcp;

/// <summary>§144/#410: the same derived slots as the simulation/UI, never item-count-as-slot-count.</summary>
internal static class McpInventoryObservations
{
    public static object Read(WorldState world, NPCState npc)
    {
        var layout = InventoryLayoutBuilder.Build(world, npc);
        var capacity = npc.Inventory.Capacity;
        var used = npc.Inventory.UsedSlots;
        var free = Math.Max(0, capacity - used);
        var types = npc.Inventory.Items.Select(i => i.DefinitionId)
            .Concat(npc.Perception.Objects.Where(o => !o.FromMemory).Select(o => o.DefinitionId))
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);
        return new
        {
            capacity, usedSlots = used, freeSlots = free, overflowSlots = Math.Max(0, used - capacity),
            itemCount = npc.Inventory.Items.Count,
            slots = layout.Containers.SelectMany(container => container.Slots.Select(slot => new
            {
                containerId = container.Id, kind = container.Kind.ToString(), slot = slot.Index,
                sourceIndex = slot.SourceIndex, itemId = slot.ItemDefinitionId, count = slot.StackCount,
                acceptedItemId = slot.AcceptedItemDefinitionId,
                // An explicit drop is one instance; a cell is freed only after its last instance.
                countToFreeSlot = slot.StackCount
            })).ToArray(),
            itemCapacity = types.Select(id =>
            {
                var count = npc.Inventory.Items.Count(i => i.DefinitionId == id);
                var stackSize = InventoryState.IsStackable(id) ? InventoryState.StackSizeFor(id) : 1;
                var partial = count % stackSize;
                var stackSpace = partial == 0 ? 0 : stackSize - partial;
                var additional = free * stackSize + stackSpace;
                world.Content.ObjectDefinitions.TryGetValue(id, out var definition);
                if (definition?.MaxCarriedInstances > 0)
                    additional = Math.Min(additional, Math.Max(0, definition.MaxCarriedInstances - count));
                return new { itemId = id, count, stackSize, existingStackSpace = stackSpace,
                    additionalWithoutDropping = additional, canAcceptWithoutDropping = additional > 0 };
            }).ToArray()
        };
    }
}
