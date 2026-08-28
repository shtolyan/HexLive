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

    // §121.1: explicit pace of a player-issued movement order. It belongs
    // to the plan (and therefore survives a save with that unfinished order),
    // not to the NPC: the next ordinary click must return her to a walk.
    public bool RunRequested { get; set; }
}

public sealed class PlanStep
{
    public PlanStepType Type { get; set; }

    public JunctionId? TargetJunction { get; set; }

    public ObjectId? TargetObject { get; set; }

    public InteractionType? Interaction { get; set; }

    // §121 / #266: two catalog actions may share one broad interaction type
    // (split.log and saw.log are both Process). Manual orders preserve the
    // exact menu row instead of silently taking the first action of that type.
    public string InteractionId { get; set; } = string.Empty;

    public int? TimeoutEndTick { get; set; }

    // §40.6 r16: source ownership for one personal-laundry iteration.
    // False = worn (return to body), true = inventory (return to inventory).
    public bool LaundryFromInventory { get; set; }
}

public enum PlanStepType
{
    MoveToJunction,
    Interact,
    DropInventoryItem,
    ConsumeInventoryItem,
    UndressItem,
    GroundSit,   // sit in place on the land (spec 29G)
    GroundSleep, // lie at a free hex center (spec 29G)
    GroundCool,  // dwell on a shaded/water tile until cooled (spec 35.4)
    DrinkBottle, // drink in place from the carried bottle (spec 29H)
    CraftInPlace, // §gear-craft: recipe has no station — craft right here
    Wait,
    PrepareBathe,
    SwimBathe,
    WashClothes,
    RedressAfterBathe, // §40.6: return to the shore pile and re-don the clothes
    TreatSelf, // §68: wind a dressing round your own wounds, in place
    // §116 append-only rescue/medical actions.
    PickUpPerson,
    PutPersonInBed,
    ApplySplint,
    FitProsthetic,
    // §123 append-only player inventory mutations. TimeoutEndTick carries the
    // authoritative source index; TargetItemDefinitionId guards stale layouts.
    PlayerWearInventory,
    PlayerStowWorn,
    PlayerDropCarried,
    PlayerDropWorn,
    // §128 append-only two-way transfer with an unconscious person. Direction
    // and source list stay in the type; TimeoutEndTick packs count + index.
    PlayerTakeCarried,
    PlayerTakeWorn,
    PlayerGiveCarried,
    PlayerGiveWorn,
    // §133 append-only: подойти к хозяйке вещи и спросить разрешения надеть её.
    AskWearPermission,
    // §133 append-only: поднять валяющуюся одежду и отнести её к дому.
    StowCarriedGarment,
    // §137 append-only: сесть на землю там, где стоишь, и отдохнуть.
    IdleRest,
    // §128.5 append-only: обмен с ВЕЩЬЮ (истлевшее тело, снятый рюкзак).
    // TargetObject — сам контейнер; TimeoutEndTick пакует счёт и индекс ячейки,
    // ровно как в человеческой паре шагов выше.
    PlayerTakeFromContainer,
    PlayerGiveToContainer
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
