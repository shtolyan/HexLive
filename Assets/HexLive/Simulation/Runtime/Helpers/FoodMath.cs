using HexLive.Simulation.Core;
using HexLive.Simulation.Content;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;

namespace HexLive.Simulation.Runtime
{

// §54.17: item-level food value. Before this, no food choice anywhere looked
// at nutrition — Eat took the FIRST edible item in the pack (insertion order)
// and GetFood took the NEAREST Food-tagged object, so cooked meat's edge over
// a coconut half was invisible to both and the colony lived on coconuts no
// matter how much meat it had. These are the only readers of an item's hunger
// payoff; keep them the only ones.
public static class FoodMath
{
    public const int BiteGrams = 100;
    public const int MeatPortionGrams = 300;
    private const int EatPortionTicks = 24;

    public static int EatTicks(string id, float amount) =>
        System.Math.Max(1, (int)System.MathF.Ceiling(
            System.MathF.Min(BiteGrams, EdibleGrams(id, amount)) * EatPortionTicks / BiteGrams));

    internal static float EatTick(string id, ref float amount) =>
        SpendFood(id, ref amount, (float)BiteGrams / EatPortionTicks);

    private static float EdibleGrams(string id, float amount)
    {
        // Zero and one were whole-item markers in older saves. A negative
        // fraction records a real remainder of at most one gram unambiguously.
        if (amount < 0f) return -amount;
        if (amount is 0f or 1f) return id == ContentIds.MeatCooked ? MeatPortionGrams : BiteGrams;
        return amount;
    }

    private static float SpendFood(string id, ref float amount, float grams)
    {
        var available = EdibleGrams(id, amount);
        var eaten = System.MathF.Min(available, grams);
        var left = available - eaten;
        if (left < 0.001f) left = 0f;
        amount = left <= 1f ? -left : left;
        return eaten / BiteGrams;
    }

    public static bool CanEat(ObjectDefinition? definition) =>
        definition is not null && definition.Interactions.Exists(interaction => interaction.Type == InteractionType.Eat);

    public static int GramsRemaining(string definitionId, float resourceAmount)
    {
        if (HumanMeatCatalog.IsRaw(definitionId))
            return MealServing.PortionGrams(HumanMeatCatalog.Cooked(definitionId), 0f);
        if (MealServing.IsReadyToServe(definitionId))
            return MealServing.PortionGrams(definitionId, resourceAmount);
        return (int)System.MathF.Ceiling(EdibleGrams(definitionId, resourceAmount));
    }

    internal static float SpendInventoryBite(NPCState npc, ItemInstance item)
    {
        var amount = item.ResourceAmount;
        var share = SpendFood(item.DefinitionId, ref amount, BiteGrams);
        item.ResourceAmount = amount;
        if (amount == 0f) InventoryMath.RemoveReference(npc.Inventory.Items, item);
        return share;
    }

    // The Hunger the food's own Eat interaction removes; the §53 flat value
    // when the definition declares none.
    public static float NutritionOf(WorldState world, string definitionId)
    {
        if (world.Content.ObjectDefinitions.TryGetValue(definitionId, out var definition))
        {
            foreach (var interaction in definition.Interactions)
            {
                if (interaction.Type == InteractionType.Eat && interaction.Effects.HungerDelta < 0f)
                {
                    return -interaction.Effects.HungerDelta;
                }
            }
        }

        return Spec53.FeedRelief;
    }

    // The most nutritious ready-to-eat item in the pack (ties keep insertion
    // order, so packs without meat behave exactly as the old FindFirstFood).
    public static string? BestFoodInInventory(WorldState world, NPCState npc)
    {
        string? best = null;
        var bestNutrition = 0f;
        foreach (var item in npc.Inventory.Items)
        {
            if (!world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var definition))
            {
                continue;
            }

            foreach (var interaction in definition.Interactions)
            {
                if (interaction.Type != InteractionType.Eat)
                {
                    continue;
                }

                var nutrition = System.MathF.Max(0f, -interaction.Effects.HungerDelta);
                if (best is null || nutrition > bestNutrition + 0.001f)
                {
                    best = item.DefinitionId;
                    bestNutrition = nutrition;
                }

                break;
            }
        }

        return best;
    }

    // The spit can take one more chunk: built, and a skewer slot is open.
    // (Fuel is the caller's business — an unlit fire pauses the roast but the
    // hang itself is still worth planning around a fire she can relight.)
    public static bool SpitHasFreeHook(WorldObjectState fire) =>
        BuildSiteMath.CampfireSpitComplete(fire) &&
        BuildSiteMath.UsedSpitHooks(fire) < SimBalance.CampfireSpitCapacity;

    // What a GetFood CANDIDATE is worth in hunger, before walking to it.
    // A campfire is only a valid GetFood target while cooked meat hangs on
    // its spit (IsValidTargetFor), so it reads as a cooked chunk. Raw meat is
    // worth the future cooked yield when there is a usable fire. Roasted
    // preparations only pass the planner's filter when a serving surface exists.
    public static float ProspectiveNutrition(WorldState world, string definitionId, bool fireUsable)
    {
        if (world.Content.ObjectDefinitions.TryGetValue(definitionId, out var definition) &&
            definition.HasTag("Campfire"))
        {
            return SimBalance.CookedMeatHunger;
        }

        if (HumanMeatCatalog.IsRaw(definitionId))
            return fireUsable ? SimBalance.CookedMeatHunger : 0f;
        if (MealServing.IsReadyToServe(definitionId))
            return SimBalance.CookedMeatHunger;

        return NutritionOf(world, definitionId);
    }
}

}
