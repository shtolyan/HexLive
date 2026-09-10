using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HexLive.AgentHost;

public sealed class MashaIdentity
{
    public string Id { get; set; } = "masha";
    public string Name { get; set; } = AgentPromptFiles.Text("MashaMemoryStore.01");
    public int Age { get; set; } = 23;
    public List<string> Traits { get; set; } =
        [AgentPromptFiles.Text("MashaMemoryStore.02"), AgentPromptFiles.Text("MashaMemoryStore.03"), AgentPromptFiles.Text("MashaMemoryStore.04"), AgentPromptFiles.Text("MashaMemoryStore.05"), AgentPromptFiles.Text("MashaMemoryStore.06")];
}

public sealed class PortablePlayerBond
{
    public float Familiarity { get; set; }
    public float Trust { get; set; }
    public float Affinity { get; set; }
    public string LastInteractionEpisodeId { get; set; } = string.Empty;
    public long LastInteractionTick { get; set; } = -1;
    public DateTimeOffset? LastInteractionUtc { get; set; }
    public bool? LastObservedPlayerPresent { get; set; }
    public DateTimeOffset? LastObservedDepartureUtc { get; set; }
    public DateTimeOffset? LastObservedReturnUtc { get; set; }
    public bool AwaitingReturnVoice { get; set; }
    public string ClockWorldKey { get; set; } = "";
    public long LastClockTick { get; set; } = -1;
    public long LastVoiceTick { get; set; } = -1;
    public long LastDepartureTick { get; set; } = -1;
    public long LastReturnTick { get; set; } = -1;
}

public sealed class PortableMemory
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public float Importance { get; set; }
    public long UpdatedAtTick { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string Source { get; set; } = string.Empty;
    public string SpeakerKey { get; set; } = "";
}

public sealed class PortableJournalEntry
{
    public string SpeakerKey { get; set; } = "";
    public long Tick { get; set; }
    public long GameHour { get; set; } = -1;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}

public sealed class MashaWorldEpisode
{
    public string Id { get; set; } = string.Empty;
    public string Game { get; set; } = string.Empty;
    public string WorldKey { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int? Seed { get; set; }
    public string Mode { get; set; } = string.Empty;
    public int AvatarNpcId { get; set; }
    public string Status { get; set; } = "active";
    public long FirstTick { get; set; }
    public long LastTick { get; set; }
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public int LanguageExposure { get; set; }
    public string LastIntentSummary { get; set; } = string.Empty;
    public List<PortableMemory> Memories { get; set; } = new();
    public List<PortableJournalEntry> Journal { get; set; } = new();
}

public sealed class MashaArchive
{
    public int SchemaVersion { get; set; } = 1;
    public MashaIdentity Identity { get; set; } = new();
    public Dictionary<string, SpeakerMemory> Speakers { get; set; } = new(StringComparer.Ordinal);
    public string? PrimarySpeakerKey { get; set; }
    public PortablePlayerBond PlayerBond { get; set; } = new();
    public List<PortableMemory> CoreMemories { get; set; } = new();
    public List<MashaWorldEpisode> Worlds { get; set; } = new();
    public Dictionary<string, string> ImportFingerprints { get; set; } =
        new(StringComparer.Ordinal);
    public List<string> AppliedTurnIds { get; set; } = new();
    public List<string> AppliedDocumentEditIds { get; set; } = new();
    public HashSet<string> SuppressedMemoryValues { get; set; } = new(StringComparer.Ordinal);
}

public sealed record MashaWorldHandle(string EpisodeId, string WorldKey, long Tick, long GameHour)
{
    public string SpeakerKey { get; init; } = "";
    public string[] MessageIds { get; init; } = [];
    public string PlayerText { get; init; } = "";
    public int DayLengthTicks { get; init; }
}
public sealed record MashaImportResult(bool Changed, int Memories, int JournalEntries, string EpisodeId);

/// <summary>
/// Portable, game-independent identity store. It deliberately lives outside a HexLive
/// save and never receives raw audio; keeps only a bounded recent conversation per speaker.
/// </summary>
public sealed partial class MashaMemoryStore
{
    public const int MaxCoreMemories = 64;
    public const int MaxWorldMemories = 64;
    public const int MaxJournalEntriesPerWorld = 48;
    public const int MaxAppliedTurnIds = 256;
    public const int MaxWorlds = 32;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;
    private readonly MashaMemoryWorkspace _workspace;
    private MashaArchive _archive;
    public AgentMemoryArchive History { get; }

