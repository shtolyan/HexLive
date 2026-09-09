using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HexLive.AgentHost;

/// <summary>§163.3 Durable author edits. Only the runtime writes its archive; the editor writes this journal.</summary>
public sealed record MemoryDocumentEdit(string Id, string Document, string Before, string After);

public static class MemoryDocumentEdits
{
    public const string Start = "<!-- agent-studio:notes:start -->";
    public const string End = "<!-- agent-studio:notes:end -->";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static bool Supports(string path) => path is "USER.md" or "MEMORY.md" or "SOUL.md";
    public static string ForEditor(string document, string text) => document is "USER.md" or "MEMORY.md"
        ? text.Contains(Start, StringComparison.Ordinal) ? WithoutDuplicate(text) : Start + "\n" + text.Trim() + "\n" + End + "\n"
        : text;
    public static string NormalizeEdit(string before, string after)
    {
        if (WithoutDuplicate(before) == before) return after;
        var prefix = before[..before.IndexOf(Start, StringComparison.Ordinal)];
        return after.StartsWith(prefix, StringComparison.Ordinal) ? after[prefix.Length..] : after;
    }
    public static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static string WithoutDuplicate(string text)
    {
        var from = text.IndexOf(Start, StringComparison.Ordinal);
        var to = text.IndexOf(End, StringComparison.Ordinal);
        if (from < 0 || to < from) return text;
        var prefix = text[..from];
        var body = text[(from + Start.Length)..to];
        // Only a complete, exact duplicate is disposable. Never infer similarity of authored paragraphs.
        return Normalize(prefix) == Normalize(body) ? text[from..] : text;
    }

    public static void Validate(string document, string text)
    {
        if (!Supports(document)) return;
        var starts = Regex.Matches(text, Regex.Escape(Start)).Count;
        var ends = Regex.Matches(text, Regex.Escape(End)).Count;
        if (starts != ends || starts > 1 || starts == 1 && text.IndexOf(Start, StringComparison.Ordinal) > text.IndexOf(End, StringComparison.Ordinal))
            throw new InvalidDataException("InvalidMemorySectionMarkers");
        if (document == "USER.md") _ = Metrics(text);
        var facts = Facts(document == "SOUL.md" || text.Contains(Start, StringComparison.Ordinal) ? Section(text) : text);
        if (facts.Any(x => x.Length > 400) || facts.Count > MashaMemoryStore.MaxCoreMemories)
            throw new InvalidDataException("MemoryEditTooLarge");
    }

