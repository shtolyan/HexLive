using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace HexLive.AgentHost;

/// <summary>
/// Portable agent process. It is only an MCP client: there is no local HTTP,
/// WebSocket, audio or memory endpoint for Unity to discover.
/// </summary>
public sealed class AgentHostRuntime
{
    private static readonly string[] Capabilities =
        ["playerText", "speech", "worldActions", "relationView", "journal"];
    private static readonly string[] CriticalFragments =
        ["Wound", "Hit", "Attack", "Fainted", "Dying", "Death", "Threat"];
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AttachmentHeartbeat = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ModelHeartbeat = TimeSpan.FromSeconds(30);

    private readonly AgentHostOptions _options;
    private readonly MashaMemoryStore _memory;
    private readonly IAgentProviders _providers;
    private readonly HttpMessageHandler? _mcpHandler;
    private readonly AgentTurnOutbox _outbox;
    private readonly AgentHostStatusStore _status;
    private readonly Queue<string> _recent = new();
    private CancellationTokenSource? _actionStop;
    private Task _actionTask = Task.CompletedTask;
    private string _actionContract = string.Empty;
    private string _actionFeedback = "Физических приказов в этой сессии ещё не было.";

    public AgentHostRuntime(AgentHostOptions options, IAgentProviders? providers = null,
        HttpMessageHandler? mcpHandler = null)
    {
        _options = options;
        _memory = new MashaMemoryStore(options.MemoryDirectory);
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
        _status.Write(true, "Starting", 0, false, false);
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
                _status.Write(true, "Reconnecting", 0, false, false, ex.GetType().Name);
                Console.Error.WriteLine($"[agent] reconnecting after {ex.GetType().Name}");
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
        _status.Write(false, "Stopped", 0, false, false);
        _providers.Dispose();
    }

