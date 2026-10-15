using HexLive.AgentCore.Studio;
using Microsoft.Extensions.Logging;

namespace HexLive.VoiceBrain;

public sealed partial class VoiceBrainService
{
    private readonly SemaphoreSlim _reconciliationGate = new(1, 1);
    private int _reconciliationCursor;

    public async Task<int> ReconcilePendingSessionsAsync(CancellationToken token)
    {
        if (_ledger == null || Options.AgentId.Length == 0) return 0;
        await _reconciliationGate.WaitAsync(token);
        try
        {
            var pending = JsonlUsageLedger.ReadAll(Options.UsageDirectory).Where(e => e.Kind == UsageKind.Voice &&
                e.Provider == "ElevenLabs" && e.SessionId.Length > 0 && e.EventId == "voice-" + e.SessionId &&
                e.OwnerUserId.StartsWith("ws:", StringComparison.Ordinal) && e.WorldId.Length > 0 && e.NpcId > 0 &&
                (e.CostSource != UsageCostSource.Reconciled || !_memory.For(new SessionTicket(e.SessionId,
                    e.OwnerUserId, e.CharacterId, e.WorldId, e.NpcId, 0)).HasCompletedImport(e.SessionId)))
                .OrderBy(e => e.AtUtc).ThenBy(e => e.EventId, StringComparer.Ordinal).ToArray();
            if (pending.Length == 0) { _reconciliationCursor = 0; return 0; }
            var start = _reconciliationCursor % pending.Length;
            var count = Math.Min(4, pending.Length);
            _reconciliationCursor = (start + count) % pending.Length;
            var settled = 0;
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                var e = pending[(start + i) % pending.Length];
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(45));
                    var conversation = e.ProviderRequestId.Length > 0 ? e.ProviderRequestId :
                        await ElevenLabs.ConversationForSessionAsync(Options.AgentId, e.SessionId, timeout.Token);
                    if (conversation == null) continue;
                    // This comes only from the private durable issuance ledger. The provider's
                    // user_id is still checked by RecordSessionEndAsync before any charge.
                    var ticket = new SessionTicket(e.SessionId, e.OwnerUserId, e.CharacterId, e.WorldId, e.NpcId, 0);
                    await RecordSessionEndAsync(conversation, ticket, timeout.Token);
                    if (JsonlUsageLedger.ReadAll(Options.UsageDirectory).Any(r => r.EventId == e.EventId &&
                        r.CostSource == UsageCostSource.Reconciled)) settled++;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or OperationCanceledException or
                    System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
                { _log.LogWarning("voice billing reconciliation deferred: {Type}", ex.GetType().Name); }
            }
            return settled;
        }
        finally { _reconciliationGate.Release(); }
    }

    public async Task ReconcilePendingContinuouslyAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), token);
                try { await ReconcilePendingSessionsAsync(token); }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
                { _log.LogWarning("voice billing reconciliation deferred: {Type}", ex.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
