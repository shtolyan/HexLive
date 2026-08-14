using System;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// Deterministic offline provider for tests and future integration work.
/// It never invents a target: danger signals produce Stop and all other input produces None.
/// </summary>
public sealed class MockLlmControlProvider : ILlmControlProvider
{
    private static readonly string[] DangerSignals =
    {
        "danger",
        "threat",
        "unsafe",
        "combat"
    };

    public LlmDecision Decide(LlmDecisionContext context)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        if (ContainsDangerSignal(context.StateSummary) ||
            ContainsDangerSignal(context.PerceptionSummary) ||
            ContainsDangerSignal(context.MemorySummary))
        {
            return new LlmDecision(
                LlmCommandKind.Stop,
                reason: "Mock safety rule stopped the NPC because context reported danger.");
        }

        return new LlmDecision(
            LlmCommandKind.None,
            reason: "Mock provider found no safe action in the context summaries.");
    }

    private static bool ContainsDangerSignal(string summary)
    {
        for (var i = 0; i < DangerSignals.Length; i++)
        {
            if (summary.IndexOf(DangerSignals[i], StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}

}
