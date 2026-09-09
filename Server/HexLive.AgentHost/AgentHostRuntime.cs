using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace HexLive.AgentHost;

/// <summary>
/// Portable agent process. It is only an MCP client: there is no local HTTP,
/// WebSocket, audio or memory endpoint for Unity to discover.
/// </summary>
public sealed partial class AgentHostRuntime
{
    public string? TerminalErrorCode { get; private set; }
    private static readonly string[] Capabilities =
        ["playerText", "speech", "worldActions", "relationView", "journal"];
    private static readonly string[] CriticalFragments =
        ["Wound", "Hit", "Attack", "Fainted", "Dying", "Death", "Threat"];
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AttachmentHeartbeat = TimeSpan.FromSeconds(8);
    private TimeSpan ModelHeartbeat => TimeSpan.FromSeconds(_options.HeartbeatSeconds);
    private string? _pinnedWorldId;
    private int? _pinnedNpcId;

    private readonly AgentHostOptions _options;
    private readonly MashaMemoryStore _memory;
    private readonly IAgentProviders _providers;
    private readonly HttpMessageHandler? _mcpHandler;
    private readonly AgentTurnOutbox _outbox;
    private readonly AgentHostStatusStore _status;
    public string CurrentPhase => _status.Phase;
    public string LastIntentSummary { get; private set; } = string.Empty;
    private readonly Dictionary<string, Queue<string>> _conversations = new(StringComparer.Ordinal);
    private string _activeSpeakerKey = "";
    private Queue<string> Recent
    {
        get
        {
            if (!_conversations.TryGetValue(_activeSpeakerKey, out var queue))
                _conversations[_activeSpeakerKey] = queue = new();
            return queue;
        }
    }
    private string _lastSpeech = string.Empty;
    private CancellationTokenSource? _actionStop;
    private Task _actionTask = Task.CompletedTask;
    private volatile bool _handoffActionLease;
    private volatile bool _actionAwaitingContinuation;
    private string _actionContract = string.Empty;
    private AgentActionContract? _actionArguments;
    private string _actionFeedback = AgentPromptFiles.Text("AgentHostRuntime.01");

    public AgentHostRuntime(AgentHostOptions options, IAgentProviders? providers = null,
        HttpMessageHandler? mcpHandler = null)
    {
        _options = options;
        if (options.HeartbeatSeconds is < 5 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(options.HeartbeatSeconds));
        _memory = new MashaMemoryStore(options.MemoryDirectory, options.InitialIdentity);
        _providers = providers ?? new AgentProviders(options.ProviderOptions);
        _mcpHandler = mcpHandler;
        _outbox = new AgentTurnOutbox(options.OutboxPath);
        _status = new AgentHostStatusStore(options.StatusPath);
    }

