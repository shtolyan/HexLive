using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HexLive.AgentHost;

/// <summary>Read-only §144.9 retrieval. No model calls, biography writes or action leases.</summary>
public sealed class AgentWorldKnowledge
{
    public const int ContextLimit = 4800;
    private readonly Func<string, int, CancellationToken, Task<JsonElement>> _read;
    private readonly Dictionary<string, string> _cache = new();
    private readonly Dictionary<string, string> _titles = new();
    private DateTime _expires;
    private DateTime _retryAfter;

    private static string Guidance => AgentPromptFiles.Read("world-guidance.md");

    public AgentWorldKnowledge(Func<string, int, CancellationToken, Task<JsonElement>> read)
        => _read = read;

    public async Task<string> BuildAsync(string query, string state, CancellationToken token)
    {
        var output = new StringBuilder(Guidance);
        if (DateTime.UtcNow < _retryAfter)
            return output.Append(AgentPromptFiles.Text("AgentWorldKnowledge.01")).ToString();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            if (DateTime.UtcNow >= _expires)
            {
                _cache.Clear();
                _titles.Clear();
                var index = await _read("", 0, deadline.Token);
                foreach (Match row in Regex.Matches(index.GetProperty("index").GetString() ?? "",
                             @"\(Spec/([0-9]{1,4}[A-Z]?)\.md\)\s*\|\s*([^\r\n|]+)"))
                    _titles[row.Groups[1].Value] = row.Groups[2].Value;
                if (_titles.Count == 0) throw new InvalidDataException("Empty spec index.");
                _expires = DateTime.UtcNow.AddMinutes(10);
            }

            var search = ExpandQuery(query.Length > 0 ? query : StateQuery(state));
            var sections = SelectSections(query, search);
            foreach (var section in sections)
            {
                var text = await ReadSectionAsync(section, deadline.Token);
                var paragraphs = CurrentParagraphs(text)
                    .Select((text, order) => new { text, order, score = Score(text, search) })
                    .OrderByDescending(p => p.score).ThenBy(p => p.order).Take(3)
                    .OrderBy(p => p.order);
                output.Append(AgentPromptFiles.Text("AgentWorldKnowledge.02")).Append(section).Append(":\n");
                foreach (var paragraph in paragraphs)
                {
                    // An explicit marker must survive even when a single paragraph is enormous.
                    var excerpt = paragraph.text.Length <= 550 ? paragraph.text : paragraph.text[..520] + AgentPromptFiles.Text("AgentWorldKnowledge.03");
                    if (output.Length + excerpt.Length + 2 > ContextLimit) break;
                    output.AppendLine(excerpt).AppendLine();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or
                                      InvalidDataException or JsonException or KeyNotFoundException or
                                      OperationCanceledException)
        {
            _retryAfter = DateTime.UtcNow.AddMinutes(1);
            // Never log provider content, player text or credentials.
            Console.Error.WriteLine($"[knowledge] unavailable: {ex.GetType().Name}");
            var unavailable = AgentPromptFiles.Text("AgentWorldKnowledge.04");
            if (output.Length + unavailable.Length <= ContextLimit) output.Append(unavailable);
        }
        return output.ToString();
    }

    private IEnumerable<string> SelectSections(string query, string search)
    {
        var selected = new List<string>();
        foreach (Match reference in Regex.Matches(query, @"§([0-9]{1,4}[A-Z]?)"))
            if (_titles.ContainsKey(reference.Groups[1].Value)) selected.Add(reference.Groups[1].Value);
        if (search.Contains("drinking-water", StringComparison.Ordinal))
        {
            selected.Add("134");
            selected.Add("55");
        }
        if (search.Contains("medical-treatment", StringComparison.Ordinal))
        {
            selected.Add("68");
            selected.Add("44");
        }
        if (search.Contains("sleep", StringComparison.Ordinal)) selected.Add("54");
        if (search.Contains("stamina", StringComparison.Ordinal)) selected.Add("137");
        selected.AddRange(_titles.OrderByDescending(p => Score(p.Value, search))
            .ThenBy(p => p.Key, StringComparer.Ordinal).Where(p => Score(p.Value, search) > 0).Select(p => p.Key));
        if (selected.Count == 0) selected.Add("121");
        return selected.Where(_titles.ContainsKey).Distinct().Take(2);
    }

    private async Task<string> ReadSectionAsync(string section, CancellationToken token)
    {
        if (_cache.TryGetValue(section, out var cached)) return cached;
        var text = new StringBuilder();
        var offset = 0;
        for (var page = 0; page < 4; page++)
        {
            var chunk = await _read(section, offset, token);
            var part = chunk.GetProperty("text").GetString() ?? "";
            if (part.Length > 24_000 || chunk.GetProperty("offset").GetInt32() != offset)
                throw new InvalidDataException("Invalid spec page.");
            text.Append(part);
            if (!chunk.GetProperty("truncated").GetBoolean())
            {
                if (_cache.Count >= 16) _cache.Clear();
                return _cache[section] = text.ToString();
            }
            var next = chunk.GetProperty("nextOffset").GetInt32();
            if (next != offset + part.Length || next <= offset)
                throw new InvalidDataException("Invalid spec continuation.");
            offset = next;
        }
        // Do not cache or silently treat a prefix as the complete chapter.
        throw new InvalidDataException("Spec exceeds retrieval page budget.");
    }

    private static string ExpandQuery(string query)
    {
        var text = query.ToLowerInvariant();
        if (Regex.IsMatch(text, AgentPromptFiles.Text("AgentWorldKnowledge.05"))) text += " sleep energy recovery";
        if (Regex.IsMatch(text, AgentPromptFiles.Text("AgentWorldKnowledge.06"))) text += " stamina rest sleep energy";
        if (Regex.IsMatch(text, AgentPromptFiles.Text("AgentWorldKnowledge.07"))) text += " fire fuel";
        if (Regex.IsMatch(text, AgentPromptFiles.Text("AgentWorldKnowledge.08"))) text += " drinking-water water thirst coconut";
        if (Regex.IsMatch(text, AgentPromptFiles.Text("AgentWorldKnowledge.09"))) text += " food hunger";
        if (Regex.IsMatch(text, AgentPromptFiles.Text("AgentWorldKnowledge.10")))
            text += " medical-treatment wound blood bandage herb TreatSelf CraftBandage";
        return text;
    }

    private static string StateQuery(string state)
    {
        // Field names are not needs: every snapshot contains energy and stamina.
        // Never let their mere presence pin the sleep chapters on every heartbeat.
        var query = new StringBuilder();
        var thirst = Regex.Match(state, @"thirst=([0-9]+(?:\.[0-9]+)?)");
        if (thirst.Success && double.TryParse(thirst.Groups[1].Value,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out var thirstValue) && thirstValue >= 0.75) query.Append(AgentPromptFiles.Text("AgentWorldKnowledge.11"));
        foreach (var (name, limit, topic) in new[]
                 { ("health", 0.9, AgentPromptFiles.Text("AgentWorldKnowledge.12")), ("blood", 0.8, AgentPromptFiles.Text("AgentWorldKnowledge.13")),
                   ("energy", 0.3, AgentPromptFiles.Text("AgentWorldKnowledge.14")), ("stamina", 0.25, AgentPromptFiles.Text("AgentWorldKnowledge.15")) })
        {
            var value = Regex.Match(state, name + @"=([0-9]+(?:\.[0-9]+)?)");
            if (value.Success && double.TryParse(value.Groups[1].Value,
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                    out var number) && number < limit) query.Append(' ').Append(topic);
        }
        return query.ToString();
    }

    private static int Score(string text, string query)
        => Regex.Matches(query, @"[\p{L}]{4,}").Select(m => m.Value).Distinct()
            .Count(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> CurrentParagraphs(string text)
    {
        var headings = new SortedDictionary<int, string>();
        foreach (var paragraph in Regex.Split(text, @"\r?\n\s*\r?\n"))
        {
            var heading = Regex.Match(paragraph, @"^(#{1,6})\s+([^\r\n]+)$");
            if (heading.Success)
            {
                var level = heading.Groups[1].Length;
                foreach (var key in headings.Keys.Where(key => key >= level).ToArray()) headings.Remove(key);
                headings[level] = heading.Groups[2].Value;
                continue;
            }
            if (headings.Values.Any(h => Regex.IsMatch(h, AgentPromptFiles.Text("AgentWorldKnowledge.16"), RegexOptions.IgnoreCase))) continue;
            if (!string.IsNullOrWhiteSpace(paragraph))
                yield return (headings.Count > 0 ? headings.Values.Last() + "\n" : "") + paragraph;
        }
    }
}

/// <summary>Production composition seam; exactly one underlying decision per turn.</summary>
public sealed class KnowledgeAwareAgentProviders : IAgentProviders
{
    private readonly IAgentProviders _inner;
    private McpClient _mcp;
    private readonly AgentWorldKnowledge _knowledge;

    public KnowledgeAwareAgentProviders(IAgentProviders inner, McpClient mcp)
    {
        _inner = inner;
        _mcp = mcp;
        _knowledge = new AgentWorldKnowledge(ReadSpecAsync);
    }

    private async Task<JsonElement> ReadSpecAsync(string section, int offset, CancellationToken token)
    {
        try { return await _mcp.CallToolAsync("read_spec", new { section, offset }, token); }
        catch (McpSessionExpiredException)
        {
            token.ThrowIfCancellationRequested();
            var expired = _mcp;
            _mcp = expired.CreateFresh();
            expired.Dispose();
            // Only this read-only query is safe to retry. A fresh handshake
            // failure escapes to the existing knowledge-unavailable backoff.
            return await _mcp.CallToolAsync("read_spec", new { section, offset }, token);
        }
    }

    public async Task<CompanionDecision> DecideAsync(string trigger, string stateJson,
        string memoryContext, string transcript, IReadOnlyList<string> recentConversation,
        CancellationToken cancellationToken)
    {
        var knowledge = await _knowledge.BuildAsync(transcript, stateJson, cancellationToken);
        return await _inner.DecideAsync(trigger, stateJson, memoryContext + "\n\n" + knowledge,
            transcript, recentConversation, cancellationToken);
    }

    public Task<VoiceArtifact> SynthesizeAsync(string text, CancellationToken cancellationToken)
        => _inner.SynthesizeAsync(text, cancellationToken);

    public void Dispose() { _inner.Dispose(); _mcp.Dispose(); }
}
