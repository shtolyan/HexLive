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

    // ── Длительности, которые были продублированы числом ─────────────────
    //
    // Каждая из них стояла ГОЛЫМ ЛИТЕРАЛОМ в двух-трёх местах сразу. Такое
    // число нельзя «подкрутить»: правка одного места из трёх не ломает сборку
    // и не роняет тест — она просто заводит два разных правила там, где было
    // одно, и расхождение всплывает месяцами позже как необъяснимое поведение.
    // Ровно так уже разъезжались фильтры кокоса и мерки дистанции.

    /// <summary>
    /// Насколько NPC «отворачивается» от источника, который по прибытии
    /// оказался занят: чтобы не топтаться вокруг него, а поискать другой.
    /// </summary>
    public static int ShunTicks = 600;

    /// <summary>Посидев, не садится снова столько тиков (§35.4 анти-дребезг).</summary>
    public static int SitCooldownTicks = 240;

    /// <summary>Одевшись, не переодевается столько тиков.</summary>
    public static int DressCooldownTicks = 160;

    /// <summary>
    /// Spec 41.5: только что проснулась — стоит и приходит в себя, цели ждут.
    /// </summary>
    public static int WakeGraceTicks = 12;

    /// <summary>
    /// Цель, чей план не удалось построить, отдыхает столько тиков. Тот же
    /// срок применяется к завершённому уходу за собой (мытьё, стирка), поэтому
    /// он и стоит здесь, а не приватной константой планировщика.
    /// </summary>
    public static int FailureCooldownTicks = 40;
}

}
