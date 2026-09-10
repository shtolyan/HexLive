using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HexLive.AgentHost;

public sealed record MemoryFilter(string? Kind = null, string? Episode = null, string? Participant = null,
    DateTimeOffset? From = null, DateTimeOffset? Until = null, long? GameDay = null);
public sealed record MemoryHit(AgentMemoryRecord Record, double Score);
public sealed record MemorySearchPage(MemoryHit[] Hits, int? NextOffset, int Total);
public sealed record MemoryReadResult(string Text, string[] SourceIds, int? NextOffset);

/// <summary>§165: disposable per-file index segments; the archive and authored documents remain authoritative.</summary>
public sealed class AgentMemorySearch
{
    private readonly AgentMemoryArchive _archive;
    private readonly object _gate = new();
    private readonly Dictionary<string, Segment> _segments = new();
    private readonly Dictionary<string, AgentMemoryRecord> _records = new();
    private readonly Dictionary<string, Dictionary<string, int>> _postings = new();
    private readonly Dictionary<string, int> _sizes = new();
    private readonly Dictionary<string, string[]> _tokens = new();
    private readonly Dictionary<string, HashSet<string>> _prefixes = new();
    private double _average = 1;
    private sealed record Segment(long Length, long Modified, AgentMemoryRecord[] Rows, Dictionary<string, string[]>? Tokens = null);
    public AgentMemorySearch(string root) => _archive = new(root);

