using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HexLive.AgentHost;

/// <summary>§165: append-only evidence. Short-term state is never the archival source of truth.</summary>
public sealed record AgentMemoryRecord
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Text { get; init; } = "";
    public string Source { get; init; } = "";
    public string Episode { get; init; } = "";
    public string Speaker { get; init; } = "";
    public string Group { get; init; } = "";
    public string Status { get; init; } = "";
    public DateTimeOffset? OccurredUtc { get; init; }
    public DateTimeOffset ReceivedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string TimeZone { get; init; } = TimeZoneInfo.Local.Id;
    public long? Tick { get; init; }
    public int DayLengthTicks { get; init; }
    public bool Incomplete { get; init; }
}

public sealed class AgentMemoryArchive
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate;
    private readonly string _root;
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lengths = new();
    public string Root => _root;
    public AgentMemoryArchive(string root)
    {
        _root = Path.GetFullPath(root);
        _gate = Gates.GetOrAdd(_root, _ => new());
    }
    public static string Id(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public string SafePath(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("ArchivePathOutsideWorkspace");
        for (var current = path; current != _root; current = Path.GetDirectoryName(current)!)
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("LinkedArchiveNotAllowed");
        if (Directory.Exists(_root) && (File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("LinkedWorkspaceNotAllowed");
        return path;
    }
    internal IEnumerable<string> Files(string relative, string extension)
    {
        var root = SafePath(relative);
        if (!Directory.Exists(root)) yield break;
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            SafePath(Path.GetRelativePath(_root, path));
            if (Directory.Exists(path))
            {
                foreach (var child in Files(Path.GetRelativePath(_root, path), extension)) yield return child;
            }
            else if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) yield return path;
        }
    }
    internal static void Protect(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | (Directory.Exists(path) ? UnixFileMode.UserExecute : 0));
    }
    internal void Atomic(string relative, string content)
    {
        var path = SafePath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Protect(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content, Utf8); Protect(temp);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static IReadOnlyList<AgentMemoryRecord> ReadFile(string path)
    {
        // A concurrent append may expose an incomplete last line. Never accept it as committed.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var buffer = new MemoryStream(); file.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var end = Array.LastIndexOf(bytes, (byte)'\n');
        if (end < 0) return [];
        return Utf8.GetString(bytes, 0, end).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<AgentMemoryRecord>(line, Json)
                ?? throw new InvalidDataException("InvalidArchiveRecord")).ToArray();
    }
    public bool Append(AgentMemoryRecord record) => AppendMany([record]) > 0;
    public int AppendMany(IEnumerable<AgentMemoryRecord> records)
    {
        lock (_gate)
        {
            var root = SafePath("memory/archive"); Directory.CreateDirectory(root); Protect(root);
            using var lease = new FileStream(SafePath("memory/archive/.write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            foreach (var file in Files("memory/archive", ".jsonl"))
            {
                var length = new FileInfo(file).Length;
                if (_lengths.GetValueOrDefault(file, -1) == length) continue;
                foreach (var row in ReadFile(file)) _ids.Add(row.Id);
                _lengths[file] = length;
            }
            var added = 0;
            foreach (var group in records.GroupBy(r => r.ReceivedUtc.UtcDateTime.ToString("yyyy-MM-dd")))
            {
                var rows = group.Where(r => !_ids.Contains(r.Id)).DistinctBy(r => r.Id).ToArray();
                if (rows.Length == 0) continue;
                if (rows.Any(r => r.Id.Length == 0 || r.Text.Length > 1_000_000)) throw new InvalidDataException("InvalidArchiveRecord");
                var path = SafePath("memory/archive/" + group.Key + ".jsonl");
                using var output = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                Protect(path);
                // Preserve interrupted bytes before repairing only the incomplete final record.
                if (output.Length > 0)
                {
                    output.Position = output.Length - 1;
                    if (output.ReadByte() != '\n')
                    {
                        var end = output.Length;
                        while (end > 0) { output.Position = --end; if (output.ReadByte() == '\n') { end++; break; } }
                        output.Position = end;
                        var tail = new byte[checked((int)(output.Length - end))]; output.ReadExactly(tail);
                        var backup = path + "." + Guid.NewGuid().ToString("N") + ".partial";
                        File.WriteAllBytes(backup, tail); Protect(backup); output.SetLength(end);
                    }
                }
                output.Position = output.Length;
                foreach (var row in rows) output.Write(Utf8.GetBytes(JsonSerializer.Serialize(row, Json) + "\n"));
                output.Flush(true);
                foreach (var row in rows) _ids.Add(row.Id);
                _lengths[path] = output.Length; added += rows.Length;
            }
            return added;
        }
    }

    public void Migrate(MashaArchive state)
    {
        lock (_gate)
        {
            var marker = SafePath(".state/archive-migration-v1.json");
            if (File.Exists(marker)) return;
            Atomic(".state/backups/before-archive-v1.json", JsonSerializer.Serialize(state, Json));
            var rows = new List<AgentMemoryRecord>();
            void Add(string key, string text, string kind, string episode, string speaker, DateTimeOffset? time, long? tick)
            {
                rows.Add(new() { Id = Id("migration:" + key), Text = text, Kind = kind, Episode = episode,
                    Speaker = speaker, OccurredUtc = time, Tick = tick, Source = "legacy-state", Group = episode, Incomplete = true });
            }
            foreach (var m in state.CoreMemories)
                Add("core:" + m.Key, m.Value, "note", "", m.SpeakerKey.Length > 0 ? m.SpeakerKey : "legacy", m.UpdatedAtUtc, m.UpdatedAtTick);
            foreach (var w in state.Worlds)
            {
                Add("episode:" + w.Id, string.Format(AgentPromptFiles.Text("HistoryEpisodeStart"), w.Label), "event", w.Id, "", w.FirstSeenUtc, w.FirstTick);
                foreach (var m in w.Memories) Add(w.Id + m.Key, m.Value, "note", w.Id, m.SpeakerKey.Length > 0 ? m.SpeakerKey : "legacy", m.UpdatedAtUtc, m.UpdatedAtTick);
                foreach (var j in w.Journal) Add(w.Id + j.Tick + j.Text, j.Text, "diary", w.Id, j.SpeakerKey.Length > 0 ? j.SpeakerKey : "legacy", j.CreatedAtUtc, j.Tick);
            }
            foreach (var (key, speaker) in state.Speakers)
            {
                foreach (var m in speaker.Facts) Add(key + m.Key, m.Value, "note", "", key, m.UpdatedAtUtc, m.UpdatedAtTick);
                for (var i = 0; i < speaker.RecentConversation.Count; i++)
                    Add(key + ":recent:" + i, speaker.RecentConversation[i], "conversation", "", key, null, null);
            }
            AppendMany(rows);
            Atomic(".state/archive-migration-v1.json", JsonSerializer.Serialize(new { version = 1, records = rows.Count, completedUtc = DateTimeOffset.UtcNow }, Json));
        }
    }
}
