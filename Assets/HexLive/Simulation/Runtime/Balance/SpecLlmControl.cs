namespace HexLive.Simulation.Runtime
{

// §32.15: safe-by-default gate for the LLM-control MVP adapter. This is an
// integration switch, not a live balance knob: shipped worlds must remain
// identical unless a host explicitly opts in and supplies selected NPC ids.
public static class SpecLlmControl
{
    public static bool Enabled = false;

    // Even an idle selected NPC is not reconsidered every simulation tick.
    // At the default 4 Hz this is sixteen seconds between provider decisions.
    public const int DecisionCooldownTicks = 64;
}

}
