using HexLive.Simulation.Common;

namespace HexLive.Simulation.Wildlife
{

// Spec 29C.3: a dog is a three-state machine, not an NPC — no needs,
// plans, or perception pipeline.
public sealed class DogState
{
    public int Id { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public JunctionId Junction { get; set; }

    public Float2 Position { get; set; } = Float2.Zero;

    public float Health { get; set; } = 0.9f;

    public DogStatus Status { get; set; } = DogStatus.Roaming;

    public EntityId? TargetNpc { get; set; }
}

public enum DogStatus
{
    Roaming,
    Chasing,
    Fighting
}

// Spec 29F.1: prey — grazes, hops, flees; spooked after a missed strike.
public sealed class RabbitState
{
    public int Id { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public JunctionId Junction { get; set; }

    public Float2 Position { get; set; } = Float2.Zero;

    public int SpookedUntilTick { get; set; }
}

}
