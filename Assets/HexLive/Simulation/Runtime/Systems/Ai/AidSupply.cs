using HexLive.Simulation.Core;
using HexLive.Simulation.Content;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;

namespace HexLive.Simulation.Runtime
{

// Spec §53.7: HELP COSTS SUPPLIES. Until now aid was free — the relief was
// applied straight to the ward and nothing left the helper's pack, so caring
// for the colony was pure upside. Now every kind of help but words spends a
// real resource out of the HELPER's own stores:
//
//   Feed     — a meal from her pack (or a coconut she carries)
//   Hydrate  — one water charge (bottle gulp, or a pierced coconut's water)
//   Treat    — one bandage from her med pouch
//   Medicate — one pill, or a herbal dressing brewed from gathered plantain
//   Console  — free: words cost nothing
//
// The same table answers the other half of §53.7: a girl who means to help but
// has nothing to give goes and FETCHES it first (DecisionSystem turns the aid
// bid into a GetFood / GetWater / GatherHerb+CraftBandage errand).
internal static class AidSupply
{
    // Which kinds of help are paid for out of the pack.
    internal static bool NeedsSupply(AidKind kind) =>
        Spec53.AidCostsSupplies &&
        kind is AidKind.Feed or AidKind.Hydrate or AidKind.Treat or AidKind.Medicate;

    // Can she pay for this help right now? Checked when the aid bid is scored,
    // when the plan picks a ward, and again when the interaction begins.
    internal static bool Has(WorldState world, NPCState npc, AidKind kind)
    {
        if (!NeedsSupply(kind))
        {
            return true;
        }

        return kind switch
        {
            // A ready meal, or a coconut she can open with the blade she carries.
            AidKind.Feed => npc.Inventory.FindFirstFood(world.Content) is not null ||
                DecisionSystem.HasInventoryCoconutMeal(npc),
            // Bottle water, a watered pierced coconut, or a whole nut + blade.
            AidKind.Hydrate => DecisionSystem.HasInventoryCoconutWater(npc),
            AidKind.Treat => MedicalSupplyMath.BandageCount(npc) > 0,
            // Medicine is a pill or a HERBAL dressing (a medkit gauze is not a
            // remedy for sickness — that is what the herb chain is for).
            AidKind.Medicate => MedicalSupplyMath.PillCount(npc) > 0 ||
                MedicalSupplyMath.HerbalBandageCount(npc) > 0,
            _ => true
        };
    }

    // What a completed aid actually took out of the helper's stores, and how
    // much relief that particular item is worth.
    internal readonly struct Spend
    {
        public Spend(string item, float amount, bool herbal)
        {
            Item = item;
            Amount = amount;
            Herbal = herbal;
        }

        // Trace label for the thing that was consumed ("" when nothing was).
        public string Item { get; }

        // Relief magnitude for Feed/Hydrate — the item's own nutrition where it
        // defines one, otherwise the Spec53 flat value.
        public float Amount { get; }

        // Treat/Medicate: a herbal dressing leaves the plantain leaf-wrap decal,
        // a medkit one plain gauze (mirrors self first-aid, spec 44).
        public bool Herbal { get; }
    }

    // Spend the supply for a completed aid. False = she no longer has it (raced
    // away between the start gate and completion) and the caller aborts without
    // applying relief — help must never appear out of nothing.
    internal static bool TrySpend(WorldState world, NPCState npc, AidKind kind, out Spend spend)
    {
        spend = kind switch
        {
            AidKind.Feed => new Spend(string.Empty, Spec53.FeedRelief, false),
            AidKind.Hydrate => new Spend(string.Empty, Spec53.HydrateRelief, false),
            _ => new Spend(string.Empty, 0f, false)
        };

        if (!NeedsSupply(kind))
        {
            return true;
        }

        return kind switch
        {
            AidKind.Feed => TrySpendFood(world, npc, out spend),
            AidKind.Hydrate => TrySpendWater(world, npc, out spend),
            AidKind.Treat => TrySpendMedical(world, npc, kind, out spend),
            AidKind.Medicate => TrySpendMedical(world, npc, kind, out spend),
            _ => true
        };
    }