    public MashaMemoryStore(string memoryDirectory, MashaIdentity? initialIdentity = null)
    {
        if (string.IsNullOrWhiteSpace(memoryDirectory))
            throw new ArgumentException("A Masha memory directory is required.", nameof(memoryDirectory));

        var directory = Path.GetFullPath(memoryDirectory);
        Directory.CreateDirectory(directory);
        _workspace = new MashaMemoryWorkspace(directory);
        _filePath = _workspace.StatePath;
        var loadPath = File.Exists(_filePath) || !File.Exists(_workspace.LegacyStatePath)
            ? _filePath
            : _workspace.LegacyStatePath;
        using var documentLock = _workspace.AcquireDocumentLock();
        _archive = LoadOrCreate(loadPath, initialIdentity);
        History = new AgentMemoryArchive(directory);
        History.Migrate(_archive);
        if (!string.Equals(loadPath, _filePath, StringComparison.Ordinal))
            WriteArchiveAtomically(_filePath, _archive);
        var edits = ApplyDocumentEdits();
        if (edits.Count > 0) WriteArchiveAtomically(_filePath, _archive);
        _workspace.WriteViews(_archive, true);
        MemoryDocumentEdits.Complete(edits);
    }

    public string FilePath => _filePath;

    public async Task<MashaWorldHandle> BindHexLiveWorldAsync(
        JsonElement worldStatus,
        int npcId,
        string configuredWorldId,
        CancellationToken cancellationToken)
    {
        var tick = ReadInt64(worldStatus, "tick");
        var seed = ReadNullableInt32(worldStatus, "seed");
        var mode = ReadString(worldStatus, "mode");
        var actualWorldId = ReadString(worldStatus, "worldId");
        var stable = actualWorldId.Length > 0 ? SafeKey(actualWorldId) : string.IsNullOrWhiteSpace(configuredWorldId)
            ? "seed-" + (seed?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown")
            : SafeKey(configuredWorldId);
        var worldKey = "hexlive:" + stable;
        var now = DateTimeOffset.UtcNow;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var episode = _archive.Worlds.LastOrDefault(x =>
                string.Equals(x.Game, "hexlive", StringComparison.Ordinal) &&
                string.Equals(x.WorldKey, worldKey, StringComparison.Ordinal) &&
                string.Equals(x.Status, "active", StringComparison.Ordinal));

            // A large backwards jump means a new world/timeline reused the same seed.
            if (episode != null && episode.LastTick - tick > 300)
            {
                episode.Status = "left";
                episode = null;
            }

            if (episode == null)
            {
                var incarnation = _archive.Worlds.Count(x =>
                    string.Equals(x.Game, "hexlive", StringComparison.Ordinal) &&
                    string.Equals(x.WorldKey, worldKey, StringComparison.Ordinal)) + 1;
                episode = new MashaWorldEpisode
                {
                    Id = worldKey + ":" + incarnation,
                    Game = "hexlive",
                    WorldKey = worldKey,
                    Label = mode.Length > 0 ? $"HexLive {mode} #{seed}" : $"HexLive #{seed}",
                    Seed = seed,
                    Mode = mode,
                    AvatarNpcId = npcId,
                    FirstTick = tick,
                    LastTick = tick,
                    FirstSeenUtc = now,
                    LastSeenUtc = now
                };
                episode.Memories.Add(new PortableMemory
                {
                    Key = "arrival.world",
                    Value = AgentPromptFiles.Text("MashaMemoryStore.07"),
                    Importance = 1f,
                    UpdatedAtTick = tick,
                    UpdatedAtUtc = now,
                    Source = "world-adapter"
                });
                History.Append(new() { Id = AgentMemoryArchive.Id("episode:" + episode.Id), Kind = "event",
                    Text = string.Format(AgentPromptFiles.Text("HistoryEpisodeStart"), episode.Label), Source = "world-adapter",
                    Episode = episode.Id, Group = episode.Id, OccurredUtc = now, Tick = tick,
                    DayLengthTicks = ReadNullableInt32(worldStatus, "dayLengthTicks") ?? 0, Incomplete = true });
                _archive.Worlds.Add(episode);
                TrimWorlds();
            }
            else
            {
                episode.LastTick = Math.Max(episode.LastTick, tick);
                episode.LastSeenUtc = now;
                episode.AvatarNpcId = npcId;
                if (episode.Mode.Length == 0) episode.Mode = mode;
            }

            await SaveAsync(cancellationToken).ConfigureAwait(false);
            return new MashaWorldHandle(episode.Id, worldKey, tick, GameHour(worldStatus, tick))
            { DayLengthTicks = ReadNullableInt32(worldStatus, "dayLengthTicks") ?? 0 };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> NeedsHexLiveLegacyImportAsync(
        MashaWorldHandle world,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return !_archive.ImportFingerprints.ContainsKey("hexlive:" + world.EpisodeId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MashaImportResult> ImportHexLiveLegacyAsync(
        MashaWorldHandle world,
        JsonElement companion,
        CancellationToken cancellationToken)
    {
        var fingerprint = Sha256(companion.GetRawText());
        var importKey = "hexlive:" + world.EpisodeId;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var episode = RequireEpisode(world.EpisodeId);
            if (_archive.ImportFingerprints.TryGetValue(importKey, out var prior) && prior == fingerprint)
                return new MashaImportResult(false, 0, 0, episode.Id);

            var memories = 0;
            var journal = 0;
            if (companion.TryGetProperty("memories", out var memoryArray) &&
                memoryArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in memoryArray.EnumerateArray())
                {
                    var key = ReadString(item, "key");
                    var value = ReadString(item, "value");
                    if (key.Length == 0 || value.Length == 0) continue;
                    Upsert(episode.Memories, new PortableMemory
                    {
                        Key = key,
                        Value = Limit(value, 400),
                        Importance = Math.Clamp(ReadSingle(item, "importance"), 0f, 1f),
                        UpdatedAtTick = ReadInt64(item, "lastUpdatedTick"),
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        Source = "hexlive-legacy"
                    }, MaxWorldMemories);
                    memories++;
                }
            }

            if (companion.TryGetProperty("journal", out var journalArray) &&
                journalArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in journalArray.EnumerateArray())
                {
                    var text = ReadString(item, "text");
                    if (text.Length == 0) continue;
                    var tick = ReadInt64(item, "tick");
                    if (episode.Journal.Any(x => x.Tick == tick && x.Text == text)) continue;
                    episode.Journal.Add(new PortableJournalEntry
                    {
                        Tick = tick,
                        GameHour = -1,
                        CreatedAtUtc = DateTimeOffset.UtcNow,
                        Text = Limit(text, 400),
                        Source = "hexlive-legacy"
                    });
                    journal++;
                }
                TrimOldest(episode.Journal, MaxJournalEntriesPerWorld);
            }

            episode.LanguageExposure = Math.Max(
                episode.LanguageExposure, ReadNullableInt32(companion, "hexkufaExposure") ?? 0);
            var intent = ReadString(companion, "lastIntentSummary");
            if (intent.Length > 0) episode.LastIntentSummary = Limit(intent, 240);

            if (companion.TryGetProperty("playerVoiceBond", out var bond) &&
                bond.ValueKind == JsonValueKind.Object)
            {
                _archive.PlayerBond.Familiarity = Math.Max(
                    _archive.PlayerBond.Familiarity, Math.Clamp(ReadSingle(bond, "familiarity"), 0f, 1f));
                _archive.PlayerBond.Trust = Math.Max(
                    _archive.PlayerBond.Trust, Math.Clamp(ReadSingle(bond, "trust"), 0f, 1f));
                _archive.PlayerBond.Affinity = Math.Max(
                    _archive.PlayerBond.Affinity, Math.Clamp(ReadSingle(bond, "affinity"), 0f, 1f));
                var lastTick = ReadInt64(bond, "lastInteractionTick");
                if (lastTick >= 0)
                {
                    _archive.PlayerBond.LastInteractionEpisodeId = episode.Id;
                    _archive.PlayerBond.LastInteractionTick = lastTick;
                }
            }

            _archive.ImportFingerprints[importKey] = fingerprint;
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            return new MashaImportResult(true, memories, journal, episode.Id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MashaImportResult> ImportMollyDirectoryAsync(
        string suppliedPath,
        string sourceLabel,
        CancellationToken cancellationToken)
    {
        var root = ResolveMollyRoot(suppliedPath);
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(IsMollyMemoryFile)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
            throw new InvalidDataException("No Molly memory files were found in the supplied directory.");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file)));
            hash.AppendData(await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false));
        }
        var fingerprint = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        var importKey = "molly-files:" + fingerprint;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _workspace.PreserveImport(root, fingerprint, sourceLabel);
            if (_archive.ImportFingerprints.ContainsKey(importKey))
            {
                var oldEpisode = _archive.Worlds.LastOrDefault(x => x.Game == "molly");
                var changed = oldEpisode != null && ImportMollyPlayerBond(root, oldEpisode);
                if (changed) await SaveAsync(cancellationToken).ConfigureAwait(false);
                return new MashaImportResult(changed, 0, 0, oldEpisode?.Id ?? "molly:legacy:1");
            }

