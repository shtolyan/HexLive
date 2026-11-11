using System.Collections.Generic;

namespace HexLive.Simulation.Content
{

/// <summary>Portable apartment finds. Acquisition is owned by the portal.</summary>
public static class ModernLootCatalog
{
    public static bool IsApartmentTool(string id) => id is
        ContentIds.KitchenKnife or ContentIds.BaseballBat or ContentIds.SteelHatchet or
        ContentIds.Flashlight or ContentIds.DisposableLighter or ContentIds.WindproofLighter or
        ContentIds.ClawHammer or ContentIds.FoldingSaw;

    public static bool IsBulkyTool(string id) =>
        id is ContentIds.BaseballBat or ContentIds.FoldingSaw or ContentIds.SteelHatchet;

    private static readonly (string Id, string Name)[] Furniture =
    {
        ("furniture.modern_bed", "Apartment bed"),
        ("furniture.modern_fridge", "Refrigerator"),
        ("furniture.modern_shelf", "Shelf rack"),
        ("furniture.modern_table", "Dining table"),
        ("furniture.modern_chair", "Dining chair"),
        ("furniture.modern_sofa", "Sofa"),
        ("furniture.modern_tv", "Television"),
    };

    public static bool IsFurniture(string id)
    {
        foreach (var (furnitureId, _) in Furniture)
            if (id == furnitureId) return true;
        return false;
    }

    public static bool IsModernProp(string id) =>
        id is ContentIds.Apple or ContentIds.Banana or ContentIds.DollarBills or ContentIds.KitchenLadle or
            ContentIds.PhotoFrame or ContentIds.BallpointPen or ContentIds.AlarmClock or
            ContentIds.DrinkingGlass or ContentIds.CeramicSaucer or ContentIds.ClothesHanger or ContentIds.TeddyBear ||
        IsApartmentTool(id) || IsFurniture(id) || ModernFoodCatalog.IsFridgeFood(id);

    internal static void AppendDefinitions(Dictionary<string, ObjectDefinition> definitions)
    {
        AddPickup(ContentIds.KitchenKnife, "Kitchen knife", "Tool", "Weapon", "Knife");
        AddPickup(ContentIds.BaseballBat, "Baseball bat", "Tool", "Weapon");
        AddPickup(ContentIds.SteelHatchet, "Steel hatchet", "Tool", "Weapon");
        AddPickup(ContentIds.ClawHammer, "Claw hammer", "Tool", "Hammer");
        AddPickup(ContentIds.FoldingSaw, "Folding saw", "Tool", "Saw");
        AddPickup(ContentIds.DisposableLighter, "Disposable lighter", "Tool");
        AddPickup(ContentIds.WindproofLighter, "Windproof lighter", "Tool");
        var flashlight = AddPickup(ContentIds.Flashlight, "Flashlight", "Tool");
        flashlight.Interactions.Add(new InteractionDefinition
        {
            Id = "flashlight.on", Type = InteractionType.SwitchFlashlight, DurationTicks = 1,
        });
        flashlight.Interactions.Add(new InteractionDefinition
        {
            Id = "flashlight.off", Type = InteractionType.SwitchFlashlight, DurationTicks = 1,
        });
        AddPickup(ContentIds.DollarBills, "Dollar bills");
        AddPickup(ContentIds.KitchenLadle, "Kitchen ladle");
        AddPickup(ContentIds.PhotoFrame, "Photo frame");
        AddPickup(ContentIds.BallpointPen, "Ballpoint pen");
        AddPickup(ContentIds.AlarmClock, "Alarm clock");
        var glass = AddPickup(ContentIds.DrinkingGlass, "Drinking glass", "Tool");
        glass.Interactions.Add(new InteractionDefinition
        {
            Id = "drink.glass", Type = InteractionType.Drink, DurationTicks = 8,
        });
        AddPickup(ContentIds.CeramicSaucer, "Ceramic saucer");
        AddPickup(ContentIds.ClothesHanger, "Clothes hanger");
        AddPickup(ContentIds.TeddyBear, "Teddy bear");
        AddFruit(ContentIds.Apple, "Apple", .22f, .07f);
        AddFruit(ContentIds.Banana, "Banana", .29f, .025f);
        ModernFoodCatalog.AppendDefinitions(definitions);
        foreach (var (id, name) in Furniture)
        {
            var furniture = AddPickup(id, name, "Furniture");
            if (id == "furniture.modern_shelf") furniture.SolidRadius = .38f;
            if (id is not ("furniture.modern_chair" or "furniture.modern_sofa")) continue;
            furniture.Tags.Add("Chair");
            furniture.Interactions.Add(new InteractionDefinition
            {
                Id = $"sit.{id}", Type = InteractionType.Sit, DurationTicks = 70,
                Effects = { ComfortDelta = Runtime.SimBalance.ChairComfort }
            });
        }

        ObjectDefinition AddPickup(string id, string name, params string[] tags)
        {
            var definition = new ObjectDefinition { Id = id, DisplayName = name, Tags = { "Modern" } };
            definition.Tags.AddRange(tags);
            definition.Interactions.Add(new InteractionDefinition
            {
                Id = $"pickup.{id}", Type = InteractionType.PickUp, DurationTicks = 4
            });
            definitions.Add(id, definition);
            return definition;
        }

        void AddFruit(string id, string name, float hunger, float thirst)
        {
            var definition = AddPickup(id, name, "Food");
            definition.Interactions.Add(new InteractionDefinition
            {
                Id = $"eat.{id}", Type = InteractionType.Eat, DurationTicks = 8,
                Effects = { HungerDelta = -hunger, ThirstDelta = -thirst }
            });
        }
    }
}

}