    public static string? Prepare(string root, string document, string before, string after)
    {
        if (!Supports(document) || before == after) return null;
        Validate(document, after);
        var directory = DirectoryFor(root);
        Directory.CreateDirectory(directory);
        if (Directory.EnumerateFiles(directory, "*.json").Take(128).Count() >= 128)
            throw new IOException("PendingMemoryEditsLimit");
        var id = DateTime.UtcNow.Ticks.ToString("D19", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(directory, id + ".json");
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new MemoryDocumentEdit(id, document, before, after), Json));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return path;
    }

    public static bool HasPending(string root) => Directory.Exists(DirectoryFor(root)) &&
        Directory.EnumerateFiles(DirectoryFor(root), "*.json").Any();

    public static bool HasPendingVersion(string root, string document, string content)
    {
        var directory = DirectoryFor(root);
        return Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.json").Any(path =>
        {
            RejectLink(path);
            var edit = JsonSerializer.Deserialize<MemoryDocumentEdit>(File.ReadAllText(path), Json);
            return edit?.Document == document && edit.After == content;
        });
    }

    public static IReadOnlyList<string> ApplyPending(string root, MashaArchive archive)
    {
        var directory = DirectoryFor(root);
        if (!Directory.Exists(directory)) return [];
        var done = new List<string>();
        foreach (var path in Directory.GetFiles(directory, "*.json").OrderBy(x => x, StringComparer.Ordinal))
        {
            RejectLink(path);
            if (new FileInfo(path).Length > 12 * 1024 * 1024) throw new InvalidDataException("InvalidMemoryEdit");
            var edit = JsonSerializer.Deserialize<MemoryDocumentEdit>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("InvalidMemoryEdit");
            if (!Supports(edit.Document) || Path.GetFileName(path) != edit.Id + ".json")
                throw new InvalidDataException("InvalidMemoryEdit");
            Validate(edit.Document, edit.After);
            if (!archive.AppliedDocumentEditIds.Contains(edit.Id))
            {
                Apply(archive, edit);
                archive.AppliedDocumentEditIds.Add(edit.Id);
            }
            done.Add(path);
        }
        // At most 128 pending operations; retain a larger crash-replay window.
        if (archive.AppliedDocumentEditIds.Count > 512)
            archive.AppliedDocumentEditIds.RemoveRange(0, archive.AppliedDocumentEditIds.Count - 512);
        return done;
    }

    public static void Complete(IEnumerable<string> paths)
    {
        foreach (var path in paths) File.Delete(path);
    }

    private static void Apply(MashaArchive archive, MemoryDocumentEdit edit)
    {
        var before = Facts(edit.Before.Contains(Start, StringComparison.Ordinal) ? Section(edit.Before) : edit.Before);
        var after = Facts(edit.After.Contains(Start, StringComparison.Ordinal) ? Section(edit.After) : edit.After);
        // SOUL's author-written personality is already read verbatim; only managed self-notes map to facts.
        if (edit.Document == "SOUL.md")
        {
            before = Facts(Section(edit.Before));
            after = Facts(Section(edit.After));
        }
        bool Relevant(PortableMemory m) => edit.Document switch
        {
            "USER.md" => m.Source is "molly-user" or "model-user",
            "SOUL.md" => m.Source == "model-self",
            _ => m.Source is not ("molly-user" or "model-user" or "model-self")
        };
        var removed = before.Except(after, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var added = after.Except(before, StringComparer.Ordinal).ToArray();
        var primary = archive.PrimarySpeakerKey != null && archive.Speakers.TryGetValue(archive.PrimarySpeakerKey, out var speaker) ? speaker : null;
        var target = edit.Document == "USER.md" && primary != null ? primary.Facts : archive.CoreMemories;
        var scope = edit.Document == "USER.md" ? archive.PrimarySpeakerKey ?? "legacy-user" : edit.Document;
        string Suppression(string value) => scope + "\n" + value;
        foreach (var value in removed) archive.SuppressedMemoryValues.Add(Suppression(value));
        foreach (var value in added) archive.SuppressedMemoryValues.Remove(Suppression(value));
        // Mirror deletions into the retained legacy facts, not into other speakers.
        archive.CoreMemories.RemoveAll(m => Relevant(m) && removed.Contains(DisplayValue(m.Value, edit.Document)));
        if (edit.Document == "USER.md" && primary != null)
            primary.Facts.RemoveAll(m => removed.Contains(DisplayValue(m.Value, edit.Document)));
        foreach (var value in added)
        {
            if (target.Any(m => Relevant(m) && Normalize(m.Value) == value)) continue;
            target.Add(new PortableMemory { Key = "author:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24],
                Value = value, Importance = 1, UpdatedAtUtc = DateTimeOffset.UtcNow,
                Source = edit.Document switch { "USER.md" => "model-user", "SOUL.md" => "model-self", _ => "model-core" },
                SpeakerKey = edit.Document == "USER.md" ? archive.PrimarySpeakerKey ?? "" : "" });
        }
        if (edit.Document == "USER.md")
        {
            var oldMetrics = Metrics(edit.Before); var newMetrics = Metrics(edit.After);
            if (newMetrics != null && newMetrics != oldMetrics)
            {
                void Set(PortablePlayerBond bond)
                { bond.Familiarity = newMetrics.Value.Familiarity; bond.Trust = newMetrics.Value.Trust; bond.Affinity = newMetrics.Value.Affinity; }
                Set(archive.PlayerBond);
                if (primary != null) { Set(primary.Bond); primary.RelationshipState = null; }
            }
        }
    }

    public static bool IsSuppressed(MashaArchive archive, string source, string speaker, string value)
    {
        var scope = source == "model-user" || source == "molly-user" ? (speaker.Length > 0 ? speaker : "legacy-user") :
            source == "model-self" ? "SOUL.md" : "MEMORY.md";
        return archive.SuppressedMemoryValues.Contains(scope + "\n" + DisplayValue(value, scope == "legacy-user" || source is "model-user" or "molly-user" ? "USER.md" : "MEMORY.md"));
    }

    public static bool IsSuppressedInContext(MashaArchive archive, string speaker, string text)
    {
        var normalized = Normalize(text);
        return archive.SuppressedMemoryValues.Any(item =>
        {
            var split = item.IndexOf('\n');
            if (split < 0) return false;
            var scope = item[..split]; var value = item[(split + 1)..];
            return (scope is "MEMORY.md" or "SOUL.md" || scope == speaker ||
                scope == "legacy-user" && (speaker.Length == 0 || speaker == archive.PrimarySpeakerKey)) &&
                value.Length > 0 && normalized.Contains(value, StringComparison.Ordinal);
        });
    }

    private static string DisplayValue(string value, string document)
    {
        value = Normalize(value); var limit = document == "USER.md" ? 300 : 400;
        return value.Length > limit ? value[..limit].TrimEnd() + "…" : value;
    }

    private static string Section(string text)
    {
        var start = text.IndexOf(Start, StringComparison.Ordinal); var end = text.IndexOf(End, StringComparison.Ordinal);
        return start >= 0 && end > start ? text[(start + Start.Length)..end] : "";
    }

    private static List<string> Facts(string text) => WithoutDuplicate(text).Replace("\r\n", "\n").Split('\n')
        .Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith('#') && !x.StartsWith("<!--") && !x.Contains("Знакомство:"))
        .Select(x => Normalize(x.StartsWith("- ") ? x[2..] : x)).Distinct(StringComparer.Ordinal).ToList();

    private static (float Familiarity, float Trust, float Affinity)? Metrics(string text)
    {
        var lines = WithoutDuplicate(text).Split('\n').Where(x => x.Contains("Знакомство:")).Distinct().ToArray();
        if (lines.Length == 0) return null;
        if (lines.Length != 1) throw new InvalidDataException("InvalidRelationshipMetrics");
        var match = Regex.Match(lines[0].Trim(), @"^-?\s*Знакомство:\s*([\d.,+-]+)%;\s*доверие:\s*([\d.,+-]+)%;\s*(?:привязанность|симпатия):\s*([\d.,+-]+)%\.?$");
        if (!match.Success) throw new InvalidDataException("InvalidRelationshipMetrics");
        float Number(int index) => float.TryParse(match.Groups[index].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && float.IsFinite(value) ? value / 100f : throw new InvalidDataException("InvalidRelationshipMetrics");
        var familiarity = Number(1); var trust = Number(2); var affinity = Number(3);
        if (familiarity is < 0 or > 1 || trust is < 0 or > 1 || affinity is < -1 or > 1)
            throw new InvalidDataException("InvalidRelationshipMetrics");
        return (familiarity, trust, affinity);
    }

    private static string DirectoryFor(string root)
    {
        var state = Path.Combine(root, ".state"); var directory = Path.Combine(state, "document-edits");
        RejectLink(root); RejectLink(state); RejectLink(directory);
        return directory;
    }
    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("LinkedMemoryEditNotAllowed");
    }
}
