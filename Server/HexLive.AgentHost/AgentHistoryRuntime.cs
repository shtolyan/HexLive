using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HexLive.AgentHost;

public sealed partial class AgentHostRuntime
{
    private MashaWorldHandle? _historyWorld;
    private readonly ConcurrentDictionary<int, string> _historyNames = new();
    private readonly ConcurrentDictionary<string, MashaWorldHandle> _actionWorlds = new();
    private readonly Dictionary<int, string> _observedHealth = new();
    private string? _lastPosition;
    private string? _movementFrom;
    private int _stationarySamples;
    private void ArchiveObservation(JsonElement state, MashaWorldHandle world)
    {
        RememberNames(state);
        if (state.TryGetProperty("position", out var position))
        {
            var current = position.GetRawText();
            if (_lastPosition != null && current != _lastPosition)
            { _movementFrom ??= _lastPosition; _stationarySamples = 0; }
            else if (_movementFrom != null && ++_stationarySamples >= 2)
            {
                _memory.History.Append(new() { Id = AgentMemoryArchive.Id("movement:" + world.EpisodeId + ":" + world.Tick + ":" + current),
                    Kind = "action", Source = "observation", Episode = world.EpisodeId, Group = "movement:" + world.Tick,
                    Text = string.Format(AgentPromptFiles.Text("HistoryMovement"), _movementFrom, current), Status = "observed-stop",
                    OccurredUtc = DateTimeOffset.UtcNow, Tick = world.Tick, DayLengthTicks = world.DayLengthTicks });
                _movementFrom = null;
            }
            _lastPosition = current;
        }
        if (!state.TryGetProperty("visibleNpcs", out var visible)) return;
        foreach (var npc in visible.EnumerateArray())
        {
            if (!npc.TryGetProperty("npcId", out var value)) continue;
            var id = value.GetInt32();
            var facts = JsonSerializer.Serialize(new {
                dying = npc.TryGetProperty("dying", out var dying) && dying.ValueKind == JsonValueKind.True,
                unconscious = npc.TryGetProperty("unconscious", out var unconscious) && unconscious.ValueKind == JsonValueKind.True
            });
            if (_observedHealth.TryGetValue(id, out var before) && before == facts) continue;
            _observedHealth[id] = facts;
            if (before == null && !facts.Contains("true", StringComparison.Ordinal)) continue;
            _memory.History.Append(new() { Id = AgentMemoryArchive.Id("observation:" + world.EpisodeId + ":" + world.Tick + ":" + id + ":" + facts),
                Kind = "event", Source = "observation", Episode = world.EpisodeId, Group = "person:" + id,
                Text = string.Format(AgentPromptFiles.Text("HistoryObservedHealth"), WithNames("NPC" + id), before ?? "unknown", facts),
                OccurredUtc = DateTimeOffset.UtcNow, Tick = world.Tick, DayLengthTicks = world.DayLengthTicks });
        }
    }
    private static readonly Lazy<Dictionary<string, string>> HistoryLabels = new(() =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(AgentPromptFiles.Read("history-events.json"))!);
    private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> NameLabels = new(() =>
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(AgentPromptFiles.Read("npc-names.json"))!);
    private static string HistoryLabel(string type) => HistoryLabels.Value.GetValueOrDefault(type,
        type.StartsWith("Crafted", StringComparison.Ordinal) ? HistoryLabels.Value["Crafted"] : type);

