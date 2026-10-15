using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HexLive.AgentCore.Studio;

// §171.4: one append-only record per PROVIDER ATTEMPT — successful, failed or
// cancelled — written at the provider seam, before the answer is parsed. A paid
// answer that later fails decision validation is still a paid answer.
public sealed record UsageEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");
    // A reconciliation re-appends the same EventId with a higher Revision; readers
    // keep the highest one, so a provider statement never doubles a charge.
    public int Revision { get; init; }
    public DateTimeOffset AtUtc { get; init; }
    public string OwnerUserId { get; init; } = "";
    public string CharacterId { get; init; } = "";
    public string WorldId { get; init; } = "";
    public int NpcId { get; init; }
    public string SessionId { get; init; } = "";
    public string TurnId { get; init; } = "";
    public string Kind { get; init; } = "";          // llm | tts | stt | voice
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";         // model id, or TTS model for voice
    public string VoiceId { get; init; } = "";
    public string Status { get; init; } = "";        // ok | error | cancelled
    public string Error { get; init; } = "";         // exception TYPE only: messages may quote private speech
    public long DurationMs { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? CachedTokens { get; init; }
    public long? Characters { get; init; }
    public double? AudioSeconds { get; init; }
    public string ProviderRequestId { get; init; } = "";
    public decimal? ProviderCredits { get; init; } // provider units, never treated as USD
    // Null means UNKNOWN, never free (§171.4).
    public decimal? Cost { get; init; }
    public string Currency { get; init; } = "";
    public string TariffVersion { get; init; } = "";
    public string CostSource { get; init; } = UsageCostSource.Unknown;
}

public static class UsageCostSource
{
    public const string Unknown = "unknown";
    public const string Estimate = "estimate";
    public const string Reconciled = "reconciled";
}

public static class UsageKind
{
    public const string Llm = "llm";
    public const string Tts = "tts";
    public const string Stt = "stt";
    // §171.7: a whole hosted voice-agent session (ASR + turn-taking + TTS), billed per minute.
    public const string Voice = "voice";
}

/// <summary>Who the spend belongs to. Fixed per session; the turn comes from <see cref="UsageScope"/>.</summary>
public sealed record UsageContext(string OwnerUserId, string CharacterId, string WorldId, int NpcId, string SessionId);

public interface IUsageLedger
{
    void Append(UsageEvent usage);
}

/// <summary>Ambient turn id so a provider call is attributable without threading it through every signature.</summary>
public static class UsageScope
{
    private static readonly AsyncLocal<string?> Current = new();
    public static string TurnId => Current.Value ?? "";
    public static IDisposable Turn(string turnId)
    {
        var previous = Current.Value;
        Current.Value = turnId;
        return new Restore(previous);
    }
    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>Monthly JSONL files under one directory. Durable on return; a torn last line is skipped on read.</summary>
public sealed class JsonlUsageLedger : IUsageLedger
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly string _directory;
    private readonly object _gate = new();

    public JsonlUsageLedger(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public void Append(UsageEvent usage)
    {
        var line = JsonSerializer.Serialize(usage, Json) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        var path = Path.Combine(_directory, FileName(usage.AtUtc));
        lock (_gate)
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
    }

    public static string FileName(DateTimeOffset at) =>
        "usage-" + at.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl";

    /// <summary>All events, latest revision per EventId, in file order of their first appearance.</summary>
    public static IReadOnlyList<UsageEvent> ReadAll(string directory)
    {
        var latest = new Dictionary<string, UsageEvent>(StringComparer.Ordinal);
        var order = new List<string>();
        if (!Directory.Exists(directory)) return [];
        foreach (var file in Directory.GetFiles(directory, "usage-*.jsonl").Order(StringComparer.Ordinal))
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                UsageEvent? usage;
                try { usage = JsonSerializer.Deserialize<UsageEvent>(line, Json); }
                catch (JsonException) { continue; } // a crash mid-append leaves at most one torn line
                if (usage == null || usage.EventId.Length == 0) continue;
                if (!latest.TryGetValue(usage.EventId, out var known)) order.Add(usage.EventId);
                else if (known.Revision >= usage.Revision) continue;
                latest[usage.EventId] = usage;
            }
        return order.Select(id => latest[id]).ToList();
    }
}

