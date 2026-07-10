using System.Collections.Generic;

namespace HexLive.Simulation.Content
{

public sealed class ObjectDefinition
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public List<InteractionDefinition> Interactions { get; } = new();

    public List<string> Tags { get; } = new();

    public ProduceDefinition? Produce { get; set; }

    // Spec 31C.7: > 0 blocks every junction within this world-unit radius
    // of the anchor (solid furniture); 0 blocks the anchor only.
    public float ObstacleRadius { get; set; }

    // Spec 31A.5B: wearable metadata (null = not wearable).
    public WearLayer? Layer { get; set; }

    public List<BodyPart> Covers { get; } = new();
}

// Spec 31A.5B (molly port): three clothing layers.
public enum WearLayer
{
    Underwear,
    Wear,
    Outerwear
}

// Spec 19.3C (molly bones, simplified): body zones for wounds and coverage.
public enum BodyPart
{
    Head,
    Torso,
    Pelvis,
    ArmL,
    ArmR,
    LegL,
    LegR
}

public sealed class ProduceDefinition
{
    public string ProducedDefinitionId { get; set; } = string.Empty;

    public int IntervalTicks { get; set; }

    public int MaxConcurrent { get; set; }

    public int MaxDistanceTiles { get; set; }
}

public sealed class InteractionDefinition
{
    public string Id { get; set; } = string.Empty;

    public InteractionType Type { get; set; }

    public int DurationTicks { get; set; }

    public InteractionEffects Effects { get; } = new();
}

public sealed class InteractionEffects
{
    public float HungerDelta { get; set; }

    public float EnergyDelta { get; set; }

    public float ComfortDelta { get; set; }

    public float ThermalDelta { get; set; }

    public float ThirstDelta { get; set; }

    public float WarmthDelta { get; set; }

    // Spec 29C.4: fraction of incoming damage absorbed when equipped.
    public float ArmorDelta { get; set; }
}

public enum InteractionType
{
    Eat,
    Drink,
    PickUp,
    Sit,
    Sleep,
    Dress,
    Undress,
    Fuel,
    Craft,
    Harvest,
    Build,
    Bury,
    Observe,
    Talk,
    Hang,
    FillBottle // spec 29H: charge the water bottle at a source
}

}
