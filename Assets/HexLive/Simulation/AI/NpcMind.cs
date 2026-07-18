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

    // Spec 35.4: overheating latch — enter at CoolOffEnterThreshold, clear at
    // CoolOffClearThreshold. Gates CoolOff availability so it doesn't flicker
    // around the entry edge and re-win at zero margin every tick.
    public bool IsOverheated { get; set; }

    // Spec 35.4: how many times the in-place cool-off dwell has re-armed without
    // the goal actually clearing (safety cap against an infinite dwell on a
    // fallback tile that never cools). Reset when CoolOff is (re)selected.
    public int CoolRearmCount { get; set; }

    // Spec 28.15C: mourning period and which bodies were already grieved for.
    public int GrievingUntilTick { get; set; }

    // Spec 40.13: knocked out — utterly spent stamina plus starvation or
    // blood loss drops the body; it lies unable to act until this tick, then
    // rises. 0 = conscious.
    public int FaintedUntilTick { get; set; }

    // Spec §60: coma — the deep unconsciousness. Unlike the timed faint above,
    // a coma has no deadline: the body lies as if dead, recovering exactly as
    // in sleep, until the STAT that felled it climbs back over
    // SimBalance.ComaWakeThreshold. Entered when Energy or Blood hits 0.
    public ComaCause ComaCause { get; set; }

    // Spec 41.5: just woke up — stand and come to your senses until this
    // tick (no goal scoring), so nobody sprints off the pillow and the
    // get-up animation has room to play.
    public int WakeGraceUntilTick { get; set; }

    public System.Collections.Generic.List<HexLive.Simulation.Common.ObjectId> GrievedCorpses { get; } = new();

    // Spec 28.8 v1 handshake: someone is walking over to talk to this NPC.
    // While set, this NPC accepts by waiting in place (no own plans) until
    // the initiator arrives, a timeout passes, or an emergency overrides.
    // Self-healed each decision pass.
    public HexLive.Simulation.Common.EntityId? PendingTalkFrom { get; set; }

    public int PendingTalkSinceTick { get; set; }

    // Spec §53: someone is walking over to HELP this NPC (feed/treat/medicate/
    // console). Mirrors PendingTalkFrom — while set, the sufferer holds still so
    // the helper can reach her, until arrival, timeout, or an emergency. This is
    // separate from PendingTalkFrom so a chat and an aid claim don't clobber each
    // other. Self-healed each decision pass.
    public HexLive.Simulation.Common.EntityId? PendingAidFrom { get; set; }

    public int PendingAidSinceTick { get; set; }

    // Reactive combat aid: when a fleeing victim calls for help, responders
    // get a short-lived Defend goal pointed at the attacker.
    public int LastHelpCryTick { get; set; } = -999999;

    public int? CombatAssistDogId { get; set; }

    public HexLive.Simulation.Common.EntityId? CombatAssistAttackerNpcId { get; set; }

    // Spec §49 (water sickness v2): raw water no longer bites in one lump.
    // A positive roll opens a visible window (SickUntilTick, drives the 🤢 icon
    // + comfort malaise) and adds to a bounded damage budget
    // (SicknessDamageRemaining) the torso pays down a little each slow tick.
    // The budget cap keeps total harm ≈ the old instant model even when a
    // thirsty colony drinks raw back-to-back (overlapping windows). 0 = well.
    public int SickUntilTick { get; set; }

    public float SicknessDamageRemaining { get; set; }

    public GoalLock? GoalLock { get; set; }

    public List<GoalCooldown> Cooldowns { get; } = new();

    public List<GoalScore> LastScores { get; } = new();

    public DecisionResult LastDecision { get; set; } = new();
}

// Spec §60: what dropped the body into a coma — and therefore which stat must
// recover past the wake threshold before it comes to. (Append-only: saves
// store ints.)
public enum ComaCause
{
    None,
    Exhaustion, // Energy drained to 0 — sleeps it off where she fell
    BloodLoss   // Blood drained to 0 — out cold until the blood knits back
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
    GatherWood,   // pick up wood (log or stick) off the ground (iteration 13 / §54)
    SplitLog,     // §54: chop a ground log into sticks (needs an axe, in the field)
    ChopCrown,    // §54.2: chop a felled palm crown into loose leaves (needs an axe)
    GatherLeaves, // §54.2: pick scattered palm leaves off the ground
    TendFire,     // fuel/light the campfire with a carried stick (iteration 13 / §54)
    Hunt,         // chase a rabbit with a spear (iteration 14)
    Prey,         // §56: kill a housemate for meat — starvation last resort
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
    WarmUp,       // huddle by the burning campfire when freezing (spec 42)
    GatherHerb,   // pick healing leaves (spec 44)
    CraftBandage, // 2 herb leaves -> bandage at the campfire (spec 44)
    HarvestYucca, // §54: cut a yucca with a blade (knife/axe) — fiber scatters
    GatherFiber,  // §54: pick plant fiber off the ground
    CraftRope,    // §54: 3 fiber -> rope (lashing) at the campfire
    CraftCloth,   // §54: 4 fiber -> cloth at the campfire
    CraftKnife,   // §54: 1 stick + 1 stone -> knife at the campfire
    Butcher,      // §54: knife a carcass/corpse into meat + hide (needs a knife)
    CraftRack,    // drying rack: 2 logs at the campfire (iteration 21)
    DryClothes,   // hang the wettest garment / stand by the fire (iter 21)
    CraftBed,     // bedroll: 2 logs + 3 palm leaves (iteration 28)
    CraftTent,    // sun shelter: 4 palm leaves at the campfire (spec 40.14)
    BuildRaft,    // haul logs to the escape raft — the way off the island (40.15)
    CraftBow,     // bow: 2 logs + 1 hide at the campfire (iteration 22)
    CraftArrows,  // 1 log -> 3 arrows at the campfire (iteration 22)
    Aid,          // tend a suffering housemate — feed/treat/medicate/console (spec 53)
    PlaceSite,     // §52: stake out a furniture build-site (intent point)
    DeliverToSite, // §52: haul a needed material to a build-site and deposit it
    BuildFurniture,// §52: raise a fully-stocked build-site with a hammer
    HaulToFire,    // §52: carry a low-value item to the fireside stockpile to free a slot
    Idle,
    Defend,      // answer a combat help cry and attack the aggressor
    Bathe,       // undress at shore, then swim long enough to wash the body
    WashClothes  // wash one dirty ground garment at the shore
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
