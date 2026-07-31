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
}

}
