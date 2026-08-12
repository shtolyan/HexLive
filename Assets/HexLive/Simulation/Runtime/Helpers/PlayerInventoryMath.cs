using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{
    /// <summary>§123 non-mutating capacity preflight for player inventory verbs.</summary>
    internal static class PlayerInventoryMath
    {
        public static bool FitsAfter(
            WorldState world, NPCState npc, InventoryItemRef itemRef, InventoryAction action)
        {
            var carried = new List<ItemInstance>(npc.Inventory.Items);
            var worn = new List<ItemInstance>(npc.WornItems);

            if (itemRef.Source == InventoryItemSource.Carried)
            {
                if (itemRef.Index < 0 || itemRef.Index >= carried.Count) return false;
                var item = carried[itemRef.Index];
                if (item.DefinitionId != itemRef.ExpectedDefinitionId) return false;
                if (action == InventoryAction.Drop)
                {
                    carried.RemoveAt(itemRef.Index);
                }
                else if (action == InventoryAction.Wear)
                {
                    if (!world.Content.ObjectDefinitions.TryGetValue(
                            item.DefinitionId, out var newDefinition) || newDefinition.Layer is null)
                        return false;
                    carried.RemoveAt(itemRef.Index);
                    for (var i = worn.Count - 1; i >= 0; i--)
                    {
                        if (world.Content.ObjectDefinitions.TryGetValue(
                                worn[i].DefinitionId, out var oldDefinition) &&
                            WearSlotCatalog.Occupies(newDefinition, oldDefinition))
                        {
                            carried.Add(worn[i]);
                            worn.RemoveAt(i);
                        }
                    }
                    worn.Add(item);
                }
                else
                {
                    return false;
                }
            }
            else
            {
                if (itemRef.Index < 0 || itemRef.Index >= worn.Count) return false;
                var item = worn[itemRef.Index];
                if (item.DefinitionId != itemRef.ExpectedDefinitionId) return false;
                if (action == InventoryAction.Stow &&
                    (!InventoryLayoutBuilder.TryCollectOwnedContents(
                         world, npc, itemRef.Index, out var pocketContents) ||
                     pocketContents.Count > 0))
                {
                    // A filled container may move as one worn bundle in §128,
                    // but it may never become a nested pocket inside another
                    // flat inventory container.
                    return false;
                }
                worn.RemoveAt(itemRef.Index);
                if (action == InventoryAction.Stow) carried.Add(item);
                else if (action != InventoryAction.Drop) return false;
            }

            return FitsProjected(world, npc, carried, worn);
        }

        internal static bool FitsProjected(
            WorldState world,
            NPCState npc,
            IReadOnlyList<ItemInstance> carried,
            IReadOnlyList<ItemInstance> worn)
        {
            var capacity = npc.Inventory.Capacity;
            foreach (var current in npc.WornItems)
            {
                if (world.Content.ObjectDefinitions.TryGetValue(
                        current.DefinitionId, out var definition))
                    capacity -= definition.InventoryCapacity;
            }
            var projectedHolsters = new HashSet<string>();
            foreach (var projected in worn)
            {
                if (world.Content.ObjectDefinitions.TryGetValue(
                        projected.DefinitionId, out var definition))
                    capacity += definition.InventoryCapacity;
                foreach (var slot in HolsterCatalog.SlotsFor(projected.DefinitionId))
                    projectedHolsters.Add(slot);
            }

            var layout = new InventoryState { Capacity = capacity };
            layout.Items.AddRange(carried);
            foreach (var slot in projectedHolsters) layout.HolsterSlotIds.Add(slot);
            return layout.UsedSlots <= layout.Capacity;
        }
    }
}
