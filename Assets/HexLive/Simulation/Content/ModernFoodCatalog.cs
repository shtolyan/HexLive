using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Content
{

/// <summary>§176: perishable-looking apartment finds share ordinary food and container rules.</summary>
public static class ModernFoodCatalog
{
    // #747: twenty food cells. Capacity is derived at runtime, never saved, so
    // fridges in existing saves and on the wire grow without a migration.
    internal const int FridgeCapacity = 20;

    private static readonly (string Id, string Name, float Hunger, float Thirst, float Comfort, int EatTicks)[] Foods =
    {
        (ContentIds.StrawberryYogurt, "Strawberry yogurt", .21f, .06f, .025f, 10),
        (ContentIds.CheeseWedge, "Cheese wedge", .29f, 0f, .018f, 12),
        (ContentIds.ChocolateBar, "Chocolate bar", .24f, 0f, .07f, 8),
        (ContentIds.HamSandwich, "Ham sandwich", .43f, 0f, .03f, 14),
        (ContentIds.Orange, "Orange", .18f, .115f, .018f, 10),
        (ContentIds.CannedPeaches, "Canned peaches", .29f, .105f, .045f, 13),
    };

    public static bool IsFridgeFood(string id) => Array.Exists(Foods, food => food.Id == id);

    internal static void AppendDefinitions(Dictionary<string, ObjectDefinition> definitions)
    {
        foreach (var food in Foods)
        {
            var definition = new ObjectDefinition
            {
                Id = food.Id,
                DisplayName = food.Name,
                Tags = { "Modern", "Food" }
            };
            definition.Interactions.Add(new InteractionDefinition
            {
                Id = $"pickup.{food.Id}", Type = InteractionType.PickUp, DurationTicks = 4,
            });
            definition.Interactions.Add(new InteractionDefinition
            {
                Id = $"eat.{food.Id}", Type = InteractionType.Eat, DurationTicks = food.EatTicks,
                Effects =
                {
                    HungerDelta = -food.Hunger, ThirstDelta = -food.Thirst, ComfortDelta = food.Comfort,
                },
            });
            definitions.Add(food.Id, definition);
        }
    }

    internal static void StockRefrigerator(WorldState world, WorldObjectState refrigerator, int day)
    {
        var available = Foods.Select(food => food.Id).Where(world.Content.ObjectDefinitions.ContainsKey).ToList();
        int count = Math.Min(available.Count, 2 + Math.Min(2, (int)(MathUtil.Hash01(world.Seed, day, 0, 17610) * 3f)));
        for (int index = 0; index < count; index++)
        {
            int pick = Math.Min(available.Count - 1, (int)(MathUtil.Hash01(world.Seed, day, index + 1, 17610) * available.Count));
            refrigerator.Contents.Add(new ItemInstance(available[pick]));
            available.RemoveAt(pick);
        }
    }
}

}
