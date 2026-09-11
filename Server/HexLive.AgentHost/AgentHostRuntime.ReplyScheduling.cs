using System.Text.Json;

namespace HexLive.AgentHost;

public sealed partial class AgentHostRuntime
{
    // One writer for snapshots, durable commits, control handoffs and speech
    // uploads. Inference/TTS never hold this gate. No second simulation owner.
    private readonly SemaphoreSlim _turnWriter = new(1, 1);
    private readonly SemaphoreSlim _modelSlot = new(1, 1);
    private ScheduledTurn? _replyTurn, _autonomyTurn;
    private long _attachmentVersion, _controlVersion;

    private sealed class ScheduledTurn
    {
        public required bool IsReply;
        public required long AttachmentVersion, ControlVersion, InboxWatermark;
        public required string TurnId;
        public required CancellationTokenSource Stop;
        public readonly TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Worker = Task.CompletedTask;
        public volatile bool ReadyToCommit;
    }

    private void EnsureCurrentTurn(ScheduledTurn? turn, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (turn != null && (turn.AttachmentVersion != _attachmentVersion ||
                            !turn.IsReply && turn.ControlVersion != _controlVersion))
            throw new OperationCanceledException("Superseded agent turn", token);
    }

    private ScheduledTurn StartTurn(McpClient mcp, string attachmentId, int npcId,
        PresenceState presence, CancellationToken attachmentToken, string trigger,
        string text = "", string? id = null, string sender = "", string[]? messages = null,
        long watermark = 0)
    {
        var turn = new ScheduledTurn
        {
            IsReply = trigger == "voice", AttachmentVersion = _attachmentVersion,
            ControlVersion = _controlVersion, InboxWatermark = watermark,
            TurnId = id ?? Guid.NewGuid().ToString("N"),
            Stop = CancellationTokenSource.CreateLinkedTokenSource(attachmentToken),
        };
        if (turn.IsReply)
        {
            // #408: a new question preempts unfinished autonomous reasoning.
            // The accepted body's action has an attachment token and keeps running.
            _autonomyTurn?.Stop.Cancel();
            _replyTurn = turn;
        }
        else _autonomyTurn = turn;
        presence.BeginTurn(turn.Stop);
        turn.Worker = RunTurnAsync();
        return turn;

        async Task RunTurnAsync()
        {
            bool consumed = false;
            Exception? failure = null;
            try
            {
                consumed = await ProcessLaneAsync(mcp, attachmentId, npcId, trigger, text,
                    turn.Stop.Token, attachmentToken, turn.TurnId, sender,
                    turn.IsReply ? messages ?? [] : [], turn).ConfigureAwait(false);
            }
            catch (Exception ex) { failure = ex; }
            finally { presence.EndTurn(turn.Stop); }
            if (failure is OperationCanceledException && turn.Stop.IsCancellationRequested)
                turn.Completion.TrySetCanceled(turn.Stop.Token);
            else if (failure != null) turn.Completion.TrySetException(failure);
            else turn.Completion.TrySetResult(consumed);
        }
    }

