namespace HexLive.Simulation.Runtime
{

// Cognition knobs that used to be private consts inside Perception/Decision:
// how far she sees, how long spatial memory lives, and the goal-lock
// hysteresis that keeps the auction from thrashing. Static (like SimBalance)
// so the CharacterBalance config asset can push tuned values at boot; the
// systems read them through `=> AiBalance.X` shims at the old const names.
public static class AiBalance
{
    public static int PerceptionRadiusTiles = 2;
    // How long a seen object / danger mark lingers in memory. 2400 ticks =
    // 10 real minutes; MeatRawSpoilTicks (2600) is deliberately tuned to
    // outlive it. Plain ticks, so it does NOT follow the visual clock.
    public static int MemoryTtlTicks = 2400;

    // Spec 23.16/35.4: a freshly won goal is locked this long; only a
    // clearly better bid (LockOverrideDelta) may break the lock, and after
    // it expires a challenger still needs SwitchDelta of margin.
    public static int GoalLockTicks = 24;
    public static float LockOverrideDelta = 0.5f;
    public static float SwitchDelta = 0.15f;

    // §28.15: how long the invited listener waits for the talk to start.
    public static int TalkWaitTimeoutTicks = 120;

    // spec §30.15 — when to say out loud "this one is not getting anywhere".
    //
    // These live HERE, in an already-registered balance class, on purpose: a NEW
    // static class needs a row in BalanceReflection.BalanceClasses, and without
    // it the knobs still mirror into the asset and still pass the coverage gate
    // while silently never being exported or applied (see the Spec81 scar
    // comment there). Cheaper to join a registered bag than to remember.

    /// <summary>Goal set, no interaction running, not moving — the §102
    /// signature. 40 ticks is ten seconds of world time: long enough that a
    /// normal hand-off between plan and path never trips it.</summary>
    public static int StuckIdleTicks = 40;

    /// <summary>One plan step held for this long. Generous: the longest honest
    /// interactions (building, sleeping) run for hundreds of ticks.</summary>
    public static int StuckStepTicks = 600;

    /// <summary>No goal at all while some need is past its crisis line — she is
    /// not idle by choice, the auction is failing to produce anything.</summary>
    public static int StuckGoallessTicks = 120;

    /// <summary>Says it is walking, but has not actually moved. The pursue-loop
    /// signature: a path that is rebuilt every tick and never advances.</summary>
    public static int StuckFrozenTicks = 60;

    /// <summary>While a stall persists, repeat the complaint no more often than
    /// this. Onset is always reported; the repeat is what makes a long freeze
    /// visible in a trace tail without drowning it.</summary>
    public static int StuckRepeatEmitTicks = 200;
}

}