            var episode = _archive.Worlds.LastOrDefault(x => x.Game == "molly" && x.Status == "active");
            if (episode == null)
            {
                var number = _archive.Worlds.Count(x => x.Game == "molly") + 1;
                episode = new MashaWorldEpisode
                {
                    Id = "molly:legacy:" + number,
                    Game = "molly",
                    WorldKey = "molly:legacy",
                    Label = Limit(string.IsNullOrWhiteSpace(sourceLabel) ? "Molly" : sourceLabel, 80),
                    FirstSeenUtc = DateTimeOffset.UtcNow,
                    LastSeenUtc = DateTimeOffset.UtcNow
                };
                _archive.Worlds.Add(episode);
            }

            var memories = 0;
            memories += ImportMarkdownMemories(
                Path.Combine(root, "USER.md"), _archive.CoreMemories, "molly-user", MaxCoreMemories);
            memories += ImportMarkdownMemories(
                Path.Combine(root, "MEMORY.md"), episode.Memories, "molly-memory", MaxWorldMemories);
            ImportMollyPlayerBond(root, episode);

            // A changed Molly snapshot replaces its own bounded diary window.
            // Otherwise an older line appended during re-import could evict a
            // newer retained line merely because the same text was deduplicated.
            episode.Journal.RemoveAll(x =>
                string.Equals(x.Source, "molly-files", StringComparison.Ordinal));
            var journal = 0;
            var journalFiles = files.Where(x =>
                x.EndsWith("thoughts.md", StringComparison.OrdinalIgnoreCase) ||
                Path.GetDirectoryName(x)?.EndsWith("diary", StringComparison.OrdinalIgnoreCase) == true);
            foreach (var file in journalFiles)
            {
                foreach (var line in File.ReadLines(file))
                {
                    var text = line.Trim();
                    if (text.Length == 0 || text.StartsWith("#", StringComparison.Ordinal)) continue;
                    if (episode.Journal.Any(x => x.Text == text)) continue;
                    episode.Journal.Add(new PortableJournalEntry
                    {
                        CreatedAtUtc = File.GetLastWriteTimeUtc(file),
                        Text = Limit(text, 400),
                        Source = "molly-files"
                    });
                    journal++;
                }
            }
            TrimOldest(episode.Journal, MaxJournalEntriesPerWorld);