    public int Refresh()
    {
        lock (_gate)
        {
            var files = _archive.Files("memory/archive", ".jsonl").Concat(_archive.Files("memory", ".md"))
                .Concat(_archive.Files("imports", ".md"))
                .Concat(new[] { "SOUL.md", "USER.md", "MEMORY.md" }.Select(_archive.SafePath).Where(File.Exists))
                .Where(p => !Path.GetFileName(p).Equals("thoughts.md", StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
            var changed = false;
            foreach (var absent in _segments.Keys.Except(files).ToArray()) {
                foreach (var row in _segments[absent].Rows) Remove(row.Id);
                _segments.Remove(absent); changed = true;
            }
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                if (_segments.TryGetValue(file, out var old) && old.Length == info.Length && old.Modified == info.LastWriteTimeUtc.Ticks) continue;
                var relative = Path.GetRelativePath(_archive.Root, file).Replace('\\', '/');
                var cache = ".state/search/" + AgentMemoryArchive.Id(relative) + ".json";
                Segment? segment = null;
                try
                {
                    var path = _archive.SafePath(cache);
                    if (File.Exists(path)) segment = JsonSerializer.Deserialize<Segment>(File.ReadAllText(path), AgentMemoryArchive.Json);
                }
                catch (Exception ex) when (ex is JsonException or IOException) { }
                if (segment == null || segment.Tokens == null || segment.Length != info.Length || segment.Modified != info.LastWriteTimeUtc.Ticks)
                {
                    var rows = file.EndsWith(".jsonl", StringComparison.Ordinal)
                        ? AgentMemoryArchive.ReadFile(file).ToArray() : DocumentRows(file, relative);
                    segment = new(info.Length, info.LastWriteTimeUtc.Ticks, rows, rows.ToDictionary(r => r.Id, r => MemoryWords.Tokens(r.Text + " " + r.Source + " " + r.Episode)));
                    _archive.Atomic(cache, JsonSerializer.Serialize(segment, AgentMemoryArchive.Json));
                }
                if (old != null)
                {
                    var retained = segment.Rows.Select(r => r.Id).ToHashSet();
                    foreach (var row in old.Rows.Where(r => !retained.Contains(r.Id))) Remove(row.Id);
                }
                foreach (var row in segment.Rows)
                {
                    if (!_records.TryAdd(row.Id, row)) continue;
                    var tokens = segment.Tokens![row.Id];
                    _tokens[row.Id] = tokens; _sizes[row.Id] = tokens.Length;
                    foreach (var group in tokens.GroupBy(s => s))
                    {
                        if (!_postings.TryGetValue(group.Key, out var posting)) _postings[group.Key] = posting = new();
                        posting[row.Id] = group.Count();
                        var prefix = group.Key[..Math.Min(3, group.Key.Length)];
                        if (!_prefixes.TryGetValue(prefix, out var words)) _prefixes[prefix] = words = new();
                        words.Add(group.Key);
                    }
                }
                _segments[file] = segment; changed = true;
            }
            if (changed)
            {
                _average = Math.Max(1, _sizes.Count == 0 ? 1 : _sizes.Values.Average());
                _archive.Atomic(".state/search/manifest.json", JsonSerializer.Serialize(new { version = 1, count = _records.Count,
                    files = files.Select(p => Path.GetRelativePath(_archive.Root, p)), updatedUtc = DateTimeOffset.UtcNow }, AgentMemoryArchive.Json));
            }
            return _records.Count;
        }
    }
    private void Remove(string id)
    {
        if (!_tokens.Remove(id, out var tokens)) return;
        _records.Remove(id); _sizes.Remove(id);
        foreach (var word in tokens.Distinct())
            if (_postings.TryGetValue(word, out var posting))
            {
                posting.Remove(id);
                if (posting.Count == 0) { _postings.Remove(word); _prefixes[word[..Math.Min(3, word.Length)]].Remove(word); }
            }
    }
    private IEnumerable<string> Expand(string term)
    {
        yield return term;
        // Bounded lexical fallback for irregular inflections (досок/доски), never a full vocabulary scan.
        if (term.Length < 4 || !_prefixes.TryGetValue(term[..3], out var words)) yield break;
        foreach (var word in words.Where(w => w != term && OneEdit(term, w)).Order(StringComparer.Ordinal).Take(16)) yield return word;
    }
    private static bool OneEdit(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1) return false;
        var i = 0; var j = 0; var edits = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (a.Length <= b.Length) j++;
            if (a.Length >= b.Length) i++;
        }
        return edits + (a.Length - i) + (b.Length - j) <= 1;
    }
    private static AgentMemoryRecord[] DocumentRows(string file, string relative)
    {
        var lines = File.ReadAllLines(file, AgentMemoryArchive.Utf8);
        // Generated aggregate views lack speaker ownership. Their structured originals are archived.
        // Keep only authored text outside managed sections; otherwise another speaker's note could leak.
        if (!relative.Contains("imports/", StringComparison.Ordinal))
        {
            if (relative.StartsWith("memory/worlds/", StringComparison.Ordinal) || relative.StartsWith("memory/daily/", StringComparison.Ordinal)) return [];
            var managed = false;
            lines = lines.Select(line => {
                if (line.Contains(MemoryDocumentEdits.Start, StringComparison.Ordinal)) { managed = true; return ""; }
                if (line.Contains(MemoryDocumentEdits.End, StringComparison.Ordinal)) { managed = false; return ""; }
                return managed ? "" : line;
            }).ToArray();
        }
        var imported = relative.Contains("imports/", StringComparison.Ordinal);
        var date = Regex.Match(relative, @"\d{4}-\d{2}-\d{2}").Value;
        DateTimeOffset? when = DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? new DateTimeOffset(DateTime.SpecifyKind(day, DateTimeKind.Unspecified), TimeSpan.Zero) : null;
        var result = new List<AgentMemoryRecord>();
        for (var start = 0; start < lines.Length; start += 6)
        {
            var text = string.Join('\n', lines.Skip(start).Take(6).Where(line => !line.Contains("[thought]", StringComparison.OrdinalIgnoreCase)));
            if (string.IsNullOrWhiteSpace(text)) continue;
            result.Add(new() { Id = AgentMemoryArchive.Id(relative + ":" + start + ":" + text),
                Text = text, Kind = imported ? "import" : "note", Source = relative + ":" + (start + 1),
                Group = relative, Speaker = "legacy", Episode = imported && relative.Contains("molly", StringComparison.OrdinalIgnoreCase) ? "molly-room-iphone" : relative,
                OccurredUtc = when, TimeZone = "unknown", Incomplete = imported || when == null });
        }
        return result.ToArray();
    }
    public MemorySearchPage Search(string query, MemoryFilter? filter = null, string order = "relevance", int offset = 0,
        Func<AgentMemoryRecord, bool>? allowed = null)
    {
        lock (_gate)
        {
            filter ??= new(); offset = Math.Max(0, offset);
            var scores = new Dictionary<string, double>();
            var terms = MemoryWords.Tokens(query).Distinct().ToArray();
            foreach (var term in terms.SelectMany(Expand).Distinct())
            {
                if (!_postings.TryGetValue(term, out var posting)) continue;
                var idf = Math.Log(1 + (_records.Count - posting.Count + .5) / (posting.Count + .5));
                foreach (var (id, count) in posting)
                {
                    var bm25 = idf * count * 2.2 / (count + 1.2 * (.25 + .75 * _sizes[id] / _average));
                    scores[id] = scores.GetValueOrDefault(id) + bm25;
                }
            }
            var candidates = terms.Length == 0 ? _records.Values : scores.Keys.Select(id => _records[id]);
            bool Match(AgentMemoryRecord r) => (allowed == null || allowed(r)) &&
                (string.IsNullOrEmpty(filter.Kind) || r.Kind == filter.Kind || filter.Kind == "conversation" && r.Kind is "player" or "speech" or "conversation") &&
                (string.IsNullOrEmpty(filter.Episode) || r.Episode.Contains(filter.Episode, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(filter.Participant) || MemoryWords.Normalize(r.Text).Contains(MemoryWords.Normalize(filter.Participant))) &&
                (filter.From == null || r.OccurredUtc >= filter.From) && (filter.Until == null || r.OccurredUtc < filter.Until) &&
                (filter.GameDay == null || r.DayLengthTicks > 0 && r.Tick / r.DayLengthTicks == filter.GameDay);
            var hits = candidates.Where(Match).Select(r => new MemoryHit(r, scores.GetValueOrDefault(r.Id) +
                (query.Length > 2 && MemoryWords.Normalize(r.Text).Contains(MemoryWords.Normalize(query)) ? 4 : 0)));
            hits = order switch { "oldest" => hits.OrderBy(h => !string.IsNullOrEmpty(filter.Episode) ? h.Record.Tick ?? long.MaxValue : 0).ThenBy(h => h.Record.OccurredUtc ?? DateTimeOffset.MaxValue).ThenBy(h => h.Record.Tick),
                "newest" => hits.OrderByDescending(h => !string.IsNullOrEmpty(filter.Episode) ? h.Record.Tick ?? long.MinValue : 0).ThenByDescending(h => h.Record.OccurredUtc).ThenByDescending(h => h.Record.Tick),
                _ => hits.OrderByDescending(h => h.Score).ThenByDescending(h => h.Record.OccurredUtc) };
            var page = hits.ToArray();
            return new(page.Skip(offset).Take(8).ToArray(), offset + 8 < page.Length ? offset + 8 : null, page.Length);
        }
    }
    public MemoryReadResult Read(string id, int offset = 0, Func<AgentMemoryRecord, bool>? allowed = null)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var row) || allowed != null && !allowed(row))
                return new("SourceUnavailable", [], null);
            var neighbors = _records.Values.Where(r => r.Group.Length > 0 && r.Group == row.Group && r.Episode == row.Episode &&
                r.Speaker == row.Speaker && (allowed == null || allowed(r))).OrderBy(r => r.OccurredUtc ?? r.ReceivedUtc).ToArray();
            var index = Array.FindIndex(neighbors, r => r.Id == id);
            offset = Math.Clamp(offset, 0, row.Text.Length);
            var length = Math.Min(6000, row.Text.Length - offset);
            var primary = Describe(row with { Text = row.Text.Substring(offset, length), Incomplete = row.Incomplete || offset > 0 || length < row.Text.Length });
            while (primary.Length > 7900 && length > 0) {
                length /= 2; primary = Describe(row with { Text = row.Text.Substring(offset, length), Incomplete = true });
            }
            var texts = new List<string> { primary }; var ids = new List<string> { row.Id }; var size = primary.Length;
            if (index >= 0 && offset == 0)
                foreach (var neighbor in neighbors.Skip(Math.Max(0, index - 2)).Take(5).Where(r => r.Id != id))
                {
                    var description = Describe(neighbor);
                    if (size + description.Length + 2 > 8000) continue;
                    texts.Add(description); ids.Add(neighbor.Id); size += description.Length + 2;
                }
            return new(string.Join("\n\n", texts), ids.ToArray(), offset + length < row.Text.Length ? offset + length : null);
        }
    }
    public static string Describe(AgentMemoryRecord row) => JsonSerializer.Serialize(new { sourceId = row.Id,
        row.Kind, row.Source, row.Episode, row.Status, row.OccurredUtc, row.Tick, row.DayLengthTicks,
        row.TimeZone, row.Incomplete, row.Text }, AgentMemoryArchive.Json);
}
