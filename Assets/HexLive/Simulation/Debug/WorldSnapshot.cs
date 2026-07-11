using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Debug
{

public sealed class WorldSnapshot
{
    public int Tick { get; set; }

    public float Temperature { get; set; }

    public string Clock { get; set; } = string.Empty;

    public string DayPhase { get; set; } = string.Empty;

    public float UvIndex { get; set; }

    public bool IsRaining { get; set; }

    // Spec 40.15: logs hauled to the escape raft, and the target — for a HUD
    // "escape progress" readout. At Progress >= Target the colony can leave.
    public int RaftProgress { get; set; }
    public int RaftTarget { get; set; }

    public List<TileSnapshot> Tiles { get; } = new();

    public List<JunctionSnapshot> Junctions { get; } = new();

    public List<ObjectSnapshot> Objects { get; } = new();

    public List<NpcSnapshot> Npcs { get; } = new();

    public List<DogSnapshot> Dogs { get; } = new();

    public List<CrabSnapshot> Crabs { get; } = new();

    public List<TraceEventSnapshot> TraceEvents { get; } = new();
}

public sealed class CrabSnapshot
{
    public int Id { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public Float2 Position { get; set; } = Float2.Zero;
}

public sealed class DogSnapshot
{
    public int Id { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public Float2 Position { get; set; } = Float2.Zero;

    public float Health { get; set; }

    public string Status { get; set; } = string.Empty;
}

public sealed class TileSnapshot
{
    public TileCoord Coord { get; set; } = TileCoord.Zero;

    public bool Walkable { get; set; }

    public bool Blocked { get; set; }

    public bool Indoor { get; set; }

    public bool Water { get; set; }

    public int Elevation { get; set; }
}

public sealed class JunctionSnapshot
{
    public JunctionId Id { get; set; }

    public Float2 WorldPosition { get; set; } = Float2.Zero;

    public List<TileCoord> Tiles { get; } = new();

    public bool Blocked { get; set; }

    public bool Occupied { get; set; }

    public bool Reserved { get; set; }

    public List<JunctionId> Neighbors { get; } = new();
}

public sealed class ObjectSnapshot
{
    public ObjectId Id { get; set; }

    public string DefinitionId { get; set; } = string.Empty;

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    // Spec 29E.3: fuel ticks. For a campfire, > 0 means lit/burning.
    public float ResourceAmount { get; set; }

    public List<JunctionId> Junctions { get; } = new();
}

public sealed class NpcSnapshot
{
    public EntityId Id { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string ActorMesh { get; set; } = string.Empty;

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public Float2 Position { get; set; } = Float2.Zero;

    public float RotationDegrees { get; set; }

    public float Health { get; set; }

    public bool IsFighting { get; set; }

    public List<string> BodyParts { get; } = new();

    public string WorstBodyPart { get; set; } = string.Empty;

    public float Hunger { get; set; }

    public float Thirst { get; set; }

    public float Energy { get; set; }

    public float Comfort { get; set; }

    public float Social { get; set; }

    public float ThermalDiscomfort { get; set; }

    // Spec 29C.10: signed thermal comfort for the UI — 0 ideal, - cold, + hot.
    public float ThermalComfort { get; set; }

    // Spec 40.1: stamina 0..1 — the energy to act (UI + panting cue).
    public float Stamina { get; set; }

    // Spec 40.6: hygiene 1=clean..0=filthy (UI + grime tint).
    public float Hygiene { get; set; }

    // Spec 40.2: blood 0..1 — bleeds from bad wounds; 0 = death (UI).
    public float Blood { get; set; }

    // Spec 40.7: tan 0=pale..1=dark — sun on bare skin (skin-paint cue).
    public float TanLevel { get; set; }

    // Spec 40.3: bandages left in the med pouch (UI).
    public int Bandages { get; set; }
    public int Pills { get; set; }
    public float Sunburn { get; set; }

    // Spec 40.9: authoritative injury-locomotion hint for the presentation
    // pose layer, derived from body damage + faint. One of: Faint, Crawl,
    // Limp, ArmHang, HeadClutch, Upright.
    public string PostureHint { get; set; } = "Upright";

    // Spec 40.1: winded — stamina spent to the floor. Drives the panting
    // pose/breath in presentation. Derived (Stamina < 0.15), export-only.
    public bool Winded { get; set; }

    // Spec 40.13: knocked out — the presentation lays the body limp.
    public bool IsFainted { get; set; }

    // Spec 40.13: stress 0=calm..1=breaking point (UI).
    public float Stress { get; set; }

    public string CurrentGoal { get; set; } = string.Empty;

    public string PlanStatus { get; set; } = string.Empty;

    public string MovementStatus { get; set; } = string.Empty;

    public string ExecutionStatus { get; set; } = string.Empty;

    public string CurrentInteraction { get; set; } = string.Empty;

    public TileCoord? TargetTile { get; set; }

    public bool IsStarving { get; set; }

    public List<string> InventoryItems { get; } = new();

    public List<string> WornItems { get; } = new();

    // Spec 40.11: "definitionId\tdurability" per worn garment — for the
    // character panel's wear progress bars.
    public List<string> WornDurability { get; } = new();

    public int InventoryCapacity { get; set; }

    public int? GoalLockEndTick { get; set; }

    public List<string> CooldownGoals { get; } = new();

    public List<string> Relationships { get; } = new();

    public List<RelationshipSnapshot> RelationshipDetails { get; } = new();

    public int KnownObjectCount { get; set; }

    public List<string> KnownObjects { get; } = new();

    public List<JunctionId> Path { get; } = new();

    public List<GoalScoreSnapshot> GoalScores { get; } = new();
}

public sealed class RelationshipSnapshot
{
    public int OtherId { get; set; }

    public string OtherName { get; set; } = string.Empty;

    public float Trust { get; set; }

    public float Familiarity { get; set; }

    public float Affinity { get; set; }
}

public sealed class GoalScoreSnapshot
{
    public string Goal { get; set; } = string.Empty;

    public float FinalScore { get; set; }
}

public sealed class TraceEventSnapshot
{
    public int Tick { get; set; }

    public int? EntityId { get; set; }

    public string Type { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}

}
