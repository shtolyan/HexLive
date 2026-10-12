namespace HexLive.AgentCore.Studio;

/// <summary>§171.16: dollar-for-dollar projection, latest event revision only.</summary>
public sealed record UsageBalance(decimal OpeningUsd, decimal BalanceUsd, decimal SpentUsd,
    decimal EstimatedUsd, int PendingRequests, long InputTokens, long OutputTokens, double AudioSeconds)
{
    public static UsageBalance Calculate(decimal openingUsd, string owner, IEnumerable<UsageEvent> events)
    {
        decimal spent = 0, estimated = 0;
        int pending = 0;
        long input = 0, output = 0;
        double seconds = 0;
        foreach (var e in events.Where(e => e.OwnerUserId == owner && e.EventId.Length > 0)
                     .GroupBy(e => e.EventId).Select(g => g.OrderByDescending(e => e.Revision).First()))
        {
            input += e.InputTokens ?? 0; output += e.OutputTokens ?? 0; seconds += e.AudioSeconds ?? 0;
            if (e.Currency != "USD" || e.Cost is not { } cost || cost < 0 ||
                e.CostSource is not (UsageCostSource.Estimate or UsageCostSource.Reconciled)) { pending++; continue; }
            spent += cost;
            if (e.CostSource == UsageCostSource.Estimate) estimated += cost;
        }
        return new(openingUsd, openingUsd - spent, spent, estimated, pending, input, output, seconds);
    }
}
