using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Navigation
{

public sealed class MovementState
{
    public bool IsMoving { get; set; }

    public TileCoord? CurrentTargetTile { get; set; }

    public PointId? CurrentTargetPoint { get; set; }

    public List<TileCoord> TilePath { get; } = new();

    public int PathIndex { get; set; }

    public Float2 DesiredDirection { get; set; } = Float2.Zero;

    public float DesiredRotationDegrees { get; set; }

    public float MoveSpeed { get; set; } = 1f;

    public float TurnSpeed { get; set; } = 180f;

    public MovementStatus Status { get; set; } = MovementStatus.Idle;

    public string StopReason { get; set; } = string.Empty;

    public TileCoord? FinalTile { get; set; }

    public Float2? FinalWorldTarget { get; set; }
}

public enum MovementStatus
{
    Idle,
    Rotating,
    Moving,
    Arrived,
    Blocked,
    Waiting,
    Invalid
}

}