    internal static ItemInstance SelectItem(WorldState world, NPCState npc, AidKind kind)
    {
        ItemInstance Find(string id) => npc.Inventory.Items.Find(item => item.DefinitionId == id);
        if (!NeedsSupply(kind)) return null;
        switch (kind)
        {
            case AidKind.Feed:
                var food = FoodMath.BestFoodInInventory(world, npc);
                return (food is null ? null : Find(food)) ?? Find(ContentIds.CoconutOpen) ??
                    (DecisionSystem.HasCoconutBlade(npc)
                        ? Find(ContentIds.CoconutPierced) ?? Find(ContentIds.Coconut) : null);
            case AidKind.Hydrate:
                return BottleInventoryMath.FirstDrinkable(npc) ??
                    npc.Inventory.Items.Find(item => item.DefinitionId == ContentIds.CoconutPierced && item.ResourceAmount > 0f) ??
                    (DecisionSystem.HasCoconutBlade(npc) ? Find(ContentIds.Coconut) : null);
            case AidKind.Treat:
                return npc.Inventory.Items.Find(item => item.DefinitionId == ContentIds.Bandage && item.ResourceAmount < 0.5f)
                    ?? Find(ContentIds.Bandage);
            case AidKind.Medicate:
                return Find(ContentIds.Pill) ?? npc.Inventory.Items.Find(
                    item => item.DefinitionId == ContentIds.Bandage && item.ResourceAmount >= 0.5f);
            default: return null;
        }
    }

    internal static void Bind(WorldState world, NPCState npc, AidKind kind)
    {
        npc.Execution.ActionSupply = SelectItem(world, npc, kind);
        npc.Execution.ActionItemsBound = true;
    }

    private static ItemInstance Selected(WorldState world, NPCState npc, AidKind kind)
    {
        if (!npc.Execution.ActionItemsBound) return SelectItem(world, npc, kind);
        var item = npc.Execution.ActionSupply;
        return item is not null && InventoryMath.ContainsReference(npc.Inventory.Items, item) ? item : null;
    }

    private static bool TrySpendFood(WorldState world, NPCState npc, out Spend spend)
    {
        spend = new Spend(string.Empty, Spec53.FeedRelief, false);
        var item = Selected(world, npc, AidKind.Feed);
        if (item is null) return false;
        var id = item.DefinitionId;
        var needsBlade = id == ContentIds.Coconut || id == ContentIds.CoconutPierced;
        if (needsBlade && !DecisionSystem.HasCoconutBlade(npc)) return false;
        InventoryMath.RemoveReference(npc.Inventory.Items, item);
        spend = new Spend(id, needsBlade ? Spec53.FeedRelief : NutritionOf(world, id), false);
        return true;
    }

    private static bool TrySpendWater(WorldState world, NPCState npc, out Spend spend)
    {
        spend = new Spend(string.Empty, Spec53.HydrateRelief, false);
        var item = Selected(world, npc, AidKind.Hydrate);
        if (item is null) return false;
        if (item.DefinitionId == ContentIds.Bottle)
        {
            if (!BottleInventoryMath.ConsumeOne(item, out _)) return false;
        }
        else if (item.DefinitionId == ContentIds.CoconutPierced && item.ResourceAmount > 0f)
            item.ResourceAmount = System.MathF.Max(0f, item.ResourceAmount - 1f);
        else if (item.DefinitionId == ContentIds.Coconut && DecisionSystem.HasCoconutBlade(npc))
            InventoryMath.RemoveReference(npc.Inventory.Items, item);
        else return false;
        spend = new Spend(item.DefinitionId, Spec53.HydrateRelief, false);
        return true;
    }

    private static bool TrySpendMedical(WorldState world, NPCState npc, AidKind kind, out Spend spend)
    {
        spend = new Spend(string.Empty, 0f, false);
        var item = Selected(world, npc, kind);
        if (item is null) return false;
        var herbal = item.DefinitionId == ContentIds.Bandage && item.ResourceAmount >= 0.5f;
        if (kind == AidKind.Medicate && item.DefinitionId != ContentIds.Pill && !herbal) return false;
        InventoryMath.RemoveReference(npc.Inventory.Items, item);
        spend = new Spend(item.DefinitionId, 0f, herbal);
        return true;
    }

    // §54.17: the shared item-nutrition rule lives in FoodMath now.
    private static float NutritionOf(WorldState world, string definitionId) =>
        FoodMath.NutritionOf(world, definitionId);
}

}