    private async Task RunAttachedAsync(CancellationToken cancellationToken)
    {
        using var mcp = new McpClient(_options.ProviderOptions, _mcpHandler);
        var colonists = await mcp.CallToolAsync("list_colonists", new { }, cancellationToken);
        var npcId = SelectNpc(colonists, _options.ProfileId);
        var catalog = await mcp.ReadToolCatalogAsync(cancellationToken);
        _actionContract = BuildActionContract(catalog);
        _actionFeedback = "Новая MCP-сессия: результаты прежних приказов неизвестны.";
        var attached = await mcp.CallToolAsync("attach_agent", new
        {
            npcId,
            displayName = _options.DisplayName,
            capabilities = Capabilities,
            ttlSeconds = 45,
        }, cancellationToken);
        var attachmentId = RequiredString(attached, "attachmentId");
        var presence = new PresenceState(attached.TryGetProperty("playerPresent", out var present) &&
                                         present.GetBoolean());
        _status.Write(true, presence.Value ? "Ready" : "Sleeping", npcId, true, presence.Value);
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

                var inbox = await mcp.CallToolAsync("read_agent_inbox", new
                {
                    attachmentId,
                    sinceSeq = inboxWatermark,
                    limit = 16,
                }, cancellationToken).ConfigureAwait(false);
                if (inbox.TryGetProperty("watermark", out var watermark))
                    inboxWatermark = watermark.GetInt64();
                var playerText = MergePlayerMessages(inbox);

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
                        await ProcessSafelyAsync(mcp, attachmentId, npcId, trigger, playerText,
                            turnStop.Token, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !presence.Value)
                    { /* player left: do not start another paid operation */ }
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
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(AttachmentHeartbeat, cancellationToken).ConfigureAwait(false);
            var result = await mcp.CallToolAsync("agent_heartbeat", new { attachmentId },
                cancellationToken).ConfigureAwait(false);
            var current = result.TryGetProperty("playerPresent", out var value) && value.GetBoolean();
            presence.Value = current;
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
                _status.Write(true, current ? "Ready" : "Sleeping", npcId, true, current);
        }
    }

    private async Task ProcessSafelyAsync(McpClient mcp, string attachmentId, int npcId,
        string trigger, string playerText, CancellationToken cancellationToken,
        CancellationToken attachmentCancellation)
    {
        var turnId = Guid.NewGuid().ToString("N");
        try
        {
            await PublishPhaseAsync(mcp, attachmentId, turnId, "Thinking", cancellationToken);
            _status.Write(true, "Thinking", npcId, true, true);
            var worldStatus = await mcp.CallToolAsync("world_status", new { }, cancellationToken);
            var state = await mcp.CallToolAsync("describe_colonist", new { npcId }, cancellationToken);
            var world = await _memory.BindHexLiveWorldAsync(
                worldStatus, npcId, _options.WorldId, cancellationToken).ConfigureAwait(false);
            await ImportLegacyStateAsync(world, state, cancellationToken).ConfigureAwait(false);
            await _memory.ObserveHexLiveStateAsync(world, state, cancellationToken)
                .ConfigureAwait(false);
            var recall = BuildRecallQuery(playerText, state, _recent);
            var memoryContext = await _memory.BuildPromptContextAsync(world, recall, cancellationToken)
                .ConfigureAwait(false);
            Console.Error.WriteLine(
                $"[memory] promptChars={memoryContext.CharacterCount} recalled={memoryContext.RecalledFragments}");
            var physicalState = JsonSerializer.Serialize(state.EnumerateObject()
                .Where(property => property.Name != "legacyAgentState")
                .ToDictionary(property => property.Name, property => property.Value));
            var decision = await _providers.DecideAsync(trigger, physicalState,
                memoryContext.Text + "\nДопустимые arguments (npcId подставляется автоматически):\n" + _actionContract +
                "\nРезультат последнего физического приказа (не новая просьба):\n" +
                Volatile.Read(ref _actionFeedback) +
                "\nПри отказе исправь причину; не обещай выполненное лечение до подтверждения состоянием. " +
                "TreatSelf — перевязка готовым бинтом; treat_limbs — шина/протез, не замена бинта.",
                playerText, _recent.ToArray(), cancellationToken)
                .ConfigureAwait(false);

            var pending = new PendingAgentTurn
            {
                TurnId = turnId,
                Trigger = trigger,
                World = world,
                Decision = decision,
            };
            _outbox.Add(pending);
            await _memory.CommitTurnAsync(world, turnId, trigger, decision, cancellationToken)
                .ConfigureAwait(false);
            _outbox.MarkLocalCommitted(turnId);
            await CommitServerViewAsync(mcp, attachmentId, pending, cancellationToken)
                .ConfigureAwait(false);
            _outbox.Remove(turnId);

            if (playerText.Length > 0) Remember("Игрок: " + playerText);
            if (decision.Speech.Length > 0)
            {
                Remember(_options.DisplayName + ": " + decision.Speech);
                await PublishPhaseAsync(mcp, attachmentId, turnId, "Speaking", cancellationToken);
                await PublishSpeechAsync(mcp, attachmentId, turnId, trigger, decision,
                    cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (decision.Action != null)
            {
                await StopActionAsync().ConfigureAwait(false);
                _actionStop = CancellationTokenSource.CreateLinkedTokenSource(attachmentCancellation);
                _actionTask = PerformActionSafelyAsync(mcp, npcId, decision.Action, _actionStop.Token);
            }
            await PublishPhaseAsync(mcp, attachmentId, turnId, "Ready", cancellationToken);
            _status.Write(true, "Ready", npcId, true, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[turn] {turnId} failed: {ex.GetType().Name}");
            _status.Write(true, "Error", npcId, true, true, ex.GetType().Name);
            try { await PublishPhaseAsync(mcp, attachmentId, turnId, "Error", cancellationToken); }
            catch { }
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
            relationView = RelationView(archive.PlayerBond),
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
        await mcp.CallToolAsync("commit_agent_turn", new
        {
            attachmentId,
            turnId = "attach-" + Guid.NewGuid().ToString("N"),
            reaction = "None",
            intentSummary = episode?.LastIntentSummary ?? string.Empty,
            relationView = RelationView(archive.PlayerBond),
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
            if (wav.Length > 3 * 1024 * 1024 || WavDurationMilliseconds(wav) > 30000)
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
            language = "ru",
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

    private async Task PerformActionAsync(McpClient mcp, int npcId,
        CompanionAction action, CancellationToken cancellationToken)
    {
        await mcp.CallToolAsync("acquire_npc_control", new { npcId, ttlSeconds = 45 },
            cancellationToken).ConfigureAwait(false);
        try
        {
            var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                                action.Arguments.GetRawText()) ?? new();
            using var npcDocument = JsonDocument.Parse(
                npcId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            arguments["npcId"] = npcDocument.RootElement.Clone();
            await mcp.CallToolAsync(action.Tool, arguments, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _actionFeedback, action.Tool + ": Accepted — приказ принят, выполнение ещё не подтверждено.");

            var renew = DateTimeOffset.UtcNow.AddSeconds(9);
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                var list = await mcp.CallToolAsync("list_colonists", new { }, cancellationToken)
                    .ConfigureAwait(false);
                if (!HasActivePlan(list, npcId))
                {
                    Volatile.Write(ref _actionFeedback, action.Tool + ": PlanEnded — план закончился; успех проверь по состоянию, это не подтверждение результата.");
                    break;
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
            try
            {
                await mcp.CallToolAsync("release_control", new { npcId }, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch { }
        }
    }

    private async Task PerformActionSafelyAsync(McpClient mcp, int npcId,
        CompanionAction action, CancellationToken cancellationToken)
    {
        try { await PerformActionAsync(mcp, npcId, action, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { Volatile.Write(ref _actionFeedback, action.Tool + ": Cancelled — приказ прерван, не считай его выполненным."); }
        catch (Exception ex)
        {
            var code = ex is McpToolRejectedException rejection ? rejection.ReasonCode : ex.GetType().Name;
            Volatile.Write(ref _actionFeedback, action.Tool + ": " + code + " — действие не подтверждено. Проверь аргументы и необходимые припасы.");
            Console.Error.WriteLine($"[action] tool={action.Tool} result={code}");
        }
    }

    private async Task StopActionAsync()
    {
        _actionStop?.Cancel();
        await _actionTask.ConfigureAwait(false);
        _actionStop?.Dispose();
        _actionStop = null;
        _actionTask = Task.CompletedTask;
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
            if (result.Length >= 1000) break;
        }
        return result.Length <= 1000 ? result.ToString() : result.ToString(0, 1000);
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

    private static string RelationView(PortablePlayerBond bond) =>
        JsonSerializer.Serialize(new
        {
            familiarity = Math.Clamp(bond.Familiarity, 0f, 1f),
            trust = Math.Clamp(bond.Trust, 0f, 1f),
            affinity = Math.Clamp(bond.Affinity, 0f, 1f),
        });

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
            text.Append(name).Append(": ").AppendLine(tool.GetProperty("description").GetString());
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
        _recent.Enqueue(line);
        while (_recent.Count > 12) _recent.Dequeue();
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
