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

    // Spec 43: sun path for the renderer — align the directional light to
    // the sim's shadow math so visual shadows match sim shade.
    public Float2 SunDirection { get; set; } = Float2.Zero;
    public float SunElevationDegrees { get; set; }

    public List<TileSnapshot> Tiles { get; } = new();

    public List<JunctionSnapshot> Junctions { get; } = new();

    public List<ObjectSnapshot> Objects { get; } = new();

    public List<NpcSnapshot> Npcs { get; } = new();

    public List<DogSnapshot> Dogs { get; } = new();

    public List<CrabSnapshot> Crabs { get; } = new();

    public List<SharkSnapshot> Sharks { get; } = new();

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

// Spec 40.18: a shark patrolling the water (presentation renders a fin/model).
public sealed class SharkSnapshot
{
    public int Id { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public Float2 Position { get; set; } = Float2.Zero;
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

    // Spec 40.17: this walkable junction straddles a one-level elevation step —
    // presentation draws a climb-seam marker ("точечки на шве") and plays the
    // hand-over-hand climb here.
    public bool IsClimbSeam { get; set; }

    // Spec 40.18: an opened swim junction — presentation draws water an NPC can
    // cross (and sites the swim animation / shark patrol).
    public bool IsSwimmable { get; set; }

    public List<JunctionId> Neighbors { get; } = new();
}

public sealed class ObjectSnapshot
{
    public ObjectId Id { get; set; }

    public string DefinitionId { get; set; } = string.Empty;

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    // Spec 29E.3: fuel ticks. For a campfire, > 0 means lit/burning.
    public float ResourceAmount { get; set; }

    // Spec 40.13: whose body/marker this is (corpse.npc / grave.npc carry the
    // dead NPC's id in CurrentUser) — lets the view adopt the actor's ragdoll.
    public int? OwnerNpcId { get; set; }

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

    // Spec 40.8/40.6: zones with NO garment coverage — skin decals (wounds,
    // dirt, sweat) may only appear on these; clothing hides the rest.
    public List<string> UncoveredParts { get; } = new();

    // Spec 44: zones dressed with a herbal bandage — leaf-wrap decal.
    public List<string> BandagedZones { get; } = new();

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

    // Iter 28: sitting at a one-step ledge junction — the presentation
    // lifts the body so the butt rests on the upper step. Export-only.
    public bool IsLedgeSit { get; set; }

    // Spec 41.5: wake-up grace — standing still, coming to her senses after
    // sleep; presentation holds the idle so the get-up clip can finish.
    public bool IsWaking { get; set; }

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

    // Spec 35.5: "definitionId\twetness" per worn garment — rain soaks cloth,
    // fire/racks dry it; presentation shows a wet sheen that fades as it dries.
    public List<string> WornWetness { get; } = new();

    // Spec 40.8B: "Zone|seed|heal01" per open wound — each maps to ONE decal
    // whose exact spot/look derive from seed and whose alpha fades with heal.
    public List<string> Wounds { get; } = new();

    // Spec 40.8B: HP fraction held hostage by open wounds (Fallout-style red
    // bar segment — regen can't cross it; it shrinks as wounds close).
    public float WoundLockedHp { get; set; }

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
