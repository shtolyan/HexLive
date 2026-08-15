using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

// Physical medical supplies. Dressings are ordinary ItemInstances, grouped in
// visible stacks of ten by InventoryState/InventoryLayoutBuilder. ResourceAmount
// is a persisted provenance bit: 0 is a
// pre-made medkit gauze, 1 is the plantain dressing produced by CraftBandage.
internal static class MedicalSupplyMath
{
    private const float HerbalMarker = 0.5f;

    internal static int BandageCount(NPCState npc)
    {
        var count = 0;
        foreach (var item in npc.Inventory.Items)
        {
            if (item.DefinitionId == ContentIds.Bandage)
            {
                count++;
            }
        }

        return count;
    }

    internal static int HerbalBandageCount(NPCState npc)
    {
        var count = 0;
        foreach (var item in npc.Inventory.Items)
        {
            if (item.DefinitionId == ContentIds.Bandage && item.ResourceAmount >= HerbalMarker)
            {
                count++;
            }
        }

        return count;
    }

    internal static int PillCount(NPCState npc)
    {
        var count = 0;
        foreach (var item in npc.Inventory.Items)
        {
            if (item.DefinitionId == ContentIds.Pill)
            {
                count++;
            }
        }

        return count;
    }

    internal static ItemInstance CreateBandage(bool herbal) =>
        new(ContentIds.Bandage) { ResourceAmount = herbal ? 1f : 0f };

    internal static ItemInstance CreatePill() => new(ContentIds.Pill);

    // §68 r2: a dressing is a physical treatment input, not necessarily pack
    // cargo. The same ready-output search that prevents duplicate crafting
    // also sees a loose bandage or one in reachable dropped clothing while a
    // full pack rejects the ordinary pickup path.
    internal static bool TryFindReachableBandageSource(
        WorldState world, NPCState npc, out WorldObjectState source) =>
        CraftProjectMath.TryFindUsableCompletedOutput(
            world, npc, GoalType.CraftBandage, out source);

    internal static bool TryClaimBandageSource(
        WorldState world, NPCState npc, ObjectId sourceId)
    {
        if (!world.Entities.Objects.TryGetValue(sourceId, out var source) ||
            (source.IsOccupied && source.CurrentUser != npc.Id) ||
            !SourceContainsBandage(world, source))
        {
            return false;
        }

        source.IsOccupied = true;
        source.CurrentUser = npc.Id;
        return true;
    }

    // Consumption happens only when the treatment beat completes. If the plan
    // is interrupted, PlanInterruption merely releases this claim and the
    // exact physical dressing remains in the world.
    internal static bool TrySpendClaimedBandageSource(
        WorldState world, NPCState npc, ObjectId sourceId, out bool herbal)
    {
        herbal = false;
        if (!world.Entities.Objects.TryGetValue(sourceId, out var source) ||
            source.CurrentUser != npc.Id)
        {
            return false;
        }

        if (source.DefinitionId == ContentIds.Bandage &&
            !source.IsCraftProject &&
            (source.CraftWorkRequired <= 0 ||
             source.CraftWorkDone >= source.CraftWorkRequired))
        {
            herbal = source.ResourceAmount >= HerbalMarker;
            WorldObjectMutations.DespawnObject(world, source.Id);
            return true;
        }

        var index = FindPreferredBandageIndex(source.Contents);
        if (index < 0)
        {
            ReleaseBandageSource(world, npc, sourceId);
            return false;
        }

        herbal = source.Contents[index].ResourceAmount >= HerbalMarker;
        source.Contents.RemoveAt(index);
        ReleaseBandageSource(world, npc, sourceId);
        return true;
    }

    internal static void ReleaseBandageSource(
        WorldState world, NPCState npc, ObjectId sourceId)
    {
        if (world.Entities.Objects.TryGetValue(sourceId, out var source) &&
            source.CurrentUser == npc.Id)
        {
            source.IsOccupied = false;
            source.CurrentUser = null;
        }
    }

    // Medkit gauze is spent before herbal wraps, preserving §44 provenance.
    internal static bool TrySpendBandage(NPCState npc, out bool herbal)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < npc.Inventory.Items.Count; i++)
            {
                var item = npc.Inventory.Items[i];
                if (item.DefinitionId != ContentIds.Bandage)
                {
                    continue;
                }

                herbal = item.ResourceAmount >= HerbalMarker;
                if ((pass == 0 && herbal) || (pass == 1 && !herbal))
                {
                    continue;
                }

                npc.Inventory.Items.RemoveAt(i);
                return true;
            }
        }

        herbal = false;
        return false;
    }

    internal static bool TrySpendHerbalBandage(NPCState npc)
    {
        for (var i = 0; i < npc.Inventory.Items.Count; i++)
        {
            var item = npc.Inventory.Items[i];
            if (item.DefinitionId == ContentIds.Bandage && item.ResourceAmount >= HerbalMarker)
            {
                npc.Inventory.Items.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    internal static bool TrySpendPill(NPCState npc)
    {
        for (var i = 0; i < npc.Inventory.Items.Count; i++)
        {
            if (npc.Inventory.Items[i].DefinitionId == ContentIds.Pill)
            {
                npc.Inventory.Items.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    private static bool SourceContainsBandage(WorldState world, WorldObjectState source)
    {
        if (source.DefinitionId == ContentIds.Bandage &&
            !source.IsCraftProject &&
            (source.CraftWorkRequired <= 0 ||
             source.CraftWorkDone >= source.CraftWorkRequired))
        {
            return true;
        }

        // Contents on build/process objects are paid inputs, not a public
        // stash. Only a dropped wearable (Layer != null) exposes its pockets.
        if (source.IsCraftProject || source.Contents.Count == 0 ||
            !world.Content.ObjectDefinitions.TryGetValue(
                source.DefinitionId, out var definition) ||
            definition.Layer == null)
        {
            return false;
        }

        return FindPreferredBandageIndex(source.Contents) >= 0;
    }

    private static int FindPreferredBandageIndex(
        System.Collections.Generic.List<ItemInstance> items)
    {
        // Preserve the inventory rule: pre-made gauze before herbal wraps.
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.DefinitionId != ContentIds.Bandage)
                {
                    continue;
                }

                var herbal = item.ResourceAmount >= HerbalMarker;
                if ((pass == 0 && !herbal) || (pass == 1 && herbal))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    // Blob v41 and older saves stored dressings outside Inventory. Keep the
    // legacy fields in the blob for compatibility, but materialize them once
    // after the inventory has been read.
    internal static void MaterializeLegacyPouch(NPCState npc)
    {
        var total = System.Math.Max(0, npc.Needs.Bandages);
        var herbal = System.Math.Clamp(npc.Needs.HerbalBandages, 0, total);
        for (var i = 0; i < total - herbal; i++)
        {
            npc.Inventory.Items.Add(CreateBandage(herbal: false));
        }

        for (var i = 0; i < herbal; i++)
        {
            npc.Inventory.Items.Add(CreateBandage(herbal: true));
        }

        for (var i = 0; i < System.Math.Max(0, npc.Needs.Pills); i++)
        {
            npc.Inventory.Items.Add(CreatePill());
        }

        npc.Needs.Bandages = 0;
        npc.Needs.HerbalBandages = 0;
        npc.Needs.Pills = 0;
    }
}

}
