using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

// Spec §52: pack bookkeeping keyed on item importance. Water/food outrank
// resources, so a full pack sheds a stone before a coconut. Centralizes the
// "what do I drop / keep" decisions shared by overflow, haul-to-fire and stash.
internal static class InventoryMath
{
    public static int Importance(WorldState world, string definitionId) =>
        world.Content.ObjectDefinitions.TryGetValue(definitionId, out var def)
            ? ItemCatalog.Importance(def)
            : ItemCatalog.ImportanceById(definitionId);

    // Spec §52: importance follows the INSTANCE, not the label — a drained
    // pierced coconut is an empty shell (Resource-rank, first to drop), not
    // "water". Victim-side checks must use this overload or a dry shell
    // blocks the pack forever.
    public static int Importance(WorldState world, ItemInstance item) =>
        ItemCatalog.IsDrainedWaterShell(item.DefinitionId, item.ResourceAmount)
            ? ItemCatalog.Importance(ItemCategory.Resource)
            : Importance(world, item.DefinitionId);

    public static bool CanMakeRoomFor(WorldState world, NPCState npc, string incomingDefinitionId)
    {
        if (npc.Inventory.HasSpace || FitsExistingStack(npc, incomingDefinitionId))
        {
            return true;
        }

        var victim = LowestImportanceDroppable(world, npc);
        return victim is not null &&
            Importance(world, incomingDefinitionId) > Importance(world, victim);
    }

    public static bool MakeRoomFor(WorldState world, NPCState npc, string incomingDefinitionId)
    {
        if (npc.Inventory.HasSpace || FitsExistingStack(npc, incomingDefinitionId))
        {
            return true;
        }

        var incomingImportance = Importance(world, incomingDefinitionId);
        var guard = 0;
        while (!npc.Inventory.HasSpace &&
               !FitsExistingStack(npc, incomingDefinitionId) &&
               guard++ < 64)
        {
            var victim = LowestImportanceDroppable(world, npc);
            if (victim is null)
            {
                return false;
            }

            var victimImportance = Importance(world, victim);
            if (victimImportance >= incomingImportance)
            {
                return false;
            }

            npc.Inventory.Items.Remove(victim);
            ExecutionSystem.DropItemAtFeet(world, npc, victim);
            Trace.Emit(world, npc.Id, "InventoryMadeRoom",
                $"Dropped {victim.DefinitionId}({victimImportance}) for " +
                $"{incomingDefinitionId}({incomingImportance})");
        }

        return npc.Inventory.HasSpace || FitsExistingStack(npc, incomingDefinitionId);
    }

    private static bool FitsExistingStack(NPCState npc, string definitionId)
    {
        if (!InventoryState.IsStackable(definitionId))
        {
            return false;
        }

        var count = 0;
        foreach (var item in npc.Inventory.Items)
        {
            if (item.DefinitionId == definitionId)
            {
                count++;
            }
        }

        return count > 0 && count % InventoryState.StackSizeFor(definitionId) != 0;
    }

    // Spec §52: does this ground object carry, in its pockets, a Tool the NPC
    // lacks and could make room for? Dropped garments keep their stash in
    // Contents — the undress overflow may have carried the knife down with
    // the jacket, and GatherTools must be able to see it there.
    public static bool StashHoldsWantedTool(WorldState world, NPCState npc, WorldObjectState container)
    {
        foreach (var stashed in container.Contents)
        {
            if (npc.Inventory.Items.Contains(stashed.DefinitionId))
            {
                continue;
            }

            if (world.Content.ObjectDefinitions.TryGetValue(stashed.DefinitionId, out var def) &&
                def.Tags.Contains("Tool") &&
                Content.GearCatalog.AddsValueOver(
                    npc.Inventory.Items, stashed.DefinitionId, npc.Body.IntactHands) &&
                CanMakeRoomFor(world, npc, stashed.DefinitionId))
            {
                return true;
            }
        }

        return false;
    }

    // The least-wanted pocket item — the first to go when room is tight.
    // Personal effects (the bottle) are never candidates.
    public static ItemInstance LowestImportanceDroppable(WorldState world, NPCState npc)
    {
        ItemInstance worst = null;
        var worstImp = int.MaxValue;
        foreach (var item in npc.Inventory.Items)
        {
            if (InventoryState.IsPersonalEffect(item.DefinitionId))
            {
                continue;
            }

            // Spec §52.8: a holstered tool is strapped to the leg and costs no
            // pocket — shedding it frees nothing, so it is never the victim. It
            // leaves the pack only when the holster itself comes off (the slots
            // vanish first, then it spills by the normal rules).
            if (npc.Inventory.IsHolstered(item))
            {
                continue;
            }

            var imp = Importance(world, item);
            if (imp < worstImp)
            {
                worstImp = imp;
                worst = item;
            }
        }

        return worst;
    }

    // Drop the lowest-importance pocket items until the pack fits again. Used
    // whenever capacity shrinks under a full load (undress, arm severed). Items
    // land at the NPC's feet with their instance state (wetness/durability).
    public static void SpillOverflow(WorldState world, NPCState npc)
    {
        var inv = npc.Inventory;
        var guard = 0;
        while (inv.UsedSlots > inv.Capacity && guard++ < 64)
        {
            var victim = LowestImportanceDroppable(world, npc);
            if (victim is null)
            {
                break;
            }

            inv.Items.Remove(victim);
            ExecutionSystem.DropItemAtFeet(world, npc, victim);
            Trace.Emit(world, npc.Id, "ItemSpilled", $"{victim.DefinitionId} (no pocket room)");
        }
    }
}

}