    private async Task RunReplySchedulingAsync(McpClient mcp, string attachmentId, int npcId,
        JsonElement attached, PresenceState presence, Task heartbeatTask, Task historyTask, CancellationToken token)
    {
        ++_attachmentVersion;
        _controlVersion = 0;
        long inboxWatermark = 0;
        _incidents.Reset();
        long? eventWatermark = attached.TryGetProperty("eventWatermark", out var mark) &&
            mark.ValueKind == JsonValueKind.Number ? mark.GetInt64() : null;
        var nextAutonomy = DateTimeOffset.UtcNow.Add(ModelHeartbeat);
        var nextEvents = DateTimeOffset.UtcNow;
        var nextInbox = DateTimeOffset.UtcNow;
        var retryAutonomy = DateTimeOffset.MinValue;
        var retryReply = DateTimeOffset.MinValue;
        var failedReply = "";
        var critical = false;
        var planDecision = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                // #391: timeout-completed heartbeat can be Canceled, not Faulted.
                if (heartbeatTask.IsCompleted)
                {
                    await heartbeatTask.ConfigureAwait(false);
                    throw new McpRequestException("McpHeartbeatStopped");
                }
                if (historyTask.IsFaulted) await historyTask.ConfigureAwait(false);
                planDecision |= Interlocked.Exchange(ref _planNeedsDecision, 0) != 0;
                await _turnWriter.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await MaintainPlanLeaseAsync(mcp, npcId, presence.Value, token).ConfigureAwait(false);
                    if (_replyTurn is { } reply && reply.Completion.Task.IsCompleted)
                    {
                        _replyTurn = null;
                        try
                        {
                            if (await reply.Completion.Task.ConfigureAwait(false))
                            {
                                // Only the reply lane owns these message IDs and cursor.
                                if (reply.InboxWatermark > inboxWatermark &&
                                    await AcknowledgeInboxSafelyAsync(mcp, attachmentId, reply.InboxWatermark, token))
                                    inboxWatermark = reply.InboxWatermark;
                                failedReply = "";
                            }
                            else { failedReply = reply.TurnId; retryReply = DateTimeOffset.UtcNow.Add(RetryDelay); }
                        }
                        catch (OperationCanceledException) when (reply.Stop.IsCancellationRequested) { }
                        finally { reply.Stop.Dispose(); }
                    }
                    if (_autonomyTurn is { } autonomy && autonomy.Completion.Task.IsCompleted)
                    {
                        _autonomyTurn = null;
                        try
                        {
                            if (!await autonomy.Completion.Task.ConfigureAwait(false))
                                retryAutonomy = DateTimeOffset.UtcNow.Add(ModelHeartbeat);
                        }
                        catch (OperationCanceledException) when (autonomy.Stop.IsCancellationRequested) { }
                        finally { autonomy.Stop.Dispose(); }
                        nextAutonomy = DateTimeOffset.UtcNow.Add(ModelHeartbeat);
                    }
                    if (!presence.Value)
                    {
                        await StopActionAsync().ConfigureAwait(false);
                        nextAutonomy = DateTimeOffset.UtcNow.Add(ModelHeartbeat);
                    }
                    else
                    {
                        if (_outbox.Items.Count > 0)
                            await FlushOutboxAsync(mcp, attachmentId, npcId, token).ConfigureAwait(false);
                        var now = DateTimeOffset.UtcNow;
                        // Same two idle inbox requests/sec as before. No polling a
                        // second batch while its sole reply slot is occupied.
                        if (_replyTurn == null && now >= nextInbox)
                        {
                            nextInbox = now.AddMilliseconds(500);
                            var inbox = await mcp.CallToolAsync("read_agent_inbox",
                                new { attachmentId, sinceSeq = inboxWatermark, limit = 16 }, token);
                            ArchiveInbox(inbox);
                            inbox = FirstSpeakerInbox(await RemoveProcessedMessagesAsync(inbox, token));
                            var watermark = inbox.TryGetProperty("watermark", out var value) ? value.GetInt64() : inboxWatermark;
                            var text = MergePlayerMessages(inbox);
                            if (text.Length == 0 && watermark > inboxWatermark)
                            {
                                if (await AcknowledgeInboxSafelyAsync(mcp, attachmentId, watermark, token))
                                    inboxWatermark = watermark;
                            }
                            else if (text.Length > 0)
                            {
                                var id = VoiceTurnId(attachmentId, inbox);
                                // A failed previous question does not delay a new batch.
                                if (id != failedReply || now >= retryReply)
                                    StartTurn(mcp, attachmentId, npcId, presence, token, "voice",
                                        text, id, SenderOf(inbox), MessageIdsOf(inbox), watermark);
                            }
                        }
                        if (now >= nextEvents)
                        {
                            var events = await mcp.CallToolAsync("read_events",
                                new { sinceSeq = eventWatermark, limit = 50, npcId }, token);
                            if (events.TryGetProperty("watermark", out var value)) eventWatermark = value.GetInt64();
                            _incidents.Observe(events);
                            critical |= ContainsCriticalEvent(events);
                            if (critical && _executingPlan)
                            {
                                var runningPlan = (await _memory.SnapshotAsync(token).ConfigureAwait(false)).ExecutionPlan;
                                if (runningPlan is { Status: "active" })
                                    await _memory.PauseExecutionPlanAsync(runningPlan.Id, runningPlan.Revision, "CriticalEvent", token).ConfigureAwait(false);
                                await StopActionAsync().ConfigureAwait(false);
                            }
                            await TryStartExecutionPlanAsync(mcp, npcId, token).ConfigureAwait(false);
                            nextEvents = now.AddSeconds(2);
                        }
                        if (_executingPlan && now >= nextAutonomy)
                        {
                            _diagnostics.Record("plan.heartbeat");
                            nextAutonomy = now.Add(ModelHeartbeat);
                        }
                        if (_autonomyTurn == null && _replyTurn == null && now >= retryAutonomy)
                        {
                            var trigger = critical || planDecision || _incidents.HasPending ? "critical" :
                                !_executingPlan && !_reconcilingPlan && now >= nextAutonomy ? "heartbeat" : "";
                            if (trigger.Length > 0)
                            {
                                critical = false;
                                planDecision = false;
                                StartTurn(mcp, attachmentId, npcId, presence, token, trigger);
                            }
                        }
                    }
                }
                finally { _turnWriter.Release(); }
                await Task.Delay(presence.Value ? 50 : 750, token).ConfigureAwait(false);
            }
        }
        finally
        {
            ++_attachmentVersion;
            var reply = _replyTurn;
            var autonomy = _autonomyTurn;
            reply?.Stop.Cancel(); autonomy?.Stop.Cancel();
            if (reply != null) { await reply.Worker.ConfigureAwait(false); _ = reply.Completion.Task.Exception; reply.Stop.Dispose(); }
            if (autonomy != null) { await autonomy.Worker.ConfigureAwait(false); _ = autonomy.Completion.Task.Exception; autonomy.Stop.Dispose(); }
            _replyTurn = null; _autonomyTurn = null;
        }
    }
}
