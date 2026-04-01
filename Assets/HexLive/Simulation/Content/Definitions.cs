using System.Collections.Generic;

namespace HexLive.Simulation.Content
{

public sealed class ObjectDefinition
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public List<InteractionDefinition> Interactions { get; } = new();

    public List<string> Tags { get; } = new();
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

    public float WarmthDelta { get; set; }
}

public enum InteractionType
{
    Eat,
    Sit,
    Sleep,
    Dress,
    Observe
}

}
