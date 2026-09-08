using System;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{
    public static class AdminGarments
    {
        // Category stays catalog-owned. This finer query distinguishes skirts from other bottoms.
        public static string Kind(GarmentParams g) => (g.Id + " " + g.PrototypeId).IndexOf("skirt", StringComparison.OrdinalIgnoreCase) >= 0 ? "skirt" : g.Category.ToString();
        public static string Execute(WorldState world, NPCState npc, AdminCommand c)
        {
            if (c.Count != 1) return "InvalidCount";
            var choices = GarmentLibrary.Spawnable.Where(g => world.Content.ObjectDefinitions.ContainsKey(g.Id) &&
                (g.Sex == GarmentSex.Any || g.Sex == npc.Sex) &&
                (string.IsNullOrEmpty(c.Category) || string.Equals(Kind(g), c.Category, StringComparison.OrdinalIgnoreCase) || string.Equals(g.Category.ToString(), c.Category, StringComparison.OrdinalIgnoreCase)))
                .Where(g => c.DefinitionId == "random" || c.DefinitionId == g.Id).OrderBy(g => g.Id, StringComparer.Ordinal).ToArray();
            if (choices.Length == 0) return "NoCompatibleGarment";
            uint hash = 2166136261;
            foreach (var ch in c.OperationId ?? string.Empty) hash = unchecked((hash ^ ch) * 16777619);
            var picked = choices[hash % (uint)choices.Length];
            var added = new ItemInstance(picked.Id) { OwnerId = npc.Id.Value };
            var inventory = new InventoryState { Capacity = npc.Inventory.Capacity };
            inventory.Items.AddRange(npc.Inventory.Items);
            if (c.Kind == "give_garment")
            {
                inventory.HolsterSlotIds.UnionWith(npc.Inventory.HolsterSlotIds); inventory.Items.Add(added);
                if (inventory.UsedSlots > inventory.Capacity) return "InventoryFull";
                npc.Inventory.Items.Add(added);
            }
            else
            {
                var definition = world.Content.ObjectDefinitions[picked.Id];
                var displaced = npc.WornItems.Where(i => world.Content.ObjectDefinitions.TryGetValue(i.DefinitionId, out var old) && WearSlotCatalog.Occupies(definition, old)).ToArray();
                var remaining = npc.WornItems.Where(i => !displaced.Any(d => ReferenceEquals(d, i))).ToArray();
                inventory.Capacity += definition.InventoryCapacity - displaced.Sum(i => world.Content.ObjectDefinitions[i.DefinitionId].InventoryCapacity);
                foreach (var item in remaining) inventory.HolsterSlotIds.UnionWith(HolsterCatalog.SlotsFor(item.DefinitionId));
                inventory.HolsterSlotIds.UnionWith(HolsterCatalog.SlotsFor(picked.Id));
                inventory.Items.AddRange(displaced);
                if (inventory.UsedSlots > inventory.Capacity) return "InventoryFull";
                npc.WornItems.RemoveAll(i => displaced.Any(d => ReferenceEquals(i, d)));
                npc.WornItems.Add(added); npc.Inventory.Items.AddRange(displaced);
            }
            // Return the actual choice to the model; the bus deduplicates using the original request.
            c.ResolvedDefinitionId = picked.Id;
            return string.Empty;
        }
    }
}
