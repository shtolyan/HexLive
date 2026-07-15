using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.AI
{

public sealed class NPCPlanState
{
    public GoalType Goal { get; set; } = GoalType.None;

    public List<PlanStep> Steps { get; } = new();

    public int CurrentStepIndex { get; set; }

    public PlanStatus Status { get; set; } = PlanStatus.None;

    public ObjectId? TargetObjectId { get; set; }

    public JunctionId? TargetJunctionId { get; set; }

    public TileCoord? TargetTile { get; set; }

    // Inventory item consumed by a ConsumeInventoryItem plan (definition id).
    public string? TargetItemDefinitionId { get; set; }

    // Agent targeted by a Talk plan (spec 28.15A).
    public EntityId? TargetAgentId { get; set; }
}

public sealed class PlanStep
{
    public PlanStepType Type { get; set; }

    public JunctionId? TargetJunction { get; set; }

    public ObjectId? TargetObject { get; set; }

    public InteractionType? Interaction { get; set; }

    public int? TimeoutEndTick { get; set; }
}

public enum PlanStepType
{
    MoveToJunction,
    Interact,
    ConsumeInventoryItem,
    UndressItem,
    GroundSit,   // sit in place on the land (spec 29G)
    GroundSleep, // lie at a free hex center (spec 29G)
    GroundCool,  // dwell on a shaded/water tile until cooled (spec 35.4)
    DrinkBottle, // drink in place from the carried bottle (spec 29H)
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
