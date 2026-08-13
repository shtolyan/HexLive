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
            AidKind.Hydrate => TrySpendWater(npc, out spend),
            AidKind.Treat => TrySpendBandage(npc, out spend),
            AidKind.Medicate => TrySpendMedicine(npc, out spend),
            _ => true
        };
    }

    private static bool TrySpendFood(WorldState world, NPCState npc, out Spend spend)
    {
        spend = new Spend(string.Empty, Spec53.FeedRelief, false);

        // A ready-to-eat item feeds her by its OWN nutrition — sharing meat is
        // worth more than sharing a scrap; §54.17: the BEST item, same rule
        // the donor would use for herself.
        if (FoodMath.BestFoodInInventory(world, npc) is { } foodId)
        {
            npc.Inventory.Items.Remove(foodId);
            spend = new Spend(foodId, NutritionOf(world, foodId), false);
            return true;
        }

        // Otherwise a coconut out of the pack: an open one is handed over as is,
        // a whole/pierced one she splits with the blade she is carrying.
        if (npc.Inventory.Items.Remove(ContentIds.CoconutOpen))
        {
            spend = new Spend(ContentIds.CoconutOpen, NutritionOf(world, ContentIds.CoconutOpen), false);
            return true;
        }

        if (DecisionSystem.HasCoconutBlade(npc))
        {
            foreach (var id in new[] { ContentIds.CoconutPierced, ContentIds.Coconut })
            {
                if (npc.Inventory.Items.Remove(id))
                {
                    spend = new Spend(id, Spec53.FeedRelief, false);
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TrySpendWater(NPCState npc, out Spend spend)
    {
        spend = new Spend(string.Empty, Spec53.HydrateRelief, false);

        // One gulp out of the bottle (spec §52: the bottle empties on the last).
        if (npc.BottleWater != WaterKind.None && npc.BottleCharges > 0)
        {
            npc.BottleCharges--;
            if (npc.BottleCharges <= 0)
            {
                npc.BottleCharges = 0;
                npc.BottleWater = WaterKind.None;
            }

            spend = new Spend(ContentIds.Bottle, Spec53.HydrateRelief, false);
            return true;
        }

        // A pierced coconut she carries keeps its water like a canteen.
        foreach (var item in npc.Inventory.Items)
        {
            if (item.DefinitionId == ContentIds.CoconutPierced && item.ResourceAmount > 0f)
            {
                item.ResourceAmount = System.MathF.Max(0f, item.ResourceAmount - 1f);
                spend = new Spend(ContentIds.CoconutPierced, Spec53.HydrateRelief, false);
                return true;
            }
        }

        // Last: pierce a whole nut for her — the nut is gone either way.
        if (DecisionSystem.HasCoconutBlade(npc) && npc.Inventory.Items.Remove(ContentIds.Coconut))
        {
            spend = new Spend(ContentIds.Coconut, Spec53.HydrateRelief, false);
            return true;
        }

        return false;
    }

    private static bool TrySpendBandage(NPCState npc, out Spend spend)
    {
        spend = new Spend(string.Empty, 0f, false);
        if (!MedicalSupplyMath.TrySpendBandage(npc, out var herbal))
        {
            return false;
        }

        // Spec 44 order: medkit dressings first, so the leaf-wrap decal always
        // means someone actually went and gathered plantain.
        spend = new Spend(herbal ? ContentIds.Bandage : ContentIds.Medkit, 0f, herbal);
        return true;
    }

    private static bool TrySpendMedicine(NPCState npc, out Spend spend)
    {
        spend = new Spend(string.Empty, 0f, false);
        if (MedicalSupplyMath.TrySpendPill(npc))
        {
            spend = new Spend(ContentIds.Pill, 0f, false);
            return true;
        }

        if (MedicalSupplyMath.TrySpendHerbalBandage(npc))
        {
            spend = new Spend(ContentIds.Bandage, 0f, true);
            return true;
        }

        return false;
    }

    // §54.17: the shared item-nutrition rule lives in FoodMath now.
    private static float NutritionOf(WorldState world, string definitionId) =>
        FoodMath.NutritionOf(world, definitionId);
}

}
