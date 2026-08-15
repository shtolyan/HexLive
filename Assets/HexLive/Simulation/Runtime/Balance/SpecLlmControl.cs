namespace HexLive.Simulation.Runtime
{

// §32.15: safe-by-default gate for the LLM-control MVP adapter. This is an
// integration switch, not a live balance knob: shipped worlds must remain
// identical unless a host explicitly opts in and supplies selected NPC ids.
public static class SpecLlmControl
{
    public static bool Enabled = false;

    // The server's default world advances four base ticks per real second and
    // runs this system on every fourth (Medium) tick. Provider results can only
    // be drained on those Medium passes.
    public const int SimulationTicksPerSecond = 4;
    public const int ResultDrainIntervalTicks = 4;

    // Even an idle selected NPC is not reconsidered every simulation tick.
    // At the default 4 Hz this is sixteen seconds between provider decisions.
    public const int DecisionCooldownTicks = 64;

    // Provider work older than this is canceled and any late result is dropped
    // by its issued tick. At 4 Hz this is a sixteen-second response budget.
    public const int RequestTimeoutTicks = 64;

    // Age 64 is already stale, so age 60 is the final production Medium pass
    // that can apply a result: (64 - 4) / 4 = 15 real seconds at normal 1x.
    // Host timeout/backoff validation derives its wall-clock ceiling from here.
    public const int LastApplicableResultAgeTicks =
        RequestTimeoutTicks - ResultDrainIntervalTicks;
    public const int MaxProviderDelaySeconds =
        LastApplicableResultAgeTicks / SimulationTicksPerSecond;

    // Bound external work independently of colony size. Round-robin selection
    // ensures a full cap cannot permanently favor low entity ids.
    public const int MaxInFlightRequests = 2;

    // Provider-side bounds are independent of the system cap. They protect a
    // provider reused by another host caller and include completed results that
    // have not yet been drained, so its owned queues cannot grow without bound.
    public const int MaxProviderQueuedRequests = 2;
    public const int MaxProviderConcurrentRequests = 2;
}

}
