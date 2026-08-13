using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;

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
