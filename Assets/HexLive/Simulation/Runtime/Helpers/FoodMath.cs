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
internal static class FoodMath
{
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
        BuildSiteMath.HangingMeat(fire, ContentIds.MeatRaw) +
        BuildSiteMath.HangingMeat(fire, ContentIds.MeatCooked) <
        SimBalance.CampfireSpitCapacity;

    // What a GetFood CANDIDATE is worth in hunger, before walking to it.
    // A campfire is only a valid GetFood target while cooked meat hangs on
    // its spit (IsValidTargetFor), so it reads as a cooked chunk. Raw meat is
    // worth a cooked chunk when the colony can actually roast it — otherwise
    // it is a gamble valued at the §53 flat relief, below an open coconut.
    public static float ProspectiveNutrition(WorldState world, string definitionId, bool fireUsable)
    {
        if (world.Content.ObjectDefinitions.TryGetValue(definitionId, out var definition) &&
            definition.Tags.Contains("Campfire"))
        {
            return SimBalance.CookedMeatHunger;
        }

        if (definitionId == ContentIds.MeatRaw)
        {
            return fireUsable ? SimBalance.CookedMeatHunger : Spec53.FeedRelief;
        }

        return NutritionOf(world, definitionId);
    }
}

}
