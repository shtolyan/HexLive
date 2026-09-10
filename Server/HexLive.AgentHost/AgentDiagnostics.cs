using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HexLive.AgentHost;

/// <summary>§163: bounded, local diagnostic evidence; never exception messages or provider payloads.</summary>
public sealed class AgentDiagnostics
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly TimeSpan _retention;
    private readonly Func<DateTimeOffset> _now;
    private readonly string _profile;
    private readonly string _session = Guid.NewGuid().ToString("N");
    private long _sequence;
    private string _world = "";
    private int _npc;
    private string _lastFailure = "";
    private int _repeatedFailures;
    private readonly Dictionary<string, string> _lastOutcomes = new();
    public string ErrorCode { get; private set; } = "";
    public string DirectoryPath => _directory;

    public AgentDiagnostics(string directory, string profile, long maxBytes = 8 * 1024 * 1024,
        int maxFiles = 8, TimeSpan? retention = null, Func<DateTimeOffset>? now = null)
    {
        if (maxBytes < 1024 || maxFiles < 2) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _directory = directory; _profile = Correlate(profile); _maxBytes = maxBytes;
        _maxFiles = maxFiles; _retention = retention ?? TimeSpan.FromDays(30);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public void Bind(string world, int npc)
    {
        lock (_gate) { _world = Correlate(world); _npc = npc; }
        Record("session.attached");
    }

    public void Record(string kind, string turn = "", string trigger = "", string tool = "",
        string result = "", long? elapsedMs = null, bool? committed = null,
        AgentDiagnosticObservation? observation = null, IReadOnlyList<string>? sourceIds = null,
        AgentDiagnosticRelationship? relationship = null)
    {
        lock (_gate)
        {
            try
            {
                Write(kind, turn, trigger, tool, result, elapsedMs, committed, observation, sourceIds, relationship);
                ErrorCode = "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // A disk failure must not turn a successfully executed command into a failed action.
                if (ErrorCode.Length == 0) Console.Error.WriteLine("[diagnostics] DiagnosticsUnavailable");
                ErrorCode = "DiagnosticsUnavailable";
            }
        }
    }

    public void Action(string turn, string tool, string result)
    {
        lock (_gate)
        {
            var key = Correlate(turn);
            if (_lastOutcomes.TryGetValue(key, out var previous) && previous == result) return;
            if (_lastOutcomes.Count >= 256) _lastOutcomes.Clear();
            _lastOutcomes[key] = result;
            Record("action.result", turn, tool: tool, result: result);
            if (result is "Accepted" or "AwaitingCarryContinuation" or "Cancelled") return;
            if (result is "Completed" or "PlanCompleted")
            { _lastFailure = ""; _repeatedFailures = 0; return; }
            var failure = tool + ":" + result;
            _repeatedFailures = failure == _lastFailure ? _repeatedFailures + 1 : 1;
            _lastFailure = failure;
            if (_repeatedFailures == 3)
                Record("signal.repeated_failure", turn, tool: tool, result: result);
        }
    }

    private void Write(string kind, string turn, string trigger, string tool, string result,
        long? elapsedMs, bool? committed, AgentDiagnosticObservation? observation, IReadOnlyList<string>? sourceIds,
        AgentDiagnosticRelationship? relationship)
    {
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var current = Path.Combine(_directory, "events.jsonl");
        var now = _now();
        var line = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, sessionId = _session, sequence = ++_sequence, utc = now,
            profileId = _profile, worldId = _world, npcId = _npc,
            runtimeVersion = typeof(AgentDiagnostics).Assembly.GetName().Version?.ToString(),
            kind = Code(kind), turnId = turn.Length == 0 ? "" : Correlate(turn),
            trigger = Code(trigger), tool = Code(tool), result = Code(result), elapsedMs, committed, observation,
            sourceIds = sourceIds?.Take(16).Select(ReferenceId).ToArray(), relationship
        }) + "\n";
        if (File.Exists(current) && new FileInfo(current).Length + Encoding.UTF8.GetByteCount(line) > _maxBytes)
            File.Move(current, Path.Combine(_directory, "events-" + now.ToUnixTimeMilliseconds() + "-" + Guid.NewGuid().ToString("N") + ".jsonl"));
        var files = new DirectoryInfo(_directory).GetFiles("events-*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
        for (var i = 0; i < files.Length; i++)
            if (i >= _maxFiles - 1 || now.UtcDateTime - files[i].LastWriteTimeUtc > _retention) files[i].Delete();
        if (File.Exists(current) && now.UtcDateTime - File.GetLastWriteTimeUtc(current) > _retention)
            File.Delete(current);
        using var stream = new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.Read);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(current, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var bytes = Encoding.UTF8.GetBytes(line); stream.Write(bytes); stream.Flush();
    }

    private static string Code(string value) => value.Length <= 96 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') ? value : "redacted";
    private static string ReferenceId(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value, "^(spec:[0-9]{1,4}[A-Z]?|spec:preamble|skill:[a-z-]{1,48}|skills:index|(?:recipes|build):[a-z0-9._-]{1,80}):[0-9]{1,10}:[a-f0-9]{64}$")
            ? value : Correlate(value);
    public static string Correlate(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16];
}

public sealed record AgentDiagnosticRelationship(float Familiarity, float Trust, float Sympathy,
    float? FamiliarityDelta, float? TrustDelta, float? SympathyDelta)
{
    public static AgentDiagnosticRelationship From(MashaArchive archive, string key)
    {
        var bond = MashaMemoryStore.BondFor(archive, key);
        archive.Speakers.TryGetValue(key, out var speaker);
        return new(bond.Familiarity, bond.Trust, bond.Affinity, speaker?.LastFamiliarityDelta,
            speaker?.LastTrustDelta, speaker?.LastAffinityDelta);
    }
}

/// <summary>Allowlisted observations, not raw world/prompt/voice payloads.</summary>
public sealed record AgentDiagnosticObservation(long? Tick, double? X, double? Y,
    int? CarriedItems, int? VisibleItems, int? Capacity, int? UsedSlots, int? FreeSlots)
{
    public static AgentDiagnosticObservation From(JsonElement state)
    {
        static int? Count(JsonElement e, string key) => e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Array ? p.GetArrayLength() : null;
        static int? Number(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) ? n : null;
        static double? Coordinate(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
        state.TryGetProperty("position", out var position); state.TryGetProperty("inventorySummary", out var inventory);
        return new(state.TryGetProperty("tick", out var tick) && tick.ValueKind == JsonValueKind.Number && tick.TryGetInt64(out var t) ? t : null,
            Coordinate(position, "x"), Coordinate(position, "y"), Count(state, "inventoryItems"), Count(state, "visibleItems"),
            Number(inventory, "capacity"), Number(inventory, "usedSlots"), Number(inventory, "freeSlots"));
    }
}
