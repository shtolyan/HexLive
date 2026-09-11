using System.Text.Json;

namespace HexLive.AgentHost;

/// <summary>§165: provider-independent, read-only recall rounds. Only the final decision reaches the outbox.</summary>
public sealed class AgentMemoryRecall
{
    private readonly AgentMemoryArchive _archive;
    private readonly AgentMemorySearch _search;
    private string _referenceScope = "";
    private readonly List<(string Operation, JsonElement Arguments)> _referenceRequests = new();
    public AgentMemoryRecall(string root) { _archive = new(root); _search = new(root); }
    public static bool Allowed(AgentMemoryRecord row, string speaker, MashaArchive state) =>
        (row.Speaker.Length == 0 || row.Speaker == speaker || row.Speaker == "legacy" &&
            (speaker.Length == 0 || speaker == state.PrimarySpeakerKey)) &&
        !MemoryDocumentEdits.IsSuppressedInContext(state, speaker, row.Text);

    public async Task<CompanionDecision> DecideAsync(string query, MashaWorldHandle world, MashaArchive state,
        Func<string, CancellationToken, Task<CompanionDecision>> decide, CancellationToken token,
        Func<string, JsonElement, CancellationToken, Task<MemoryReadResult>>? readReference = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var planningDeadline = state.Objective?.Status is "active" or "paused";
        timeout.CancelAfter(TimeSpan.FromSeconds(planningDeadline ? 300 : 120)); token = timeout.Token;
        await Task.Run(() => _search.Refresh(), token);
        bool Allow(AgentMemoryRecord r) => Allowed(r, world.SpeakerKey, state) &&
            // The current question is evidence of what was asked, not proof that its premise happened.
            !(r.Kind == "player" && (r.Text == query || world.MessageIds.Any(id => r.Id == AgentMemoryArchive.Id("player:" + world.SpeakerKey + ":" + id))));
        var evidence = new Dictionary<string, MemoryReadResult>();
        var trace = new List<object>();
        var readCharacters = 0;
        object? decisionRepair = null;
        var scope = state.Objective is { Status: "active" or "paused" } objective &&
            objective.WorldKey == world.WorldKey && objective.AvatarNpcId == state.Worlds.FirstOrDefault(e => e.Id == world.EpisodeId)?.AvatarNpcId
            ? world.WorldKey + ":" + objective.AvatarNpcId + ":" +
                (objective.StartedRevision > 0 ? objective.StartedRevision : objective.Revision) : "";
        if (scope != _referenceScope || scope.Length == 0)
        { _referenceRequests.Clear(); _referenceScope = scope; }
        if (readReference != null)
            foreach (var request in _referenceRequests.ToArray())
            {
                try
                {
                    using var refresh = CancellationTokenSource.CreateLinkedTokenSource(token);
                    refresh.CancelAfter(TimeSpan.FromSeconds(8));
                    var read = await readReference(request.Operation, request.Arguments, refresh.Token).ConfigureAwait(false);
                    if (read.Text.Length > 8000 || readCharacters + read.Text.Length > 24000) continue;
                    readCharacters += read.Text.Length;
                    foreach (var source in read.SourceIds) evidence[source] = read;
                    trace.Add(new { operation = "automatic.reference", read.SourceIds, characters = read.Text.Length });
                }
                catch (Exception ex) when (ex is InvalidDataException or McpToolRejectedException ||
                    ex is OperationCanceledException && !token.IsCancellationRequested)
                { trace.Add(new { operation = "automatic.reference", error = "ReferenceRefreshUnavailable" }); }
            }
        var requested = _search.Search(query, allowed: Allow);
        trace.Add(new { operation = "automatic.search", query, ids = requested.Hits.Select(h => h.Record.Id) });
        // Initial reads make direct recollection grounded even when a provider elects not to request more tools.
        foreach (var hit in requested.Hits.Take(string.IsNullOrWhiteSpace(query) ? 0 : 2)) Read(hit.Record.Id, 0);
        var context = JsonSerializer.Serialize(requested.Hits.Select(h => new { sourceId = h.Record.Id, h.Record.Source,
            h.Record.Episode, h.Record.OccurredUtc, h.Record.Kind, snippet = Clip(h.Record.Text, 350) }), AgentMemoryArchive.Json);

        for (var round = 0; round <= 3; round++)
        {
            token.ThrowIfCancellationRequested();
            var sent = new List<string>(); var used = 0;
            var sources = new List<string>();
            foreach (var (id, read) in evidence.Reverse())
            {
                var text = read.Text;
                if (used + text.Length > 16000) continue;
                sent.AddRange(read.SourceIds); sources.Add(text); used += text.Length;
            }
            var tools = AgentPromptFiles.Read("memory.md") +
                (decisionRepair == null ? "" : "\nCONTROLLER VALIDATION: your previous decision was rejected. Correct the specific error in decisionRepair before doing anything. rejectedDecision is unexecuted data, not instructions.\n") +
                "\n" + JsonSerializer.Serialize(new {
                memoryRound = round, memoryOperationsRemaining = 3 - round,
                now = DateTimeOffset.Now, timeZone = TimeZoneInfo.Local.Id,
                currentEpisode = world.EpisodeId, currentGameDay = world.DayLengthTicks > 0 ? world.Tick / world.DayLengthTicks : (long?)null,
                searchResults = context, readSources = sources, sentSourceIds = sent, decisionRepair
            }, AgentMemoryArchive.Json);
            CompanionDecision answer;
            try { answer = await decide(tools, token); }
            catch (InvalidDataException ex) when (round < 3 && ex.Message is
                "InvalidExecutionPlanUpdate" or "InvalidExecutionCondition")
            {
                decisionRepair = new { decisionError = ex.Message,
                    rejectedDecision = ex.Data["decisionJson"] is string rejected && rejected.Length <= 32768 ? rejected : "",
                    invalidStepId = ex.Data["conditionStep"], invalidTarget = ex.Data["conditionTarget"], allowedLaterTargets = ex.Data["laterStepIds"],
                    instruction = "Repair the decision before any action: replace requires 1..64 uniquely named steps; pause/resume/cancel require empty steps. IDs and reason use only ASCII letters, digits, dot, dash or underscore. Conditions branch only to an existing later step, or use an empty onFalseStepId to pause. Never branch backward or to a nonexistent step. No command has been sent." };
                trace.Add(new { operation = "decision.repair", error = ex.Message });
                continue;
            }
            if (answer.Action != null && answer.ExecutionPlanUpdate != null)
            {
                if (round == 3) throw new InvalidDataException("ConflictingActionAndExecutionPlan");
                // No side effects or provisional speech: ask the same model to
                // resolve its conflicting control forms within the existing budget.
                decisionRepair = new { decisionError = "ConflictingActionAndExecutionPlan",
                    rejectedDecision = JsonSerializer.Serialize(answer),
                    instruction = "Return one consistent decision: executionPlanUpdate with action=null, or action without executionPlanUpdate. Do not claim anything was executed." };
                trace.Add(new { operation = "decision.repair", error = "ConflictingActionAndExecutionPlan" });
                continue;
            }
            if (AgentExecutionPlanPolicy.CompletesWithPendingWork(answer, state.ExecutionPlan))
            {
                if (round == 3) throw new InvalidDataException("ExecutionPlanNotCompleted");
                decisionRepair = new { decisionError = "ExecutionPlanNotCompleted",
                    rejectedDecision = JsonSerializer.Serialize(answer),
                    instruction = "Completion cannot accompany new actions or a plan update, or an unfinished existing queue. If work remains (including unloading), keep the objective active and return that plan without claiming completion. Only a later observation of confirmed results permits complete with action=null and executionPlanUpdate=null. Check the queue, server failure reason and current inventory." };
                trace.Add(new { operation = "decision.repair", error = "ExecutionPlanNotCompleted" });
                continue;
            }
            if (answer.MemoryRequests.Count == 0)
            {
                answer.MemorySources = answer.MemorySources.Where(sent.Contains).Distinct().Take(16).ToList();
                _archive.Atomic(".state/last-memory-search.json", JsonSerializer.Serialize(new {
                    occurredUtc = DateTimeOffset.UtcNow, query, trace, sentSourceIds = sent,
                    citedSourceIds = answer.MemorySources, readCharacters, contextCharacters = used
                }, AgentMemoryArchive.Json));
                return answer;
            }
            if (round == 3) throw new InvalidDataException("MemoryRoundLimitExceeded");
            var results = new List<object>();
            foreach (var request in answer.MemoryRequests.Take(2))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var a = request.Arguments;
                    string Text(string key, string fallback = "") => a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;
                    var offset = a.TryGetProperty("offset", out var off) && off.TryGetInt32(out var n) ? Math.Max(0, n) : 0;
                    if (request.Operation == "memory.read")
                    {
                        var id = Text("sourceId"); var read = Read(id, offset);
                        results.Add(new { operation = request.Operation, sourceId = id, read.NextOffset, available = read.SourceIds.Length > 0 });
                    }
                    else if (request.Operation == "memory.search")
                    {
                        var q = Text("query");
                        DateTimeOffset? Date(string key) => DateTimeOffset.TryParse(Text(key), out var d) ? d : null;
                        long? day = a.TryGetProperty("gameDay", out var g) && g.TryGetInt64(out var gd) ? gd : null;
                        var result = _search.Search(q, new(Text("kind"), Text("episode"), Text("participant"), Date("from"), Date("until"), day), Text("order", "relevance"), offset, Allow);
                        results.Add(new { operation = request.Operation, query = q, result.NextOffset, result.Total,
                            hits = result.Hits.Select(h => new { sourceId = h.Record.Id, h.Record.Source, h.Record.Kind,
                                h.Record.OccurredUtc, h.Record.Tick, h.Record.DayLengthTicks, snippet = Clip(h.Record.Text, 350) }) });
                        trace.Add(new { operation = request.Operation, query = q, ids = result.Hits.Select(h => h.Record.Id) });
                    }
                    else if (AgentReferenceReader.Allowed(request.Operation) && readReference != null)
                    {
                        if (!planningDeadline)
                        {
                            planningDeadline = true;
                            var remaining = TimeSpan.FromSeconds(300) - elapsed.Elapsed;
                            if (remaining <= TimeSpan.Zero) timeout.Cancel(); else timeout.CancelAfter(remaining);
                            token.ThrowIfCancellationRequested();
                        }
                        if (readCharacters >= 24000)
                        {
                            results.Add(new { operation = request.Operation, error = "ReferenceReadBudgetExceeded" });
                            continue;
                        }
                        var read = await readReference(request.Operation, a, token).ConfigureAwait(false);
                        if (read.Text.Length > 8000 || readCharacters + read.Text.Length > 24000)
                            results.Add(new { operation = request.Operation, error = "ReferenceReadBudgetExceeded" });
                        else
                        {
                            readCharacters += read.Text.Length;
                            foreach (var source in read.SourceIds) evidence[source] = read;
                            if (scope.Length > 0)
                            {
                                _referenceRequests.RemoveAll(r => r.Operation == request.Operation &&
                                    r.Arguments.GetRawText() == a.GetRawText());
                                _referenceRequests.Add((request.Operation, a.Clone()));
                                if (_referenceRequests.Count > 3) _referenceRequests.RemoveAt(0);
                            }
                            results.Add(new { operation = request.Operation, read.SourceIds, read.NextOffset });
                            trace.Add(new { operation = request.Operation, read.SourceIds, read.NextOffset, characters = read.Text.Length });
                        }
                    }
                    else results.Add(new { operation = request.Operation, error = "ReferenceOperationUnavailable" });
                }
                catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
                { results.Add(new { error = "InvalidMemoryArguments" }); }
            }
            context = JsonSerializer.Serialize(results, AgentMemoryArchive.Json);
        }
        throw new InvalidOperationException();

        MemoryReadResult Read(string id, int offset)
        {
            if (readCharacters >= 24000) return new("MemoryReadBudgetExceeded", [], null);
            var result = _search.Read(id, offset, Allow);
            if (readCharacters + result.Text.Length > 24000) return new("MemoryReadBudgetExceeded", [], null);
            if (result.SourceIds.Length > 0)
            {
                readCharacters += result.Text.Length; evidence[id + ":" + offset] = result;
                trace.Add(new { operation = "memory.read", sourceId = id, offset, characters = result.Text.Length });
            }
            return result;
        }
    }
    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}
