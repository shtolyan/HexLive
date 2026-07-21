using HexLive.Simulation.AI;

namespace HexLive.Simulation.Runtime
{

// Spec §64: the dream layer's balance knobs. A dream is a colony-driven,
// ordered aspiration (campfire → own bed → …) that biases the EXISTING build
// machinery once basic needs are met — it does not add a new plan/execution
// path. All tuning lives here so the headless harness can bisect it and a flip
// of Enabled restores the pre-dream baseline exactly (the survival balance is
// knife-edge — see the dog-fragility notes). DreamSystem publishes the state;
// DecisionSystem reads BuildPull; BedSiteSystem reads the active dream + owner.
public static class SpecDream
{
    // Master kill-switch. false ⇒ DreamSystem parks ActiveDream = None, the
    // build pull is 0, and BedSiteSystem falls back to its old aggregate
    // count-based staking — i.e. the exact pre-§64 behaviour, for harness
    // bisection and balance rollback.
    public static bool Enabled = true;

    // The ordered aspiration queue, given to the whole colony. Extensible:
    // append future dreams (a wall, a store, a workshop…) here and to DreamType.
    public static DreamType[] DefaultQueue = { DreamType.Campfire, DreamType.OwnBed };

    // The owner's extra push toward HER OWN dream build, added to BuildFurniture
    // and its active feeders — but ONLY while free-hands is open (needs met), so
    // it can never enter a survival bid. Sized to WIN among peacetime chores:
    // when comfort is low (wants to sit/rest) or social is low (wants to talk),
    // the build-dream should still outrank that leisure. Survival (hunger/thirst
    // /danger) always preempts it via the peacetime gate. Was 0.18 (too timid —
    // the bed never got fed); 0.30 lets the dream beat sit/socialize/idle.
    public static float BuildPull = 0.30f;

    // Bed ownership strength. false ⇒ SOFT: each girl prefers her own bed but
    // may use any free bed until hers is built (survival unchanged). true ⇒
    // STRICT: only the owner ever sleeps in a bed (more "personal" but a
    // survival-balance change that also needs the IsValidTargetFor +
    // HasFurnitureCandidate ownership filters — not wired in this pass).
    public static bool BedExclusive = false;

    // Campfire-dream completion basis. true ⇒ latch when a LIT campfire first
    // exists (ResourceAmount > 0); false ⇒ latch as soon as a campfire OBJECT
    // exists (matches BedSiteSystem's hearth test, so bed staking is not delayed
    // by the raise→first-light gap). Flip to false if the lit gate regresses
    // comfort in a re-soak.
    public static bool CampfireRequiresLit = true;
}

}