/// <summary>Versioned price table, loaded from a file. No price is compiled in: provider rates are not verified here.</summary>
public sealed class UsageTariffs
{
    public string Version { get; init; } = "";
    public string Currency { get; init; } = "";
    public List<UsageRate> Rates { get; init; } = [];

    public static UsageTariffs Load(string path) =>
        JsonSerializer.Deserialize<UsageTariffs>(File.ReadAllText(path), JsonlUsageLedger.Json)
            ?? throw new InvalidDataException("InvalidUsageTariffs");

    public UsageRate? Find(string kind, string provider, string model) =>
        Rates.FirstOrDefault(r => r.Kind == kind && r.Provider == provider && r.Model == model)
        ?? Rates.FirstOrDefault(r => r.Kind == kind && r.Provider == provider && r.Model == "*");

    /// <summary>Stamps cost, currency and tariff version. Any missing unit or rate leaves the cost UNKNOWN.</summary>
    public UsageEvent Price(UsageEvent usage)
    {
        var rate = Find(usage.Kind, usage.Provider, usage.Model);
        var cost = rate?.Cost(usage);
        return cost == null
            ? usage with { Cost = null, Currency = "", TariffVersion = rate == null ? "" : Version, CostSource = UsageCostSource.Unknown }
            : usage with { Cost = cost, Currency = Currency, TariffVersion = Version, CostSource = UsageCostSource.Estimate };
    }
}

public sealed class UsageRate
{
    public string Kind { get; init; } = "";
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";
    public decimal? InputPerMillion { get; init; }
    public decimal? OutputPerMillion { get; init; }
    public decimal? CachedPerMillion { get; init; }
    public decimal? PerThousandCharacters { get; init; }
    public decimal? PerMinuteAudio { get; init; }

    internal decimal? Cost(UsageEvent u)
    {
        switch (u.Kind)
        {
            case UsageKind.Llm:
                if (u.InputTokens is not { } input || u.OutputTokens is not { } output ||
                    InputPerMillion is not { } inRate || OutputPerMillion is not { } outRate) return null;
                var cached = u.CachedTokens ?? 0;
                if (cached > 0 && CachedPerMillion == null) return null;
                return ((input - cached) * inRate + cached * (CachedPerMillion ?? 0) + output * outRate) / 1_000_000m;
            case UsageKind.Tts:
                return u.Characters is { } chars && PerThousandCharacters is { } perK ? chars * perK / 1000m : null;
            case UsageKind.Stt:
            case UsageKind.Voice:
                return u.AudioSeconds is { } seconds && PerMinuteAudio is { } perMin ? (decimal)seconds * perMin / 60m : null;
            default:
                return null;
        }
    }
}

/// <summary>Records every model attempt. Ledger failure never fails the turn — it is counted instead.</summary>
public sealed class MeteredModelAdapter(IModelAdapter inner, IUsageLedger ledger, UsageContext context,
    UsageTariffs? tariffs = null, TimeProvider? clock = null) : IModelAdapter, IDisposable
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public int FailedWrites => _failedWrites;
    private int _failedWrites;

    public async Task<ModelAnswer> CompleteAsync(ModelSelection selection, ModelRequest request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var answer = await inner.CompleteAsync(selection, request, cancellationToken);
            Record(selection, "ok", "", started, answer.InputTokens, answer.OutputTokens);
            return answer;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Record(selection, "cancelled", "", started, null, null);
            throw;
        }
        catch (Exception ex)
        {
            Record(selection, "error", ex.GetType().Name, started, null, null);
            throw;
        }
    }

    private void Record(ModelSelection selection, string status, string error, long started, long? input, long? output)
    {
        var usage = new UsageEvent
        {
            AtUtc = _clock.GetUtcNow(), OwnerUserId = context.OwnerUserId, CharacterId = context.CharacterId,
            WorldId = context.WorldId, NpcId = context.NpcId, SessionId = context.SessionId, TurnId = UsageScope.TurnId,
            Kind = UsageKind.Llm, Provider = selection.Provider.ToString(), Model = selection.ModelId,
            Status = status, Error = error, DurationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            InputTokens = input is >= 0 ? input : null, OutputTokens = output is >= 0 ? output : null,
        };
        try { ledger.Append(tariffs?.Price(usage) ?? usage); }
        catch (Exception) { Interlocked.Increment(ref _failedWrites); }
    }

    public void Dispose() => (inner as IDisposable)?.Dispose();
}

