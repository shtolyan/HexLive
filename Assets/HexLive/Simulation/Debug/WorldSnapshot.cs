using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Debug
{

public sealed class WorldSnapshot
{
    public int Tick { get; set; }

    public float Temperature { get; set; }

    public List<TileSnapshot> Tiles { get; } = new();

    public List<PointSnapshot> Points { get; } = new();

    public List<ObjectSnapshot> Objects { get; } = new();

    public List<NpcSnapshot> Npcs { get; } = new();

    public List<TraceEventSnapshot> TraceEvents { get; } = new();
}

public sealed class TileSnapshot
{
    public TileCoord Coord { get; set; } = TileCoord.Zero;

    public bool Walkable { get; set; }

    public bool Blocked { get; set; }

    public bool Indoor { get; set; }
}

public sealed class PointSnapshot
{
    public PointId Id { get; set; }

    public ConnectionGroupId? ConnectionGroupId { get; set; }

    public List<TileCoord> Tiles { get; } = new List<TileCoord>();

    public PointRole Role { get; set; } = PointRole.None;

    public Float2 LocalOffset { get; set; } = Float2.Zero;

    public Float2 WorldPosition { get; set; } = Float2.Zero;

    public PointKind Kind { get; set; } = PointKind.Interior;

    public bool Occupied { get; set; }

    public bool Reserved { get; set; }
}

public sealed class ObjectSnapshot
{
    public ObjectId Id { get; set; }

    public string DefinitionId { get; set; } = string.Empty;

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public List<PointId> Points { get; } = new();
}

public sealed class NpcSnapshot
{
    public EntityId Id { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public Float2 Position { get; set; } = Float2.Zero;

    public float RotationDegrees { get; set; }

    public float Hunger { get; set; }

    public float Energy { get; set; }

    public float Comfort { get; set; }

    public float ThermalDiscomfort { get; set; }

    public string CurrentGoal { get; set; } = string.Empty;

    public string PlanStatus { get; set; } = string.Empty;

    public string MovementStatus { get; set; } = string.Empty;

    public string ExecutionStatus { get; set; } = string.Empty;

    public string CurrentInteraction { get; set; } = string.Empty;

    public TileCoord? TargetTile { get; set; }

    public List<TileCoord> Path { get; } = new();

    public List<GoalScoreSnapshot> GoalScores { get; } = new();
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
