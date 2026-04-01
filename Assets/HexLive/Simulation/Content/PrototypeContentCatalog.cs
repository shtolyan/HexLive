using System.Collections.Generic;

namespace HexLive.Simulation.Content
{

public static class PrototypeContentCatalog
{
    public static IReadOnlyDictionary<string, ObjectDefinition> CreateDefaults()
    {
        return new Dictionary<string, ObjectDefinition>
        {
            ["food.apple"] = new ObjectDefinition
            {
                Id = "food.apple",
                DisplayName = "Apple",
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "eat.apple",
                        Type = InteractionType.Eat,

                        DurationTicks = 8,
                        Effects = { HungerDelta = -0.45f, ComfortDelta = 0.05f }
                    }
                },
                Tags = { "Food" }
            },
            ["chair.basic"] = new ObjectDefinition
            {
                Id = "chair.basic",
                DisplayName = "Chair",
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "sit.chair",
                        Type = InteractionType.Sit,

                        DurationTicks = 12,
                        Effects = { ComfortDelta = 0.35f, EnergyDelta = 0.05f }
                    }
                },
                Tags = { "Chair" }
            },
            ["bed.basic"] = new ObjectDefinition
            {
                Id = "bed.basic",
                DisplayName = "Bed",
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "sleep.bed",
                        Type = InteractionType.Sleep,

                        DurationTicks = 20,
                        Effects = { EnergyDelta = 0.5f, ComfortDelta = 0.2f }
                    }
                },
                Tags = { "Bed" }
            },
            ["clothing.coat"] = new ObjectDefinition
            {
                Id = "clothing.coat",
                DisplayName = "Coat",
                Interactions =
                {
                    new InteractionDefinition
                    {
                        Id = "dress.coat",
                        Type = InteractionType.Dress,

                        DurationTicks = 10,
                        Effects = { ThermalDelta = -0.3f, WarmthDelta = 0.4f }
                    }
                },
                Tags = { "Clothing" }
            }
        };
    }
}

}