/// <summary>Records every TTS attempt: billed characters from the request, audio length from the returned WAV.</summary>
public sealed class MeteredVoiceAdapter(IVoiceAdapter inner, IUsageLedger ledger, UsageContext context,
    string provider, UsageTariffs? tariffs = null, TimeProvider? clock = null) : IVoiceAdapter
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public int FailedWrites => _failedWrites;
    private int _failedWrites;

    public async Task<byte[]> SynthesizeWavAsync(VoiceSelection selection, string text, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var wav = await inner.SynthesizeWavAsync(selection, text, cancellationToken);
            Record(selection, text, "ok", "", started, WavSeconds(wav));
            return wav;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Record(selection, text, "cancelled", "", started, null);
            throw;
        }
        catch (Exception ex)
        {
            Record(selection, text, "error", ex.GetType().Name, started, null);
            throw;
        }
    }

    private void Record(VoiceSelection selection, string text, string status, string error, long started, double? seconds)
    {
        var usage = new UsageEvent
        {
            AtUtc = _clock.GetUtcNow(), OwnerUserId = context.OwnerUserId, CharacterId = context.CharacterId,
            WorldId = context.WorldId, NpcId = context.NpcId, SessionId = context.SessionId, TurnId = UsageScope.TurnId,
            Kind = UsageKind.Tts, Provider = provider, Model = selection.ModelId, VoiceId = selection.VoiceId,
            Status = status, Error = error, DurationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            // The text length is what was SENT, so a failed or cancelled request keeps its possible charge visible.
            Characters = text?.Length ?? 0, AudioSeconds = seconds,
        };
        try { ledger.Append(tariffs?.Price(usage) ?? usage); }
        catch (Exception) { Interlocked.Increment(ref _failedWrites); }
    }

    /// <summary>Duration of a PCM RIFF/WAVE buffer, or null when the header is not one.</summary>
    public static double? WavSeconds(byte[] wav)
    {
        if (wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
            return null;
        int byteRate = 0, offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, offset, 4);
            var size = BitConverter.ToInt32(wav, offset + 4);
            if (size < 0) return null;
            if (id == "fmt " && offset + 20 <= wav.Length) byteRate = BitConverter.ToInt32(wav, offset + 16); // fmt: format,channels,rate,BYTE RATE
            if (id == "data") return byteRate > 0 ? Math.Min(size, wav.Length - offset - 8) / (double)byteRate : null;
            offset += 8 + size + (size & 1);
        }
        return null;
    }
}

/// <summary>Aggregates are always rebuilt from events, never stored as the source of truth (§171.4).</summary>
public static class UsageReport
{
    public sealed record Row(DateOnly Day, string OwnerUserId, string CharacterId, string Kind, string Provider, string Model)
    {
        public int Requests { get; set; }
        public int Failed { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long Characters { get; set; }
        public double AudioSeconds { get; set; }
        public decimal KnownCost { get; set; }
        public int UnknownCostRequests { get; set; }
        public string Currency { get; set; } = "";
    }

    public static IReadOnlyList<Row> Daily(IEnumerable<UsageEvent> events)
    {
        var rows = new Dictionary<(DateOnly, string, string, string, string, string), Row>();
        foreach (var e in events)
        {
            var key = (DateOnly.FromDateTime(e.AtUtc.UtcDateTime), e.OwnerUserId, e.CharacterId, e.Kind, e.Provider, e.Model);
            if (!rows.TryGetValue(key, out var row)) rows[key] = row = new Row(key.Item1, key.Item2, key.Item3, key.Item4, key.Item5, key.Item6);
            row.Requests++;
            if (e.Status != "ok") row.Failed++;
            row.InputTokens += e.InputTokens ?? 0; row.OutputTokens += e.OutputTokens ?? 0;
            row.Characters += e.Characters ?? 0; row.AudioSeconds += e.AudioSeconds ?? 0;
            if (e.Cost is { } cost && (row.Currency.Length == 0 || row.Currency == e.Currency))
            { row.KnownCost += cost; row.Currency = e.Currency; }
            else row.UnknownCostRequests++;
        }
        return rows.Values.OrderBy(r => r.Day).ThenBy(r => r.OwnerUserId, StringComparer.Ordinal)
            .ThenBy(r => r.CharacterId, StringComparer.Ordinal).ThenBy(r => r.Kind, StringComparer.Ordinal).ToList();
    }
}
