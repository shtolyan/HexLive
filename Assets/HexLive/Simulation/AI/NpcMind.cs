using System.Collections.Generic;

namespace HexLive.Simulation.AI
{

public sealed class NPCMind
{
    public GoalType CurrentGoal { get; set; } = GoalType.None;

    // Emergency appraisal (spec 23.17): hysteresis 0.85 enter / 0.60 clear.
    public bool IsStarving { get; set; }

    // Spec 29E.1: same hysteresis pattern for water.
    public bool IsDehydrated { get; set; }

    // Spec 28.15C: mourning period and which bodies were already grieved for.
    public int GrievingUntilTick { get; set; }

    // Spec 40.13: knocked out — utterly spent stamina plus starvation or
    // blood loss drops the body; it lies unable to act until this tick, then
    // rises. 0 = conscious.
    public int FaintedUntilTick { get; set; }

    public System.Collections.Generic.List<HexLive.Simulation.Common.ObjectId> GrievedCorpses { get; } = new();

    // Spec 28.8 v1 handshake: someone is walking over to talk to this NPC.
    // While set, this NPC accepts by waiting in place (no own plans) until
    // the initiator arrives, a timeout passes, or an emergency overrides.
    // Self-healed each decision pass.
    public HexLive.Simulation.Common.EntityId? PendingTalkFrom { get; set; }

    public int PendingTalkSinceTick { get; set; }

    public GoalLock? GoalLock { get; set; }

    public List<GoalCooldown> Cooldowns { get; } = new();

    public List<GoalScore> LastScores { get; } = new();

    public DecisionResult LastDecision { get; set; } = new();
}

public enum GoalType
{
    None,
    Eat,       // consume food from inventory
    GetFood,   // acquire food from the world (PickUp)
    Sleep,
    Sit,
    Dress,
    Socialize,   // talk to another NPC (iteration 4)
    Explore,     // wander to a random far junction (iteration 9)
    Flee,        // run to the nearest indoor junction (iteration 10)
    Undress,     // take off the warmest safe-to-remove item (iteration 11)
    Drink,        // drink from the carried bottle in place (iter 13 / 29H)
    GetWater,     // fill the bottle at a water source (iteration 29)
    GatherTools,  // pick up a missing lighter/pot (iteration 13)
    GatherWood,   // pick up a log for the fire (iteration 13)
    TendFire,     // fuel/light the campfire with a carried log (iteration 13)
    Hunt,         // chase a rabbit with a spear (iteration 14)
    CraftSpear,   // whittle a spear from a log at the campfire (iteration 14)
    CookMeat,     // cook raw meat on the lit fire (iteration 14)
    CraftLeather, // sew pants from a hide, worn immediately (iteration 14)
    Mourn,        // visit a body (rite) or a grave (remembrance) (iter 15/16)
    Bury,         // lay a housemate's body to rest (iteration 16)
    GatherStone,  // pick up stones for tool recipes (iteration 18)
    CraftAxe,     // stone axe: 1 log + 1 stone (iteration 18)
    CraftPickaxe, // stone pickaxe: 1 log + 2 stones (iteration 18)
    HarvestTree,  // fell a big tree or palm with axe/saw (iteration 18)
    MineBoulder,  // break a boulder with the pickaxe (iteration 18)
    Build,        // add a piece to the communal hut (iteration 19)
    CoolOff,      // stand in shade or the river when overheating (iter 20)
    CraftRack,    // drying rack: 2 logs at the campfire (iteration 21)
    DryClothes,   // hang the wettest garment / stand by the fire (iter 21)
    CraftBed,     // bedroll: 2 logs + 3 palm leaves (iteration 28)
    CraftTent,    // sun shelter: 4 palm leaves at the campfire (spec 40.14)
    BuildRaft,    // haul logs to the escape raft — the way off the island (40.15)
    CraftBow,     // bow: 2 logs + 1 hide at the campfire (iteration 22)
    CraftArrows,  // 1 log -> 3 arrows at the campfire (iteration 22)
    Idle
}

public sealed class GoalScore
{
    public GoalType Goal { get; set; }

    public float BaseScore { get; set; }

    public float NeedModifier { get; set; }

    public float MemoryModifier { get; set; }

    public float SocialModifier { get; set; }

    public float EnvironmentModifier { get; set; }

    public float CommandModifier { get; set; }

    public float EmergencyModifier { get; set; }

    public float FinalScore { get; set; }
}

public sealed class GoalLock
{
    public GoalType Goal { get; set; }

    public int StartTick { get; set; }

    public int EndTick { get; set; }
}

public sealed class GoalCooldown
{
    public GoalType Goal { get; set; }

    public int EndTick { get; set; }
}

public sealed class DecisionResult
{
    public GoalType SelectedGoal { get; set; }

    public List<GoalScore> Scores { get; } = new();

    public string Reason { get; set; } = string.Empty;
}

}
