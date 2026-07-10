using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.AI
{

public sealed class PerceptionSnapshot
{
    public SelfState Self { get; } = new();

    public List<PerceivedObject> Objects { get; } = new();

    public List<PerceivedAgent> Agents { get; } = new();

    public PerceivedEnvironment Environment { get; } = new();

    public int LastUpdatedTick { get; set; }
}

public sealed class SelfState
{
    public float Hunger { get; set; }

    public float Energy { get; set; }

    public float Comfort { get; set; }

    public float Social { get; set; }

    public float ThermalDiscomfort { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public FragmentId Fragment { get; set; }
}

public sealed class PerceivedObject
{
    public ObjectId Id { get; set; }

    public string DefinitionId { get; set; } = string.Empty;

    // True when the entry comes from spatial memory, not current sight
    // (spec 27.14/27.18A): occupancy is then assumed, not observed.
    public bool FromMemory { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public float Distance { get; set; }

    public bool IsReachable { get; set; }

    public bool IsOccupied { get; set; }

    public EntityId? OccupiedBy { get; set; }

    public List<InteractionType> AvailableInteractions { get; } = new();
}

public sealed class PerceivedAgent
{
    public EntityId Id { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public float Distance { get; set; }

    public bool CanSee { get; set; }

    public bool CanHear { get; set; }

    public JunctionId? Junction { get; set; }

    public bool IsReachable { get; set; }

    // Busy = mid-interaction other than Talk (talking agents stay approachable).
    public bool IsBusy { get; set; }

    // Walking agents are not talk targets in v1 (no chasing, spec 28.15A).
    public bool IsMoving { get; set; }

    public RelationshipSummary Relationship { get; } = new();
}

public sealed class PerceivedEnvironment
{
    public float Temperature { get; set; }

    public bool IsCrowded { get; set; }

    public bool IsPrivate { get; set; }

    public int NearbyAgentsCount { get; set; }
}

}
