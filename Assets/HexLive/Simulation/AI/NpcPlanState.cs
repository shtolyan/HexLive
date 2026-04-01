using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.AI
{

public sealed class NPCPlanState
{
    public GoalType Goal { get; set; } = GoalType.None;

    public List<PlanStep> Steps { get; } = new();

    public int CurrentStepIndex { get; set; }

    public PlanStatus Status { get; set; } = PlanStatus.None;

    public ObjectId? TargetObjectId { get; set; }

    public PointId? TargetPointId { get; set; }

    public TileCoord? TargetTile { get; set; }
}

public sealed class PlanStep
{
    public PlanStepType Type { get; set; }

    public TileCoord? TargetTile { get; set; }

    public PointId? TargetPoint { get; set; }

    public ObjectId? TargetObject { get; set; }

    public int? TimeoutEndTick { get; set; }
}

public enum PlanStepType
{
    MoveToTile,
    MoveToPoint,
    Interact,
    Wait
}

public enum PlanStatus
{
    None,
    Active,
    Completed,
    Failed,
    Invalid
}

}
