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

    // Spec §52: inventory slots this item grants while worn (garments only).
    // The pack has no base capacity — the body has 2 hands and each worn piece
    // adds its pockets. 0 for non-wearables and accessories with no pockets.
    public int InventoryCapacity { get; set; }
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

    // Spec §54: data-driven harvest/process/butcher output. When non-empty, the
    // completion handler spawns these drops (scattered on the ground) instead of
    // the old hardcoded tag switch. One verb, one yield list — a palm's chop
    // drops logs+leaves, a log's Process drops sticks, a carcass's Butcher drops
    // meat+hide. Empty ⇒ this verb yields nothing (e.g. Eat/Sit/Sleep).
    public List<HarvestDrop> Yields { get; } = new();
}

// Spec §54: one line of a Yields table — "spawn Count of DefinitionId". Scatter
// = land on distinct nearby junctions (logs around the stump) rather than pile
// at the actor's feet.
public sealed class HarvestDrop
{
    public string DefinitionId { get; set; } = string.Empty;

    public int Count { get; set; } = 1;

    public bool Scatter { get; set; } = true;
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
    Process,    // spec §54: split a log into sticks (in the field, needs an axe)
    Butcher,    // spec §54: knife a carcass/corpse into meat + hide
    Build,
    Bury,
    Observe,
    Talk,
    Hang,
    FillBottle, // spec 29H: charge the water bottle at a source
    BuildRaft,  // spec 40.15: haul logs to the escape raft
    FeedOther,     // spec 53: share a meal with a starving housemate
    TreatOther,    // spec 53: dress a wounded housemate's wound
    MedicateOther, // spec 53: hand a pill to a sick / gravely weak housemate
    ConsoleOther   // spec 53: sit with a grieving / stressed housemate
}

}