    private void ArchiveInbox(JsonElement inbox)
    {
        if (!inbox.TryGetProperty("messages", out var messages)) return;
        var world = Volatile.Read(ref _historyWorld);
        _memory.History.AppendMany(messages.EnumerateArray().Select(m => {
            var sender = m.TryGetProperty("senderId", out var s) ? s.GetString() ?? "" : "";
            var speaker = SpeakerKey(sender);
            var id = m.GetProperty("messageId").GetString() ?? "";
            var occurred = m.TryGetProperty("createdUtc", out var d) && d.TryGetDateTimeOffset(out var at) ? at : (DateTimeOffset?)null;
            return new AgentMemoryRecord { Id = AgentMemoryArchive.Id("player:" + speaker + ":" + id), Kind = "player",
                Text = m.GetProperty("text").GetString() ?? "", Speaker = speaker, Group = speaker, Source = "player",
                Episode = world?.EpisodeId ?? "", OccurredUtc = occurred, Tick = world?.Tick,
                DayLengthTicks = world?.DayLengthTicks ?? 0, Incomplete = occurred == null };
        }));
    }
    private void RememberNames(JsonElement state)
    {
        if (state.TryGetProperty("colonists", out var roster))
            foreach (var npc in roster.EnumerateArray())
                if (npc.TryGetProperty("npcId", out var id) && npc.TryGetProperty("name", out var label))
                {
                    var name = label.GetString() ?? "";
                    _historyNames[id.GetInt32()] = NameLabels.Value.TryGetValue(name, out var translated)
                        ? string.Join(" / ", translated.Values) : name;
                }
        if (state.TryGetProperty("visibleNpcs", out var visible))
            foreach (var npc in visible.EnumerateArray())
                if (npc.TryGetProperty("npcId", out var id) && id.TryGetInt32(out var number))
                    _historyNames[number] = npc.TryGetProperty("names", out var names)
                        ? string.Join(" / ", names.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => p.Value.GetString()))
                        : npc.TryGetProperty("nameId", out var name) ? name.GetString() ?? "" : "";
    }
    private string WithNames(string text) => Regex.Replace(text, @"NPC(\d+)", match =>
        int.TryParse(match.Groups[1].Value, out var id) && _historyNames.TryGetValue(id, out var name)
            ? match.Value + " (" + name + ")" : match.Value);

    private async Task CollectHistoryAsync(McpClient mcp, int npcId, CancellationToken token)
    {
        _observedHealth.Clear(); _lastPosition = null; _movementFrom = null; _stationarySamples = 0;
        var cursorPath = ".state/event-cursor-" + AgentMemoryArchive.Id(_options.McpUri + ":" + _pinnedWorldId + ":" + npcId) + ".json";
        long cursor = 0; var epoch = "";
        var nextObservation = DateTimeOffset.MinValue;
        var path = _memory.History.SafePath(cursorPath);
        if (File.Exists(path))
        {
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            cursor = saved.RootElement.GetProperty("cursor").GetInt64();
            epoch = saved.RootElement.GetProperty("epoch").GetString() ?? "";
        }
        while (!token.IsCancellationRequested)
        {
            var status = await mcp.CallToolAsync("world_status", new { }, token);
            PinWorld(status);
            var oldWorld = Volatile.Read(ref _historyWorld);
            if (oldWorld == null) { await Task.Delay(100, token); continue; }
            var tick = status.TryGetProperty("tick", out var t) ? t.GetInt64() : oldWorld.Tick;
            var day = status.TryGetProperty("dayLengthTicks", out var d) ? d.GetInt32() : oldWorld.DayLengthTicks;
            var world = oldWorld with { Tick = tick, DayLengthTicks = day };
            Volatile.Write(ref _historyWorld, world);
            if (DateTimeOffset.UtcNow >= nextObservation)
            {
                ArchiveObservation(await mcp.CallToolAsync("describe_colonist", new { npcId }, token), world);
                nextObservation = DateTimeOffset.UtcNow.AddSeconds(4);
            }
            var batch = await mcp.CallToolAsync("read_events", new { npcId, sinceSeq = cursor, limit = 500 }, token);
            var nextEpoch = batch.TryGetProperty("sessionEpoch", out var e) ? e.GetString() ?? "" : "";
            var changed = epoch.Length > 0 && nextEpoch != epoch;
            if (changed)
            {
                _memory.History.Append(new() { Id = AgentMemoryArchive.Id(cursorPath + ":epoch:" + epoch + ":" + nextEpoch),
                    Kind = "gap", Text = AgentPromptFiles.Text("HistoryEpochGap"), Episode = world.EpisodeId,
                    Source = "server-ring", Incomplete = true, OccurredUtc = DateTimeOffset.UtcNow });
                _observedHealth.Clear(); _lastPosition = null; _movementFrom = null; _stationarySamples = 0;
                epoch = nextEpoch; cursor = 0; continue;
            }
            epoch = nextEpoch;
            var rows = new List<AgentMemoryRecord>();
            if (batch.TryGetProperty("gap", out var gap) && gap.ValueKind == JsonValueKind.True)
                rows.Add(new() { Id = AgentMemoryArchive.Id(cursorPath + ":gap:" + epoch + ":" + cursor), Kind = "gap",
                    Text = AgentPromptFiles.Text("HistoryRingGap"), Source = "server-ring", Episode = world.EpisodeId,
                    Incomplete = true, OccurredUtc = DateTimeOffset.UtcNow, Tick = tick, DayLengthTicks = day });
            if (batch.TryGetProperty("events", out var events))
                foreach (var item in events.EnumerateArray())
                {
                    var seq = item.GetProperty("seq").GetInt64();
                    var eventTick = item.GetProperty("tick").GetInt64();
                    var type = item.GetProperty("type").GetString() ?? "";
                    var message = item.GetProperty("message").GetString() ?? "";
                    // Numeric policy internals are neither an observable act nor a character's motive.
                    message = Regex.Replace(message, @"\b(?:Score|Roll)=[^ ]+\s*", "");
                    rows.Add(new() { Id = AgentMemoryArchive.Id(cursorPath + ":" + epoch + ":" + seq), Kind = "event",
                        Text = WithNames(type + " " + HistoryLabel(type) + " " + message), Source = "server-ring:" + epoch,
                        Episode = world.EpisodeId, Group = "events:" + epoch, Tick = eventTick, DayLengthTicks = day,
                        // The old API supplies game ticks, not the wall time at which historical events occurred.
                        OccurredUtc = eventTick == tick ? DateTimeOffset.UtcNow : null, Incomplete = eventTick != tick });
                }
            _memory.History.AppendMany(rows);
            cursor = batch.TryGetProperty("watermark", out var mark) ? mark.GetInt64() : cursor;
            _memory.History.Atomic(cursorPath, JsonSerializer.Serialize(new { cursor, epoch }, AgentMemoryArchive.Json));
            // Archive catch-up can contain old threats already handled by the live lane.
            // Only the attachment's forward event cursor may interrupt current execution.
            if (batch.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True) continue;
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
    }
}