            var statePath = Path.Combine(root, "state.txt");
            if (File.Exists(statePath) && File.ReadLines(statePath).Any(x =>
                    string.Equals(x.Trim(), "dead: true", StringComparison.OrdinalIgnoreCase)))
            {
                episode.Status = "died";
            }

            // conversations.md is included in the fingerprint so re-imports are stable,
            // and its full transcript is preserved in the local evidence archive (§165).
            _archive.ImportFingerprints[importKey] = fingerprint;
            TrimWorlds();
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            return new MashaImportResult(true, memories, journal, episode.Id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ObservePlayerPresenceAsync(bool present, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bond = _archive.PlayerBond;
            if (bond.LastObservedPlayerPresent == present) return;
            if (present)
            {
                bond.LastObservedReturnUtc = DateTimeOffset.UtcNow;
                if (bond.LastObservedPlayerPresent == false && bond.LastInteractionUtc.HasValue)
                    bond.AwaitingReturnVoice = true;
            }
            else
            {
                bond.LastObservedDepartureUtc = DateTimeOffset.UtcNow;
            }
            bond.LastObservedPlayerPresent = present;
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<MashaPromptContext> BuildPromptContextAsync(
        MashaWorldHandle world,
        string recallQuery,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshDocumentEditsAsync(cancellationToken);
            var projected = JsonSerializer.Deserialize<MashaArchive>(JsonSerializer.Serialize(_archive, JsonOptions), JsonOptions)!;
            var legacy = world.SpeakerKey.Length == 0 || world.SpeakerKey == _archive.PrimarySpeakerKey;
            if (world.SpeakerKey.Length > 0)
            {
                var speaker = GetSpeaker(world.SpeakerKey);
                projected.PlayerBond = speaker.Bond;
                projected.CoreMemories.RemoveAll(m => m.Source is "model-user" or "molly-user");
                projected.CoreMemories.AddRange(speaker.Facts);
                foreach (var chapter in projected.Worlds)
                {
                    chapter.Memories.RemoveAll(m => m.SpeakerKey.Length > 0 ? m.SpeakerKey != world.SpeakerKey :
                        !legacy && m.Source is "model" or "hexlive-legacy" or "molly-files");
                    chapter.Journal.RemoveAll(m => m.SpeakerKey.Length > 0 ? m.SpeakerKey != world.SpeakerKey : !legacy);
                }
            }
            projected.CoreMemories.RemoveAll(m => MemoryDocumentEdits.IsSuppressedInContext(projected, world.SpeakerKey, m.Value));
            foreach (var chapter in projected.Worlds)
            {
                chapter.Memories.RemoveAll(m => MemoryDocumentEdits.IsSuppressedInContext(projected, world.SpeakerKey, m.Value));
                chapter.Journal.RemoveAll(m => MemoryDocumentEdits.IsSuppressedInContext(projected, world.SpeakerKey, m.Text));
            }
            return _workspace.BuildPrompt(projected, projected.Worlds.Single(w => w.Id == world.EpisodeId), recallQuery, world, legacy);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ObserveHexLiveStateAsync(
        MashaWorldHandle world,
        JsonElement bodyState,
        CancellationToken cancellationToken)
    {
        if (!bodyState.TryGetProperty("hexkufaExposure", out var exposure) ||
            !exposure.TryGetInt32(out var contacts)) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var episode = RequireEpisode(world.EpisodeId);
            if (contacts <= episode.LanguageExposure) return;
            episode.LanguageExposure = contacts;
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> CommitTurnAsync(
        MashaWorldHandle world,
        string turnId,
        string trigger,
        CompanionDecision decision,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshDocumentEditsAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(turnId) || _archive.AppliedTurnIds.Contains(turnId))
                return false;



            if (world.SpeakerKey.Length > 0 && world.MessageIds.Any(GetSpeaker(world.SpeakerKey).AppliedMessageIds.Contains))
                return false;
            // Validate before mutating any memory, even for callers restoring a persisted outbox.
            if (trigger == "voice" && decision.RelationshipAssessment is { } proposed)
                new HexLive.AgentCore.Studio.VoiceRelationship(new(0, 0, 0, AgentPromptFiles.Text("MashaMemoryStore.08"), null))
                    .Apply(world.MessageIds.Length > 0 ? world.MessageIds : [turnId], proposed, DateTimeOffset.UtcNow);
            History.AppendMany(new[] {
                new AgentMemoryRecord { Id = AgentMemoryArchive.Id("speech:" + turnId), Kind = "speech", Text = decision.Speech,
                    Source = "agent", Group = world.SpeakerKey, Speaker = world.SpeakerKey, Episode = world.EpisodeId,
                    OccurredUtc = DateTimeOffset.UtcNow, Tick = world.Tick, DayLengthTicks = world.DayLengthTicks, Status = "prepared" },
                new AgentMemoryRecord { Id = AgentMemoryArchive.Id("intent:" + turnId), Kind = "intent", Text = decision.IntentSummary,
                    Source = "agent", Group = turnId, Speaker = world.SpeakerKey, Episode = world.EpisodeId,
                    OccurredUtc = DateTimeOffset.UtcNow, Tick = world.Tick, DayLengthTicks = world.DayLengthTicks },
                new AgentMemoryRecord { Id = AgentMemoryArchive.Id("diary:" + turnId), Kind = "diary", Text = decision.JournalText,
                    Source = "agent", Group = turnId, Speaker = world.SpeakerKey, Episode = world.EpisodeId,
                    OccurredUtc = DateTimeOffset.UtcNow, Tick = world.Tick, DayLengthTicks = world.DayLengthTicks }
            }.Where(r => r.Text.Length > 0).Concat(decision.MemoryUpserts.Select((m, i) => new AgentMemoryRecord {
                Id = AgentMemoryArchive.Id("note:" + turnId + ":" + i), Kind = "note", Text = m.Value, Source = "agent",
                Speaker = world.SpeakerKey, Episode = world.EpisodeId, Group = turnId, OccurredUtc = DateTimeOffset.UtcNow,
                Tick = world.Tick, DayLengthTicks = world.DayLengthTicks })));
            var episode = RequireEpisode(world.EpisodeId);
            episode.LastTick = Math.Max(episode.LastTick, world.Tick);
            episode.LastSeenUtc = DateTimeOffset.UtcNow;
            episode.LastIntentSummary = Limit(decision.IntentSummary, 240);

            foreach (var update in decision.MemoryUpserts.Take(3))
            {
                if (string.IsNullOrWhiteSpace(update.Key) || string.IsNullOrWhiteSpace(update.Value)) continue;
                var scope = update.Key.StartsWith("user:", StringComparison.Ordinal) ? "model-user" :
                    update.Key.StartsWith("self:", StringComparison.Ordinal) ? "model-self" :
                    update.Key.StartsWith("core:", StringComparison.Ordinal) ? "model-core" : "model";
                if (MemoryDocumentEdits.IsSuppressed(_archive, scope, world.SpeakerKey, Limit(update.Value, 400))) continue;
                var destination = scope == "model-user" && world.SpeakerKey.Length > 0 ? GetSpeaker(world.SpeakerKey).Facts :
                    scope == "model" ? episode.Memories : _archive.CoreMemories;
                Upsert(destination, new PortableMemory
                {
                    Key = Limit(update.Key, 64),
                    Value = Limit(update.Value, 400),
                    Importance = Math.Clamp(update.Importance, 0f, 1f),
                    UpdatedAtTick = world.Tick,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Source = scope,
                    SpeakerKey = scope == "model-core" || scope == "model-self" ? "" : world.SpeakerKey
                }, scope == "model" ? MaxWorldMemories : MaxCoreMemories);
            }

            var journalText = Limit(decision.JournalText, 400);
            if (journalText.Length > 0 &&
                !episode.Journal.Any(x => x.GameHour == world.GameHour && x.GameHour >= 0))
            {
                episode.Journal.Add(new PortableJournalEntry
                {
                    Tick = world.Tick,
                    GameHour = world.GameHour,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    Text = journalText,
                    Source = "model",
                    SpeakerKey = world.SpeakerKey
                });
                TrimOldest(episode.Journal, MaxJournalEntriesPerWorld);
            }

            if (string.Equals(trigger, "voice", StringComparison.Ordinal))
            {
                if (world.SpeakerKey.Length > 0)
                {
                    ApplySpeakerAssessment(world, turnId, decision);
                    var recent = GetSpeaker(world.SpeakerKey).RecentConversation;
                    if (world.PlayerText.Length > 0) recent.Add(AgentPromptFiles.Text("MashaMemoryStore.09") + Limit(world.PlayerText, 4000));
                    if (decision.Speech.Length > 0) recent.Add(_archive.Identity.Name + ": " + Limit(decision.Speech, 600));
                    TrimOldest(recent, 12);
                }
                else if (decision.RelationshipAssessment is { } assessment)
                {
                    var b = _archive.PlayerBond;
                    var r = new HexLive.AgentCore.Studio.VoiceRelationship(new(b.Familiarity, b.Trust, b.Affinity, AgentPromptFiles.Text("MashaMemoryStore.10"), null));
                    r.Apply([turnId], assessment, DateTimeOffset.UtcNow);
                    b.Familiarity = r.Snapshot.Familiarity; b.Trust = r.Snapshot.Trust; b.Affinity = r.Snapshot.Sympathy;
                }
                // Legacy persisted outbox decisions without assessments cannot change relationships.
                if (world.SpeakerKey.Length == 0)
                {
                _archive.PlayerBond.LastInteractionEpisodeId = world.EpisodeId;
                _archive.PlayerBond.LastInteractionTick = world.Tick;
                _archive.PlayerBond.LastInteractionUtc = DateTimeOffset.UtcNow;
                // A neutral/silent response still heard the new voice; heartbeat never consumes it.
                _archive.PlayerBond.AwaitingReturnVoice = false;
                AgentGameTime.Observe(_archive.PlayerBond, world);
                _archive.PlayerBond.LastVoiceTick = world.Tick;
                }
            }

            _archive.AppliedTurnIds.Add(Limit(turnId, 80));
            TrimOldest(_archive.AppliedTurnIds, MaxAppliedTurnIds);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MashaArchive> SnapshotAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshDocumentEditsAsync(cancellationToken);
            var json = JsonSerializer.Serialize(_archive, JsonOptions);
            return JsonSerializer.Deserialize<MashaArchive>(json, JsonOptions)!;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static MashaArchive LoadOrCreate(string path, MashaIdentity? initialIdentity)
    {
        MashaArchive archive;
        if (File.Exists(path))
        {
            try
            {
                archive = JsonSerializer.Deserialize<MashaArchive>(File.ReadAllText(path), JsonOptions)
                          ?? throw new InvalidDataException("Masha memory file is empty.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Masha memory file is invalid; it was not overwritten.", ex);
            }
        }
        else
        {
            archive = new MashaArchive();
            if (initialIdentity != null)
            {
                if (string.IsNullOrWhiteSpace(initialIdentity.Id) || string.IsNullOrWhiteSpace(initialIdentity.Name) ||
                    initialIdentity.Name.Length > 48 || initialIdentity.Age is < 18 or > 120)
                    throw new InvalidDataException("InvalidInitialIdentity");
                archive.Identity = new MashaIdentity { Id = initialIdentity.Id, Name = initialIdentity.Name,
                    Age = initialIdentity.Age, Traits = new List<string>(initialIdentity.Traits) };
            }
        }

        if (archive.SchemaVersion is < 1 or > 2) throw new InvalidDataException("UnsupportedMemorySchema");
        archive.Speakers ??= new(StringComparer.Ordinal);
        archive.ImportFingerprints ??= new Dictionary<string, string>(StringComparer.Ordinal);
        archive.CoreMemories ??= new List<PortableMemory>();
        archive.Worlds ??= new List<MashaWorldEpisode>();
        archive.AppliedTurnIds ??= new List<string>();
        archive.AppliedDocumentEditIds ??= new();
        archive.SuppressedMemoryValues ??= new(StringComparer.Ordinal);
        if (archive.Identity.Id == "masha")
        {
        EnsureCoreMemory(archive, "origin.life_before_room",
            AgentPromptFiles.Text("MashaMemoryStore.11"), 1f);
        EnsureCoreMemory(archive, "origin.strange_room",
            AgentPromptFiles.Text("MashaMemoryStore.12"), 1f);
        }
        EnsureCoreMemory(archive, "origin.player_voice",
            AgentPromptFiles.Text("MashaMemoryStore.13"), 0.95f);
        if (!File.Exists(path))
        {
            var json = JsonSerializer.Serialize(archive, JsonOptions);
            File.WriteAllText(path, json);
            RestrictPermissions(path);
        }
        return archive;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        using var documentLock = _workspace.AcquireDocumentLock();
        var edits = ApplyDocumentEdits();
        var temp = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temp, JsonSerializer.Serialize(_archive, JsonOptions), cancellationToken)
                .ConfigureAwait(false);
            RestrictPermissions(temp);
            File.Move(temp, _filePath, true);
            RestrictPermissions(_filePath);
            _workspace.WriteViews(_archive, true);
            MemoryDocumentEdits.Complete(edits);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    private Task RefreshDocumentEditsAsync(CancellationToken token) =>
        MemoryDocumentEdits.HasPending(Path.GetDirectoryName(_workspace.StateDirectory)!) ? SaveAsync(token) : Task.CompletedTask;

    private IReadOnlyList<string> ApplyDocumentEdits()
    {
        var root = Path.GetDirectoryName(_workspace.StateDirectory)!;
        if (!MemoryDocumentEdits.HasPending(root)) return [];
        var backups = Path.Combine(_workspace.StateDirectory, "backups");
        if (Directory.Exists(backups) && (File.GetAttributes(backups) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("LinkedMemoryBackupNotAllowed");
        Directory.CreateDirectory(backups);
        WriteArchiveAtomically(Path.Combine(backups, "before-document-edit-" + Guid.NewGuid().ToString("N") + ".json"), _archive);
        var next = JsonSerializer.Deserialize<MashaArchive>(JsonSerializer.Serialize(_archive, JsonOptions), JsonOptions)!;
        var edits = MemoryDocumentEdits.ApplyPending(root, next);
        _archive = next;
        return edits;
    }

    private static void WriteArchiveAtomically(string path, MashaArchive archive)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(archive, JsonOptions));
            RestrictPermissions(temp);
            File.Move(temp, path, true);
            RestrictPermissions(path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    private static void EnsureCoreMemory(
        MashaArchive archive, string key, string value, float importance)
    {
        if (MemoryDocumentEdits.IsSuppressed(archive, "identity-seed", "", value)) return;
        if (archive.CoreMemories.Any(x => x.Key == key || MemoryDocumentEdits.Normalize(x.Value) == MemoryDocumentEdits.Normalize(value))) return;
        archive.CoreMemories.Add(new PortableMemory
        {
            Key = key,
            Value = value,
            Importance = importance,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Source = "identity-seed"
        });
    }

    private MashaWorldEpisode RequireEpisode(string id) =>
        _archive.Worlds.FirstOrDefault(x => x.Id == id) ??
        throw new InvalidOperationException("Masha world episode is no longer available.");

    private static void Upsert(List<PortableMemory> target, PortableMemory value, int maximum)
    {
        var existing = target.FirstOrDefault(x => string.Equals(x.Key, value.Key, StringComparison.Ordinal) &&
            x.SpeakerKey == value.SpeakerKey);
        if (existing == null)
        {
            target.Add(value);
        }
        else
        {
            existing.Value = value.Value;
            existing.Importance = value.Importance;
            existing.UpdatedAtTick = value.UpdatedAtTick;
            existing.UpdatedAtUtc = value.UpdatedAtUtc;
            existing.Source = value.Source;
        }

        while (target.Count > maximum)
        {
            var discard = target.OrderBy(x => x.Importance).ThenBy(x => x.UpdatedAtUtc).First();
            target.Remove(discard);
        }
    }

    private void ApplyReaction(MashaWorldHandle world, string reaction)
    {
        var familiarity = 0f;
        var trust = 0f;
        var affinity = 0f;
        switch (reaction)
        {
            case "Warm": familiarity = 0.03f; trust = 0.03f; affinity = 0.02f; break;
            case "Neutral": familiarity = 0.02f; break;
            case "Tense": familiarity = 0.01f; trust = -0.03f; affinity = -0.02f; break;
            case "Hostile": familiarity = 0.01f; trust = -0.06f; affinity = -0.04f; break;
            default: return;
        }
        _archive.PlayerBond.Familiarity = Math.Clamp(_archive.PlayerBond.Familiarity + familiarity, 0f, 1f);
        _archive.PlayerBond.Trust = Math.Clamp(_archive.PlayerBond.Trust + trust, 0f, 1f);
        _archive.PlayerBond.Affinity = Math.Clamp(_archive.PlayerBond.Affinity + affinity, 0f, 1f);
    }

    private static IEnumerable<PortableMemory> Important(IEnumerable<PortableMemory> memories, int count) =>
        memories.OrderByDescending(x => x.Importance).ThenByDescending(x => x.UpdatedAtUtc).Take(count);

    private void TrimWorlds()
    {
        while (_archive.Worlds.Count > MaxWorlds)
        {
            var discard = _archive.Worlds.Where(x => x.Status != "active")
                .OrderBy(x => x.LastSeenUtc).FirstOrDefault();
            if (discard == null) break;
            _archive.Worlds.Remove(discard);
        }
    }

    private static void TrimOldest<T>(List<T> list, int maximum)
    {
        while (list.Count > maximum) list.RemoveAt(0);
    }

    private static int ImportMarkdownMemories(
        string path, List<PortableMemory> target, string source, int maximum)
    {
        if (!File.Exists(path)) return 0;
        var count = 0;
        var index = 0;
        foreach (var line in File.ReadLines(path))
        {
            var text = line.Trim();
            if (text.StartsWith("-", StringComparison.Ordinal)) text = text[1..].Trim();
            if (text.Length == 0 || text.StartsWith("#", StringComparison.Ordinal)) continue;
            Upsert(target, new PortableMemory
            {
                Key = source + "." + index++,
                Value = Limit(text, 400),
                Importance = 0.75f,
                UpdatedAtUtc = File.GetLastWriteTimeUtc(path),
                Source = source
            }, maximum);
            count++;
        }
        return count;
    }

    private bool ImportMollyPlayerBond(string root, MashaWorldEpisode episode)
    {
        var statePath = Path.Combine(root, "state.txt");
        var friendship = ReadLegacyStateFloat(statePath, "rel_friendship");
        var love = ReadLegacyStateFloat(statePath, "rel_love");
        var hostility = ReadLegacyStateFloat(statePath, "rel_hostility");
        if (friendship == null && love == null && hostility == null) return false;

        var familiarity = Math.Clamp((friendship ?? 0f) / 100f, 0f, 1f);
        var trust = Math.Clamp(((friendship ?? 0f) - (hostility ?? 0f)) / 100f, 0f, 1f);
        var affinity = Math.Clamp((love ?? 0f) / 100f, 0f, 1f);
        var changed = false;
        if (familiarity > _archive.PlayerBond.Familiarity)
        {
            _archive.PlayerBond.Familiarity = familiarity;
            changed = true;
        }
        if (trust > _archive.PlayerBond.Trust)
        {
            _archive.PlayerBond.Trust = trust;
            changed = true;
        }
        if (affinity > _archive.PlayerBond.Affinity)
        {
            _archive.PlayerBond.Affinity = affinity;
            changed = true;
        }

        var interactionUtc = ReadLegacyStateDate(statePath, "saved") ??
                             ReadLegacyStateDate(Path.Combine(root, "stats.md"), "last_seen") ??
                             File.GetLastWriteTimeUtc(statePath);
        if (_archive.PlayerBond.LastInteractionUtc == null ||
            interactionUtc > _archive.PlayerBond.LastInteractionUtc.Value)
        {
            _archive.PlayerBond.LastInteractionEpisodeId = episode.Id;
            _archive.PlayerBond.LastInteractionTick = -1;
            _archive.PlayerBond.LastInteractionUtc = interactionUtc;
            changed = true;
        }
        return changed;
    }

    private static float? ReadLegacyStateFloat(string path, string key)
    {
        if (!File.Exists(path)) return null;
        var prefix = key + ":";
        var line = File.ReadLines(path).FirstOrDefault(x =>
            x.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (line == null) return null;
        var value = line[(line.IndexOf(':') + 1)..].Trim();
        return float.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? ReadLegacyStateDate(string path, string key)
    {
        if (!File.Exists(path)) return null;
        var prefix = key + ":";
        var line = File.ReadLines(path).FirstOrDefault(x =>
            x.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (line == null) return null;
        var value = line[(line.IndexOf(':') + 1)..].Trim();
        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
    }

    private static string ResolveMollyRoot(string suppliedPath)
    {
        var root = Path.GetFullPath(suppliedPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var candidates = new[]
        {
            root,
            Path.Combine(root, "masha"),
            Path.Combine(root, "Documents", "masha"),
            Path.Combine(root, "AppData", "Documents", "masha")
        };
        foreach (var candidate in candidates)
        {
            if (!Directory.Exists(candidate)) continue;
            if (File.Exists(Path.Combine(candidate, "MEMORY.md")) ||
                File.Exists(Path.Combine(candidate, "USER.md")) ||
                File.Exists(Path.Combine(candidate, "SOUL.md")) ||
                Directory.Exists(Path.Combine(candidate, "diary")))
                return candidate;
        }
        throw new InvalidDataException(
            "Expected Molly's masha directory, its Documents parent, or an Xcode .xcappdata container.");
    }

    private static bool IsMollyMemoryFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("SOUL.md", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("USER.md", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("MEMORY.md", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("thoughts.md", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("conversations.md", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("stats.md", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("state.txt", StringComparison.OrdinalIgnoreCase) ||
               (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
                Path.GetDirectoryName(path)?.EndsWith("diary", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static long GameHour(JsonElement worldStatus, long tick)
    {
        if (worldStatus.TryGetProperty("gameHour", out var value) && value.TryGetInt64(out var hour))
            return hour;
        var day = ReadNullableInt32(worldStatus, "dayLengthTicks");
        return day is > 0 ? (long)(tick * 24d / day.Value) : -1;
    }

    private static string SafeKey(string value)
    {
        var chars = value.Trim().ToLowerInvariant().Select(x =>
            char.IsLetterOrDigit(x) || x is '-' or '_' ? x : '-').ToArray();
        return new string(chars).Trim('-');
    }

    private static string Limit(string? value, int maximum)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length <= maximum ? text : text[..maximum];
    }

    private static string ReadString(JsonElement source, string name) =>
        source.ValueKind == JsonValueKind.Object && source.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? string.Empty : string.Empty;

    private static int? ReadNullableInt32(JsonElement source, string name) =>
        source.ValueKind == JsonValueKind.Object && source.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var number) ? number : null;

    private static long ReadInt64(JsonElement source, string name) =>
        source.ValueKind == JsonValueKind.Object && source.TryGetProperty(name, out var value) &&
        value.TryGetInt64(out var number) ? number : 0;

    private static float ReadSingle(JsonElement source, string name) =>
        source.ValueKind == JsonValueKind.Object && source.TryGetProperty(name, out var value) &&
        value.TryGetSingle(out var number) ? number : 0f;

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