    public static async Task DoctorAsync(AgentHostOptions options, CancellationToken cancellationToken)
    {
        using var mcp = new McpClient(options.ProviderOptions);
        var world = await mcp.CallToolAsync("world_status", new { }, cancellationToken);
        var colonists = await mcp.CallToolAsync("list_colonists", new { }, cancellationToken);
        _ = SelectNpc(colonists, options.ProfileId);
        if (!world.TryGetProperty("tick", out _))
            throw new InvalidDataException("MCP world_status response has no tick.");
        var tools = await mcp.ReadToolNamesAsync(cancellationToken);
        if (!new[] { "attach_agent", "agent_heartbeat", "read_agent_inbox", "commit_agent_turn",
                "begin_agent_utterance", "append_agent_utterance", "commit_agent_utterance", "detach_agent" }
            .All(tools.Contains))
        {
            Console.Error.WriteLine("MCP server needs the generic-agent/wire-v13 release before masha on.");
            throw new InvalidOperationException("Agent tools are unavailable.");
        }
        using var providers = new AgentProviders(options.ProviderOptions);
        await providers.DoctorAsync(cancellationToken);
        Console.WriteLine("AgentHost doctor: MCP, profile selector and local provider configuration are ready.");
        Console.WriteLine("No LLM or TTS request was made.");
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        TerminalErrorCode = null;
        _status.Write(true, "Starting", 0, false, false);
        var terminalError = false;
        try
        {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunAttachedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ex is AgentTargetChangedException || ex is HttpRequestException { StatusCode:
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden })
                {
                    TerminalErrorCode = ex.GetType().Name;
                    _status.Write(false, "Error", _pinnedNpcId ?? 0, false, false, ex.GetType().Name);
                    terminalError = true;
                    return;
                }
                _status.Write(true, "Reconnecting", 0, false, false, ex.GetType().Name);
                Console.Error.WriteLine($"[agent] reconnecting after {ex.GetType().Name}");
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            await StopActionAsync().ConfigureAwait(false);
            if (!terminalError) _status.Write(false, "Stopped", _pinnedNpcId ?? 0, false, false);
            _providers.Dispose();
        }
    }

    private async Task RunAttachedAsync(CancellationToken cancellationToken)
    {
        using var mcp = new McpClient(_options.ProviderOptions, _mcpHandler);
        var worldStatus = await mcp.CallToolAsync("world_status", new { }, cancellationToken);
        PinWorld(worldStatus);
        var colonists = await mcp.CallToolAsync("list_colonists", new { }, cancellationToken);
        var npcId = _pinnedNpcId ?? _options.NpcId ?? SelectNpc(colonists, _options.ProfileId);
        if (!colonists.GetProperty("colonists").EnumerateArray().Any(n =>
                n.GetProperty("npcId").GetInt32() == npcId &&
                (!n.TryGetProperty("health", out var health) || health.GetSingle() > 0)))
            throw new AgentTargetChangedException();
        _pinnedNpcId = npcId;
        var catalog = await mcp.ReadToolCatalogAsync(cancellationToken);
        _actionContract = BuildActionContract(catalog);
        _actionArguments = new AgentActionContract(catalog);
        _actionFeedback = AgentPromptFiles.Text("AgentHostRuntime.02");
        var attached = await mcp.CallToolAsync("attach_agent", new
        {
            npcId,
            displayName = _options.DisplayName,
            capabilities = Capabilities,
            ttlSeconds = 45,
        }, cancellationToken);
        var attachmentId = RequiredString(attached, "attachmentId");
        // §163: this gate means world execution permission, NOT player presence.
        var presence = new PresenceState(!IsWorldPaused(worldStatus));
        var playerPresent = attached.TryGetProperty("playerPresent", out var present) && present.GetBoolean();
        await ObserveSpeakersAsync(attached, worldStatus, npcId, cancellationToken);
        _status.Write(true, presence.Value ? "Ready" : "Sleeping", npcId, true, playerPresent);
        Console.WriteLine($"[agent] attached profile={_options.ProfileId} npc={npcId}");

        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = HeartbeatAsync(mcp, attachmentId, npcId, presence, heartbeatStop.Token);
        try
        {
            await FlushOutboxAsync(mcp, attachmentId, npcId, cancellationToken).ConfigureAwait(false);
            await RepublishLocalViewAsync(mcp, attachmentId, npcId, cancellationToken)
                .ConfigureAwait(false);
            await PublishPhaseAsync(mcp, attachmentId, "presence",
                presence.Value ? "Ready" : "Sleeping", cancellationToken);

            long inboxWatermark = 0;
            long? eventWatermark = null;
            var nextModelHeartbeat = DateTimeOffset.UtcNow.Add(ModelHeartbeat);
            var nextEventRead = DateTimeOffset.UtcNow;
            var retryAfter = DateTimeOffset.MinValue;

            while (!cancellationToken.IsCancellationRequested)
            {
                if (heartbeatTask.IsFaulted)
                    await heartbeatTask.ConfigureAwait(false);

                if (!presence.Value)
                {
                    await StopActionAsync().ConfigureAwait(false);
                    nextModelHeartbeat = DateTimeOffset.UtcNow.Add(ModelHeartbeat);
                    await Task.Delay(750, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (DateTimeOffset.UtcNow < retryAfter)
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                // Finish durable commits before considering another model request.
                if (_outbox.Items.Count > 0)
                    await FlushOutboxAsync(mcp, attachmentId, npcId, cancellationToken);

                var inbox = await mcp.CallToolAsync("read_agent_inbox", new
                {
                    attachmentId,
                    sinceSeq = inboxWatermark,
                    limit = 16,
                }, cancellationToken).ConfigureAwait(false);
                inbox = FirstSpeakerInbox(await RemoveProcessedMessagesAsync(inbox, cancellationToken));
                var readWatermark = inbox.TryGetProperty("watermark", out var watermark)
                    ? watermark.GetInt64() : inboxWatermark;
                var playerText = MergePlayerMessages(inbox);
                if (playerText.Length == 0) inboxWatermark = readWatermark;

                var critical = false;
                if (DateTimeOffset.UtcNow >= nextEventRead)
                {
                    var events = await mcp.CallToolAsync("read_events", new
                    {
                        sinceSeq = eventWatermark,
                        limit = 50,
                        npcId,
                    }, cancellationToken).ConfigureAwait(false);
                    if (events.TryGetProperty("watermark", out var eventMark))
                        eventWatermark = eventMark.GetInt64();
                    critical = ContainsCriticalEvent(events);
                    nextEventRead = DateTimeOffset.UtcNow.AddSeconds(2);
                }

                var trigger = playerText.Length > 0 ? "voice" :
                    critical ? "critical" :
                    DateTimeOffset.UtcNow >= nextModelHeartbeat ? "heartbeat" : string.Empty;
                if (trigger.Length > 0)
                {
                    using var turnStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    presence.BeginTurn(turnStop);
                    try
                    {
                        var consumed = await ProcessSafelyAsync(mcp, attachmentId, npcId, trigger, playerText,
                            turnStop.Token, cancellationToken,
                            playerText.Length > 0 ? VoiceTurnId(attachmentId, inbox) : null,
                            SenderOf(inbox), MessageIdsOf(inbox)).ConfigureAwait(false);
                        if (consumed) inboxWatermark = readWatermark;
                        else retryAfter = DateTimeOffset.UtcNow.Add(ModelHeartbeat);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !presence.Value)
                    { /* world paused: do not start another paid operation */ }
                    finally { presence.EndTurn(); }
                    nextModelHeartbeat = DateTimeOffset.UtcNow.Add(ModelHeartbeat);
                }
                else
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await StopActionAsync().ConfigureAwait(false);
            heartbeatStop.Cancel();
            try { await heartbeatTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { }
            try
            {
                await mcp.CallToolAsync("detach_agent", new { attachmentId }, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch { /* attachment TTL is the hard fallback */ }
            _status.Write(!cancellationToken.IsCancellationRequested, "Detached", npcId,
                false, false);
        }
    }

    private async Task HeartbeatAsync(McpClient mcp, string attachmentId, int npcId,
        PresenceState presence, CancellationToken cancellationToken)
    {
        var lastPublishedPresence = presence.Value;
        var nextAttachmentHeartbeat = DateTimeOffset.UtcNow;
        bool? lastPlayerPresence = null;
        try
        {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            var world = await mcp.CallToolAsync("world_status", new { }, cancellationToken);
            try { PinWorld(world); }
            catch { presence.Value = false; throw; }
            var current = !IsWorldPaused(world);
            presence.Value = current;
            if (DateTimeOffset.UtcNow >= nextAttachmentHeartbeat)
            {
                var result = await mcp.CallToolAsync("agent_heartbeat", new { attachmentId }, cancellationToken);
                var playerPresent = result.TryGetProperty("playerPresent", out var value) && value.GetBoolean();
                await ObserveSpeakersAsync(result, world, npcId, cancellationToken);
                lastPlayerPresence = playerPresent;
                nextAttachmentHeartbeat = DateTimeOffset.UtcNow.Add(AttachmentHeartbeat);
            }
            var changed = current != lastPublishedPresence;
            if (changed)
            {
                await mcp.CallToolAsync("publish_agent_phase", new
                {
                    attachmentId,
                    turnId = "presence",
                    phase = current ? "Ready" : "Sleeping",
                }, cancellationToken).ConfigureAwait(false);
                lastPublishedPresence = current;
            }
            if (!current || changed)
                _status.Write(true, current ? "Ready" : "Sleeping", npcId, true, lastPlayerPresence ?? false);
        }
        }
        finally { presence.Value = false; }
    }

    private static bool IsWorldPaused(JsonElement world) =>
        !world.TryGetProperty("paused", out var paused) || paused.ValueKind != JsonValueKind.False;

    private void PinWorld(JsonElement world)
    {
        if (!world.TryGetProperty("worldId", out var value) || value.ValueKind != JsonValueKind.String)
            throw new AgentTargetChangedException();
        var id = value.GetString() ?? "";
        if (_options.ExpectedWorldId != null && id != _options.ExpectedWorldId)
            throw new AgentTargetChangedException();
        // Standalone test hosts may use an empty ID. Production must supply its persistent world ID.
        if (_pinnedWorldId != null && _pinnedWorldId != id) throw new AgentTargetChangedException();
        _pinnedWorldId ??= id;
    }

    private async Task<bool> ProcessSafelyAsync(McpClient mcp, string attachmentId, int npcId,
        string trigger, string playerText, CancellationToken cancellationToken,
        CancellationToken attachmentCancellation, string? messageTurnId = null,
        string senderId = "", string[]? messageIds = null)
    {
        // §160 / #374: a committed physical command owns ordinary autonomous
        // turns until it ends. Dialogue and critical events may still request
        // an explicit replacement; a carried patient needs the next leg chosen.
        if (!CanStartActionTurn(trigger))
            return true;

        var turnId = messageTurnId ?? Guid.NewGuid().ToString("N");
        var consumed = false;
        var stage = "state";
        try
        {
            await PublishPhaseAsync(mcp, attachmentId, turnId, "Thinking", cancellationToken);
            _status.Write(true, "Thinking", npcId, true, true);
            var worldStatus = await mcp.CallToolAsync("world_status", new { }, cancellationToken);
            var state = await mcp.CallToolAsync("describe_colonist", new { npcId }, cancellationToken);
            if (IsUnconscious(state))
            {
                await StopActionAsync().ConfigureAwait(false);
                await mcp.CallToolAsync("commit_agent_turn", new
                {
                    attachmentId, turnId = "body-" + Guid.NewGuid().ToString("N"), reaction = "None",
                    intentSummary = AgentPromptFiles.Text("AgentHostRuntime.03"),
                }, cancellationToken);
                await PublishPhaseAsync(mcp, attachmentId, turnId, "Ready", cancellationToken);
                _status.Write(true, "Ready", npcId, true, true);
                // No LLM/TTS, no social effect, no consumption of the waiting voice.
                return false;
            }
            var world = await _memory.BindHexLiveWorldAsync(
                worldStatus, npcId, _options.WorldId, cancellationToken).ConfigureAwait(false);
            if (trigger == "voice")
            {
                _activeSpeakerKey = SpeakerKey(senderId);
                await _memory.BindSpeakerAsync(_activeSpeakerKey,
                    senderId.Length > 0 && senderId == _options.PlayerClientId, cancellationToken);
                if (await _memory.HasProcessedMessagesAsync(_activeSpeakerKey, messageIds ?? [], cancellationToken))
                    return true;
            }
            world = world with { SpeakerKey = _activeSpeakerKey, MessageIds = messageIds ?? [], PlayerText = playerText };
            if (messageTurnId != null &&
                (await _memory.SnapshotAsync(cancellationToken)).AppliedTurnIds.Contains(turnId))
            {
                Console.Error.WriteLine($"[turn] id={turnId} correlation={ActionCorrelation(turnId)} trigger={trigger} result=duplicate-skipped");
                return true;
            }
            await ImportLegacyStateAsync(world, state, cancellationToken).ConfigureAwait(false);
            await _memory.ObserveHexLiveStateAsync(world, state, cancellationToken)
                .ConfigureAwait(false);
            if (!_conversations.ContainsKey(_activeSpeakerKey) &&
                (await _memory.SnapshotAsync(cancellationToken)).Speakers.TryGetValue(_activeSpeakerKey, out var savedSpeaker))
                _conversations[_activeSpeakerKey] = new Queue<string>(savedSpeaker.RecentConversation.TakeLast(12));
            var recall = BuildRecallQuery(playerText, state, Recent);
            var memoryContext = await _memory.BuildPromptContextAsync(world, recall, cancellationToken)
                .ConfigureAwait(false);
            Console.Error.WriteLine(
                $"[memory] promptChars={memoryContext.CharacterCount} recalled={memoryContext.RecalledFragments}");
            var physicalState = JsonSerializer.Serialize(state.EnumerateObject()
                .Where(property => property.Name != "legacyAgentState")
                .ToDictionary(property => property.Name, property => property.Value));
            stage = "model";
            var decision = await _providers.DecideAsync(trigger, physicalState,
                memoryContext.Text + AgentPromptFiles.Text("AgentHostRuntime.04") + _actionContract +
                AgentPromptFiles.Text("AgentHostRuntime.05") +
                Volatile.Read(ref _actionFeedback) +
                AgentPromptFiles.Text("AgentHostRuntime.06") +
                AgentPromptFiles.Text("AgentHostRuntime.07"),
                playerText, Recent.ToArray(), cancellationToken)
                .ConfigureAwait(false);

            if (trigger != "voice" && string.Equals(decision.Speech.Trim(), _lastSpeech,
                    StringComparison.OrdinalIgnoreCase)) decision.Speech = string.Empty;

            var pending = new PendingAgentTurn
            {
                TurnId = turnId,
                Trigger = trigger,
                World = world,
                Decision = decision,
            };
            stage = "commit";
            _outbox.Add(pending);
            consumed = true; // durable result: never regenerate it because delivery failed
            await _memory.CommitTurnAsync(world, turnId, trigger, decision, cancellationToken)
                .ConfigureAwait(false);
            _outbox.MarkLocalCommitted(turnId);
            await CommitServerViewAsync(mcp, attachmentId, pending, cancellationToken)
                .ConfigureAwait(false);
            _outbox.Remove(turnId);
            LastIntentSummary = decision.IntentSummary;

            if (playerText.Length > 0) Remember(AgentPromptFiles.Text("AgentHostRuntime.08") + playerText);
            if (decision.Speech.Length > 0)
            {
                stage = "speech";
                Remember(_options.DisplayName + ": " + decision.Speech);
                await PublishPhaseAsync(mcp, attachmentId, turnId, "Speaking", cancellationToken);
                await PublishSpeechAsync(mcp, attachmentId, turnId, trigger, decision,
                    cancellationToken).ConfigureAwait(false);
                _lastSpeech = decision.Speech.Trim();
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (decision.Action != null && CanStartActionTurn(trigger))
            {
                try { await ValidateActionAsync(mcp, npcId, decision.Action, cancellationToken); }
                catch (AgentActionValidationException ex)
                {
                    ReportAction(decision.Action.Tool, turnId, ex.ReasonCode);
                    await PublishPhaseAsync(mcp, attachmentId, turnId, "Ready", cancellationToken);
                    _status.Write(true, "Ready", npcId, true, true);
                    return true; // Keep the existing physical action and consume this rejected decision.
                }
                // Transfer the same owner's lease directly to the next command.
                // Releasing it here returns to AI and drops a carried patient.
                await StopActionAsync(handoffLease: true).ConfigureAwait(false);
                _actionStop = CancellationTokenSource.CreateLinkedTokenSource(attachmentCancellation);
                _actionTask = PerformActionSafelyAsync(mcp, npcId, decision.Action, _actionStop.Token, turnId);
            }
            await PublishPhaseAsync(mcp, attachmentId, turnId, "Ready", cancellationToken);
            _status.Write(true, "Ready", npcId, true, true);
            Console.Error.WriteLine($"[turn] id={turnId} correlation={ActionCorrelation(turnId)} trigger={trigger} result=committed");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (consumed) return true;
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[turn] id={turnId} correlation={ActionCorrelation(turnId)} trigger={trigger} stage={stage} consumed={consumed} error={ex.GetType().Name}");
            _status.Write(true, "Error", npcId, true, true, ex.GetType().Name);
            try
            {
                await mcp.CallToolAsync("commit_agent_turn", new
                {
                    attachmentId, turnId = "error-" + turnId, reaction = "None", intentSummary = "",
                }, cancellationToken);
                await PublishPhaseAsync(mcp, attachmentId, turnId, "Error", cancellationToken);
            }
            catch { }
            return consumed;
        }
    }

    private async Task FlushOutboxAsync(McpClient mcp, string attachmentId, int npcId,
        CancellationToken cancellationToken)
    {
        var status = await mcp.CallToolAsync("world_status", new { }, cancellationToken);
        var currentWorld = await _memory.BindHexLiveWorldAsync(status, npcId,
            _options.WorldId, cancellationToken);
        foreach (var item in _outbox.Items)
        {
            if (!item.LocalCommitted)
            {
                await _memory.CommitTurnAsync(item.World, item.TurnId, item.Trigger,
                    item.Decision, cancellationToken).ConfigureAwait(false);
                _outbox.MarkLocalCommitted(item.TurnId);
            }
            // A pending physical Social change belongs to its original world only.
            if (item.World.WorldKey == currentWorld.WorldKey && item.World.EpisodeId == currentWorld.EpisodeId)
                await CommitServerViewAsync(mcp, attachmentId, item, cancellationToken)
                    .ConfigureAwait(false);
            _outbox.Remove(item.TurnId);
        }
    }

    private async Task CommitServerViewAsync(McpClient mcp, string attachmentId,
        PendingAgentTurn item, CancellationToken cancellationToken)
    {
        var archive = await _memory.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var episode = archive.Worlds.FirstOrDefault(x => x.Id == item.World.EpisodeId);
        var journal = episode?.Journal.LastOrDefault()?.Text ?? string.Empty;
        await mcp.CallToolAsync("commit_agent_turn", new
        {
            attachmentId,
            turnId = item.TurnId,
            reaction = item.Decision.Reaction,
            intentSummary = item.Decision.IntentSummary,
            relationView = RelationView(archive, item.World.SpeakerKey),
            journalEntry = journal,
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task RepublishLocalViewAsync(McpClient mcp, string attachmentId, int npcId,
        CancellationToken cancellationToken)
    {
        var worldStatus = await mcp.CallToolAsync("world_status", new { }, cancellationToken);
        var world = await _memory.BindHexLiveWorldAsync(
            worldStatus, npcId, _options.WorldId, cancellationToken).ConfigureAwait(false);
        var state = await mcp.CallToolAsync("describe_colonist", new { npcId }, cancellationToken);
        await ImportLegacyStateAsync(world, state, cancellationToken).ConfigureAwait(false);
        await _memory.ObserveHexLiveStateAsync(world, state, cancellationToken)
            .ConfigureAwait(false);
        var archive = await _memory.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var episode = archive.Worlds.FirstOrDefault(x => x.Id == world.EpisodeId);
        if (_activeSpeakerKey.Length == 0)
        {
            var prefix = SpeakerKey("").Split(':')[0] + ":";
            _activeSpeakerKey = !string.IsNullOrEmpty(_options.PlayerClientId) ? SpeakerKey(_options.PlayerClientId) :
                archive.PrimarySpeakerKey?.StartsWith(prefix, StringComparison.Ordinal) == true
                    ? archive.PrimarySpeakerKey : SpeakerKey("");
        }
        await mcp.CallToolAsync("commit_agent_turn", new
        {
            attachmentId,
            turnId = "attach-" + Guid.NewGuid().ToString("N"),
            reaction = "None",
            intentSummary = string.Empty, // reconnect must not present an old intention as current
            relationView = RelationView(archive, _activeSpeakerKey),
            journalEntry = episode?.Journal.LastOrDefault()?.Text ?? string.Empty,
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ImportLegacyStateAsync(MashaWorldHandle world, JsonElement state,
        CancellationToken cancellationToken)
    {
        if (!state.TryGetProperty("legacyAgentState", out var legacy) ||
            legacy.ValueKind != JsonValueKind.Object) return;
        await _memory.ImportHexLiveLegacyAsync(world, legacy, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task PublishSpeechAsync(McpClient mcp, string attachmentId, string turnId,
        string trigger, CompanionDecision decision, CancellationToken cancellationToken)
    {
        byte[] wav;
        try
        {
            wav = (await _providers.SynthesizeAsync(decision.Speech, cancellationToken)
                .ConfigureAwait(false)).Wav;
            if (wav.Length > 6 * 1024 * 1024 || WavDurationMilliseconds(wav) > 60000)
                throw new InvalidDataException("TTS WAV exceeds the MCP speech limits.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tts] text-only fallback: {ex.GetType().Name}");
            wav = Array.Empty<byte>();
        }

        var sha = Convert.ToHexString(SHA256.HashData(wav)).ToLowerInvariant();
        var utteranceId = Guid.NewGuid().ToString("N");
        var direct = string.Equals(trigger, "voice", StringComparison.Ordinal);
        await mcp.CallToolAsync("begin_agent_utterance", new
        {
            attachmentId,
            utteranceId,
            turnId,
            language = AgentConversationLanguage.SpeechTag(decision.Speech),
            text = decision.Speech,
            emotion = decision.Emotion,
            delivery = direct ? "player_reply" : "world",
            priority = direct ? "Talk" : "Ambient",
            totalBytes = wav.Length,
            durationMs = WavDurationMilliseconds(wav),
            sha256 = sha,
        }, cancellationToken).ConfigureAwait(false);
        const int chunkSize = 128 * 1024;
        for (var offset = 0; offset < wav.Length; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, wav.Length - offset);
            await mcp.CallToolAsync("append_agent_utterance", new
            {
                attachmentId,
                utteranceId,
                chunkIndex = offset / chunkSize,
                base64 = Convert.ToBase64String(wav, offset, count),
            }, cancellationToken).ConfigureAwait(false);
        }
        await mcp.CallToolAsync("commit_agent_utterance", new
        {
            attachmentId,
            utteranceId,
            sha256 = sha,
        }, cancellationToken).ConfigureAwait(false);
    }

    private bool CanStartActionTurn(string trigger) =>
        trigger != "heartbeat" || _actionTask.IsCompleted || _actionAwaitingContinuation;

    private async Task PerformActionAsync(McpClient mcp, int npcId,
        CompanionAction action, CancellationToken cancellationToken, string turnId)
    {
        var arguments = await ValidateActionAsync(mcp, npcId, action, cancellationToken);
        var acquired = false;
        try
        {
            await mcp.CallToolAsync("acquire_npc_control", new { npcId, ttlSeconds = 45 },
                cancellationToken).ConfigureAwait(false);
            acquired = true;
            var outcome = new AgentActionOutcome(npcId,
                await mcp.CallToolAsync("read_events", new { npcId }, cancellationToken).ConfigureAwait(false));
            var result = await mcp.CallToolAsync(action.Tool, arguments, cancellationToken).ConfigureAwait(false);
            if ((result.TryGetProperty("accepted", out var accepted) && accepted.ValueKind == JsonValueKind.False) ||
                (result.TryGetProperty("status", out var rejected) && rejected.GetString() == "Rejected"))
                throw new McpToolRejectedException(result.GetRawText());
            if (result.TryGetProperty("status", out var status) && status.GetString() == "Completed")
            {
                ReportAction(action.Tool, turnId, "Completed");
                return;
            }
            ReportAction(action.Tool, turnId, "Accepted");

            var renew = DateTimeOffset.UtcNow.AddSeconds(9);
            DateTimeOffset? holdingSince = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                var list = await mcp.CallToolAsync("list_colonists", new { }, cancellationToken)
                    .ConfigureAwait(false);
                outcome.Observe(await mcp.CallToolAsync("read_events", new
                    { npcId, sinceSeq = outcome.Watermark, limit = 500 }, cancellationToken).ConfigureAwait(false));
                if (!HasActivePlan(list, npcId))
                {
                    if (!HasCarriedPerson(list, npcId))
                    {
                        if (outcome.Result == null && outcome.HasMore) continue;
                        ReportAction(action.Tool, turnId, outcome.Result ?? PlanResult(list, npcId));
                        break;
                    }
                    // Pickup/movement completion is not the end of transport.
                    // Keep renewing while the next model turn chooses a destination
                    // or explicit put-down; never hold an idle patient indefinitely.
                    holdingSince ??= DateTimeOffset.UtcNow;
                    if (DateTimeOffset.UtcNow - holdingSince.Value >= TimeSpan.FromSeconds(120))
                    {
                        ReportAction(action.Tool, turnId, "CarryContinuationTimeout");
                        break;
                    }
                    _actionAwaitingContinuation = true;
                    ReportAction(action.Tool, turnId, "AwaitingCarryContinuation");
                }
                else
                {
                    holdingSince = null;
                    _actionAwaitingContinuation = false;
                }
                if (DateTimeOffset.UtcNow >= renew)
                {
                    await mcp.CallToolAsync("acquire_npc_control", new
                    {
                        npcId,
                        ttlSeconds = 45,
                    }, cancellationToken).ConfigureAwait(false);
                    renew = DateTimeOffset.UtcNow.AddSeconds(9);
                }
            }
        }
        finally
        {
            if (acquired && !_handoffActionLease) try
            {
                await mcp.CallToolAsync("release_control", new { npcId }, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch { }
        }
    }

    private async Task PerformActionSafelyAsync(McpClient mcp, int npcId,
        CompanionAction action, CancellationToken cancellationToken, string turnId)
    {
        _actionAwaitingContinuation = false;
        try { await PerformActionAsync(mcp, npcId, action, cancellationToken, turnId); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { ReportAction(action.Tool, turnId, "Cancelled"); }
        catch (Exception ex)
        {
            var code = ex switch
            {
                AgentActionValidationException validation => validation.ReasonCode,
                McpRequestException failure => failure.ReasonCode,
                OperationCanceledException => "McpTimeout",
                HttpRequestException => "McpTransportError",
                JsonException => "McpInvalidResponse",
                _ => "ActionFailed"
            };
            ReportAction(action.Tool, turnId, code);
        }
        finally { _actionAwaitingContinuation = false; }
    }

    private async Task<Dictionary<string, JsonElement>> ValidateActionAsync(McpClient mcp, int npcId,
        CompanionAction action, CancellationToken token)
    {
        _actionArguments ??= new AgentActionContract(await mcp.ReadToolCatalogAsync(token));
        return _actionArguments.BindAndValidate(action, npcId);
    }

    private void ReportAction(string tool, string turnId, string code)
    {
        // All three fields are machine identifiers; never include arguments or exception messages.
        var safeTool = AgentProviders.IsAllowedTool(tool) ? tool : "unknown";
        var correlation = ActionCorrelation(turnId);
        var feedback = $"tool={safeTool} turn={correlation} result={code}";
        if (Volatile.Read(ref _actionFeedback) == feedback) return;
        Volatile.Write(ref _actionFeedback, feedback);
        Console.Error.WriteLine($"[action] {feedback}");
    }

    private static string ActionCorrelation(string turnId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(turnId))).ToLowerInvariant()[..16];

    private static string PlanResult(JsonElement response, int npcId)
    {
        if (response.TryGetProperty("colonists", out var rows))
            foreach (var item in rows.EnumerateArray())
                if (item.GetProperty("npcId").GetInt32() == npcId && item.TryGetProperty("planStatus", out var status))
                    return status.GetString() switch
                    {
                        "Completed" => "PlanCompleted", "Failed" => "PlanFailed", "Invalid" => "PlanInvalid",
                        "None" => "PlanNone", _ => "PlanStatusUnavailable"
                    };
        return "PlanStatusUnavailable";
    }

    private async Task StopActionAsync(bool handoffLease = false)
    {
        _handoffActionLease = handoffLease;
        try
        {
            _actionStop?.Cancel();
            await _actionTask.ConfigureAwait(false);
        }
        finally
        {
            _handoffActionLease = false;
            _actionStop?.Dispose();
            _actionStop = null;
            _actionTask = Task.CompletedTask;
        }
    }

    private static async Task PublishPhaseAsync(McpClient mcp, string attachmentId,
        string turnId, string phase, CancellationToken cancellationToken) =>
        await mcp.CallToolAsync("publish_agent_phase", new
        {
            attachmentId,
            turnId,
            phase,
        }, cancellationToken).ConfigureAwait(false);

    private static int SelectNpc(JsonElement response, string profileId)
    {
        if (!response.TryGetProperty("colonists", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("MCP list_colonists response has no colonists array.");
        var matches = rows.EnumerateArray().Where(item =>
            item.TryGetProperty("profileId", out var profile) &&
            string.Equals(profile.GetString(), profileId, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException(
                $"Expected exactly one live profileId={profileId}; found {matches.Length}.");
        return matches[0].GetProperty("npcId").GetInt32();
    }

    public static bool IsUnconscious(JsonElement state) =>
        state.TryGetProperty("stateSummary", out var summary) &&
        (summary.GetString() ?? string.Empty).Split(';', StringSplitOptions.TrimEntries)
            .Contains("unconscious=true", StringComparer.OrdinalIgnoreCase);

    public static string VoiceTurnId(string attachmentId, JsonElement inbox)
    {
        // Hash identities only: never persist/log player text to diagnose delivery.
        var identity = new StringBuilder(attachmentId);
        foreach (var message in inbox.GetProperty("messages").EnumerateArray())
        {
            identity.Append('|').Append(message.GetProperty("seq").GetInt64())
                .Append(':').Append(message.GetProperty("messageId").GetString());
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())))
            .ToLowerInvariant();
    }

    private static string MergePlayerMessages(JsonElement inbox)
    {
        if (!inbox.TryGetProperty("messages", out var messages) ||
            messages.ValueKind != JsonValueKind.Array) return string.Empty;
        var result = new StringBuilder();
        foreach (var item in messages.EnumerateArray())
        {
            if (!item.TryGetProperty("text", out var value)) continue;
            var text = value.GetString()?.Trim() ?? string.Empty;
            if (text.Length == 0) continue;
            if (result.Length > 0) result.AppendLine();
            result.Append(text);
        }
        return result.ToString();
    }

    private static bool ContainsCriticalEvent(JsonElement response)
    {
        if (!response.TryGetProperty("events", out var events) ||
            events.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in events.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var value)) continue;
            var type = value.GetString() ?? string.Empty;
            if (CriticalFragments.Any(fragment => type.Contains(fragment,
                    StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    internal static bool HasCarriedPerson(JsonElement response, int npcId)
    {
        if (!response.TryGetProperty("colonists", out var rows)) return false;
        foreach (var item in rows.EnumerateArray())
            if (item.GetProperty("npcId").GetInt32() == npcId)
                return item.TryGetProperty("carriedNpcId", out var carried) &&
                       carried.ValueKind == JsonValueKind.Number && carried.GetInt32() > 0;
        return false;
    }

    private static bool HasActivePlan(JsonElement response, int npcId)
    {
        if (!response.TryGetProperty("colonists", out var rows)) return false;
        foreach (var item in rows.EnumerateArray())
        {
            if (item.GetProperty("npcId").GetInt32() != npcId) continue;
            return item.TryGetProperty("planStatus", out var status) &&
                   string.Equals(status.GetString(), "Active", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static string RelationView(MashaArchive archive, string key)
    {
        var bond = MashaMemoryStore.BondFor(archive, key);
        return JsonSerializer.Serialize(new
        {
            familiarity = Math.Clamp(bond.Familiarity, 0f, 1f),
            trust = Math.Clamp(bond.Trust, 0f, 1f),
            affinity = Math.Clamp(bond.Affinity, -1f, 1f),
            speakerId = key.Length > 0 ? key.Split(':').Last() : "",
            voiceName = archive.Speakers.TryGetValue(key, out var speaker) ? speaker.VoiceName : AgentPromptFiles.Text("AgentHostRuntime.15")
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static int WavDurationMilliseconds(byte[] wav) => wav.Length < 44
        ? 0
        : Math.Max((int)Math.Round((wav.Length - 44) / 88.2), 0);

    private static string RequiredString(JsonElement value, string property)
    {
        var result = value.GetProperty(property).GetString()?.Trim() ?? string.Empty;
        return result.Length > 0 ? result : throw new InvalidDataException($"Missing {property}.");
    }

    private static string BuildActionContract(JsonElement catalog)
    {
        var text = new StringBuilder();
        foreach (var tool in catalog.GetProperty("tools").EnumerateArray())
        {
            var name = tool.GetProperty("name").GetString() ?? "";
            if (!AgentProviders.IsAllowedTool(name)) continue;
            text.Append(name).Append(": ").AppendLine(tool.TryGetProperty("description", out var description)
                ? description.GetString() : AgentPromptFiles.Text("AgentHostRuntime.16"));
            text.AppendLine(tool.GetProperty("inputSchema").GetRawText());
        }
        return text.ToString();
    }

    private static string BuildRecallQuery(string playerText, JsonElement state,
        IEnumerable<string> recent)
    {
        var query = new StringBuilder();
        if (playerText.Length > 0) query.AppendLine(playerText);
        foreach (var property in new[] { "stateSummary", "perceptionSummary" })
        {
            if (state.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
                query.AppendLine(value.GetString());
        }
        foreach (var line in recent.TakeLast(4)) query.AppendLine(line);
        return query.Length <= 3000 ? query.ToString() : query.ToString(0, 3000);
    }

    private void Remember(string line)
    {
        Recent.Enqueue(line);
        while (Recent.Count > 12) Recent.Dequeue();
    }

    private sealed class PresenceState
    {
        private readonly object _gate = new();
        private CancellationTokenSource? _turn;
        private int _value;
        public PresenceState(bool value) => _value = value ? 1 : 0;
        public bool Value
        {
            get => Volatile.Read(ref _value) != 0;
            set
            {
                lock (_gate)
                {
                    Interlocked.Exchange(ref _value, value ? 1 : 0);
                    if (!value) _turn?.Cancel();
                }
            }
        }
        public void BeginTurn(CancellationTokenSource turn)
        {
            lock (_gate) { _turn = turn; if (!Value) turn.Cancel(); }
        }
        public void EndTurn() { lock (_gate) _turn = null; }
    }
}

public sealed class AgentTargetChangedException : InvalidOperationException
{
    public AgentTargetChangedException() : base("Agent target changed; explicit selection required.") { }
}
