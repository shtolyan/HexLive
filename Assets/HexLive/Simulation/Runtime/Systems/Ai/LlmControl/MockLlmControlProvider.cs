using System;
using System.Threading;
using System.Threading.Tasks;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// Deterministic offline provider for tests and future integration work.
/// It never invents a target: danger signals produce Stop and all other input produces None.
/// </summary>
public sealed class MockLlmControlProvider : QueuedLlmControlProvider
{
    private static readonly string[] DangerSignals =
    {
        "danger",
        "threat",
        "unsafe",
        "combat"
    };

    protected override Task<LlmDecision> DecideAsync(
        LlmDecisionContext context, CancellationToken cancellationToken)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        cancellationToken.ThrowIfCancellationRequested();

        if (ContainsDangerSignal(context.StateSummary) ||
            ContainsDangerSignal(context.PerceptionSummary) ||
            ContainsDangerSignal(context.MemorySummary))
        {
            return Task.FromResult(new LlmDecision(
                LlmCommandKind.Stop,
                reason: "Mock safety rule stopped the NPC because context reported danger."));
        }

        return Task.FromResult(new LlmDecision(
            LlmCommandKind.None,
            reason: "Mock provider found no safe action in the context summaries."));
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
