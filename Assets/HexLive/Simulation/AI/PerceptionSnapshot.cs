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

    // Spec §60: out cold (faint or coma) — like a sleeper, never a chat
    // partner. Kept separate from IsBusy so §53 Aid can still target her:
    // unlike a sleeper she cannot wake to help herself.
    public bool IsUnconscious { get; set; }

    // Spec §53: how badly this neighbour needs help (0 = fine, 1 = dying) and
    // the single most-urgent HELPABLE kind of aid. Populated by the perception
    // build so the Aid goal can bid on, and route to, the worst-off housemate
    // without re-scanning every agent's full state.
    public float Suffering { get; set; }

    public AidKind AidKind { get; set; } = AidKind.None;

    public RelationshipSummary Relationship { get; } = new();
}

// Spec §53: the kind of care a suffering neighbour needs, in priority order of
// urgency. The Aid goal picks the neighbour with the highest Suffering and
// performs the matching interaction.
public enum AidKind
{
    None,
    Feed,     // starving — a well-fed girl shares a meal
    Hydrate,  // parched — bring her water (thirst kills faster than hunger)
    Treat,    // wounded / bleeding — dress the wound
    Medicate, // sick or gravely weak — hand over a pill
    Console   // grieving or breaking under stress — sit with her
}

public sealed class PerceivedEnvironment
{
    public float Temperature { get; set; }

    public bool IsCrowded { get; set; }

    public bool IsPrivate { get; set; }

    public int NearbyAgentsCount { get; set; }
}

}
