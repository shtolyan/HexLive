using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HexLive.Server.Assets
{

/// <summary>
/// §152 persistent live registry. Records are mutable pointers; blobs and
/// history are immutable. Every public lookup starts from validated identities.
/// </summary>
public sealed class AssetRegistryStore
{
    private sealed class RegistryState
    {
        public long RegistryRevision { get; set; }
    }

    private sealed class PublishTransaction
    {
        public ContentObjectRecord Record { get; set; } = new();
        public RegistryChange Change { get; set; } = new();
        public long RegistryRevision { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly ConcurrentDictionary<string, (long Size, long Mtime)> _verifiedBlobs =
        new(StringComparer.Ordinal);
    private readonly string _root;
    private readonly string _blobs;
    private readonly string _records;
    private readonly string _history;
    private readonly string _changes;
    private readonly string _staging;
    private readonly string _statePath;
    private readonly string _lockPath;

    public AssetRegistryStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("Asset root must not be empty.", nameof(root));
        }

        _root = Path.GetFullPath(root);
        _blobs = Path.Combine(_root, "blobs");
        _records = Path.Combine(_root, "records");
        _history = Path.Combine(_root, "history");
        _changes = Path.Combine(_root, "changes");
        _staging = Path.Combine(_root, "staging");
        _statePath = Path.Combine(_root, "registry-state.json");
        _lockPath = Path.Combine(_root, ".publish.lock");

        Directory.CreateDirectory(_blobs);
        Directory.CreateDirectory(_records);
        Directory.CreateDirectory(_history);
        Directory.CreateDirectory(_changes);
        Directory.CreateDirectory(_staging);
        RecoverTransactions();
    }

    public string RootPath => _root;
    public string StagingPath => _staging;

    public long RegistryRevision => ReadRegistryRevision();

    public static ContentPublishCandidate ReadCandidateFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Candidate path must not be empty.", nameof(path));
        }

        return ReadJson<ContentPublishCandidate>(Path.GetFullPath(path));
    }

    public async Task<ContentPublishResult> PublishAsync(
        ContentPublishCandidate candidate,
        bool retainCurrentVariants = false,
        CancellationToken cancellationToken = default)
    {
        ValidateCandidate(candidate);
        VerifyCandidates(candidate);

        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(cancellationToken).ConfigureAwait(false);

            // Recheck after the cross-process lock: staging bytes are part of
            // the transaction, not a promise made before another publisher ran.
            VerifyCandidates(candidate);
            var current = ReadCurrent(candidate.Type, candidate.Id);
            candidate.Metadata = PreserveServerMetadata(current, candidate.Metadata);
            var proposedVariants = candidate.Variants
                .Select(ToRecordVariant)
                .ToList();
            if (candidate.State == "retired" && current is not null &&
                proposedVariants.Count == 0)
            {
                // §154.4: retirement is a catalogue state, not deletion. Keep
                // immutable payload pointers so old saves can still resolve the
                // item and reactivation does not require a re-upload.
                proposedVariants.AddRange(current.Variants.Select(CloneVariant));
            }
            if (retainCurrentVariants && current is not null &&
                current.State == "active" && candidate.State == "active")
            {
                var incoming = proposedVariants.Select(VariantKey)
                    .ToHashSet(StringComparer.Ordinal);
                var retained = current.Variants
                    .Where(value => !incoming.Contains(VariantKey(value)))
                    .ToArray();
                if (retained.Length != 0 && !JsonEquals(current.Metadata, candidate.Metadata))
                {
                    throw new InvalidDataException(
                        "Cannot retain platform variants while changing object metadata; " +
                        "publish all platforms for this semantic update.");
                }
                foreach (var variant in retained)
                {
                    VerifyRetainedVariant(candidate.Type, candidate.Id, variant);
                    proposedVariants.Add(CloneVariant(variant));
                }
            }
            proposedVariants = proposedVariants
                .OrderBy(value => value.Platform, StringComparer.Ordinal)
                .ThenBy(value => value.RuntimeProfile, StringComparer.Ordinal)
                .ToList();

            if (current is not null && SamePublishedValue(current, candidate, proposedVariants))
            {
                return new ContentPublishResult
                {
                    Changed = false,
                    RegistryRevision = ReadRegistryRevision(),
                    Record = current,
                };
            }

            foreach (var variant in candidate.Variants)
            {
                InstallBlob(variant.Sha256, variant.Size, variant.StagedPath);
                foreach (var attachment in variant.Attachments)
                {
                    InstallBlob(attachment.Sha256, attachment.Size, attachment.StagedPath);
                }
            }

            var now = DateTimeOffset.UtcNow;
            var objectRevision = NextObjectRevision(candidate.Type, candidate.Id, current);
            var record = new ContentObjectRecord
            {
                Type = candidate.Type,
                Id = candidate.Id,
                Revision = objectRevision,
                State = candidate.State,
                Metadata = CloneMetadata(candidate.Metadata),
                Variants = proposedVariants,
                PublishedAtUtc = now,
            };

            var registryRevision = checked(ReadRegistryRevision() + 1);
            var change = new RegistryChange
            {
                RegistryRevision = registryRevision,
                Type = record.Type,
                Id = record.Id,
                ObjectRevision = record.Revision,
                State = record.State,
                PublishedAtUtc = now,
            };

            // Persist an fsync'd intent before touching any public pointer.
            // Startup recovery can finish this exact one-object transaction if
            // the process dies between record, change and cursor writes.
            var transactionPath = Path.Combine(
                _staging, ".transaction-" + Guid.NewGuid().ToString("N") + ".json");
            WriteJsonNew(transactionPath, new PublishTransaction
            {
                Record = record,
                Change = change,
                RegistryRevision = registryRevision,
            });
            try
            {
                ApplyTransaction(record, change, registryRevision);
                File.Delete(transactionPath);
            }
            catch
            {
                // Leave the intent for deterministic recovery.
                throw;
            }

            return new ContentPublishResult
            {
                Changed = true,
                RegistryRevision = registryRevision,
                Record = record,
            };
        }
        finally
        {
            _publishGate.Release();
        }
    }

    public AssetIndexResponse GetIndex(
        string platform, string runtimeProfile, long? afterRegistryRevision = null)
    {
        ValidateVariantSelection(platform, runtimeProfile);
        var currentRevision = ReadRegistryRevision();
        if (afterRegistryRevision < 0 || afterRegistryRevision > currentRevision)
        {
            throw new ArgumentOutOfRangeException(
                nameof(afterRegistryRevision),
                $"Registry cursor must be between 0 and {currentRevision}.");
        }

        IEnumerable<(string Type, string Id)> keys;
        if (afterRegistryRevision.HasValue)
        {
            keys = ReadChangesAfter(afterRegistryRevision.Value, currentRevision)
                .Select(value => (value.Type, value.Id))
                .Distinct();
        }
        else
        {
            keys = EnumerateCurrentKeys();
        }

        var response = new AssetIndexResponse
        {
            RegistryRevision = currentRevision,
            IsDelta = afterRegistryRevision.HasValue,
        };
        foreach (var key in keys.OrderBy(value => value.Type, StringComparer.Ordinal)
                     .ThenBy(value => value.Id, StringComparer.Ordinal))
        {
            var current = ReadCurrent(key.Type, key.Id);
            if (current is null)
            {
                continue;
            }

            var resolved = ResolveRecord(current, platform, runtimeProfile);
            if (resolved is not null)
            {
                response.Objects.Add(resolved);
            }
            else
            {
                // An active object with no payload for this client is a real
                // publication gap, not an empty slot. Say so, or a Windows
                // Player reads a short index as a complete one.
                response.PlatformMissing.Add(
                    new AssetObjectKey { Type = key.Type, Id = key.Id });
            }
        }

        return response;
    }

    /// <summary>§154: current records for the authenticated admin/catalogue layer.</summary>
    public IReadOnlyList<ContentObjectRecord> ReadCurrentRecords(string? type = null) =>
        ReadSnapshot(type).Records;

    /// <summary>
    /// Reads the registry cursor and all current pointers under the same
    /// cross-process lock as publish. Without this, a materialiser can observe
    /// the new record just before ApplyTransaction advances registry-state and
    /// incorrectly label new content with the old revision.
    /// </summary>
    public AssetRegistrySnapshot ReadSnapshot(string? type = null)
    {
        if (type is not null && !ContentIdentity.IsType(type))
        {
            throw new ArgumentException("Invalid content type.", nameof(type));
        }

        using var fileLock = AcquireFileLockSynchronously();
        var records = EnumerateCurrentKeys()
            .Where(key => type is null || string.Equals(key.Type, type, StringComparison.Ordinal))
            .Select(key => ReadCurrent(key.Type, key.Id))
            .Where(value => value is not null)
            .Cast<ContentObjectRecord>()
            .OrderBy(value => value.Type, StringComparer.Ordinal)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        return new AssetRegistrySnapshot
        {
            RegistryRevision = ReadRegistryRevision(),
            Records = records,
        };
    }

    public ContentObjectRecord? ReadCurrentRecord(string type, string id)
    {
        ValidateIdentity(type, id);
        return ReadCurrent(type, id);
    }

    /// <summary>
    /// §154.4 metadata/state transaction used by the password-protected admin.
    /// Payload bytes and variant pointers are retained verbatim; an optimistic
    /// revision check prevents two operators/publishers from losing an update.
    /// </summary>
    public async Task<ContentPublishResult> UpdateRecordAsync(
        string type,
        string id,
        long expectedRevision,
        string state,
        Dictionary<string, JsonElement> metadata,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(type, id);
        if (state is not ("active" or "retired"))
        {
            throw new ArgumentException("Content state must be active or retired.", nameof(state));
        }
        if (expectedRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }
        metadata ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(cancellationToken).ConfigureAwait(false);
            var current = ReadCurrent(type, id)
                ?? throw new InvalidOperationException($"Content object {type}/{id} does not exist.");
            if (current.Revision != expectedRevision)
            {
                throw new InvalidOperationException(
                    $"Content object changed from revision {expectedRevision} to {current.Revision}; reload it before saving.");
            }

            var proposedMetadata = CloneMetadata(metadata);
            if (current.State == state && JsonEquals(current.Metadata, proposedMetadata))
            {
                return new ContentPublishResult
                {
                    Changed = false,
                    RegistryRevision = ReadRegistryRevision(),
                    Record = current,
                };
            }

            var variants = current.Variants.Select(CloneVariant).ToList();
            if (state == "active" && variants.Count == 0)
            {
                var lastActive = ReadHistory(type, id)
                    .Where(value => value.State == "active" && value.Variants.Count > 0)
                    .OrderByDescending(value => value.Revision)
                    .FirstOrDefault();
                if (lastActive is null)
                {
                    throw new InvalidOperationException(
                        $"Cannot reactivate {type}/{id}: no published payload exists in history.");
                }
                variants.AddRange(lastActive.Variants.Select(CloneVariant));
            }
            foreach (var variant in variants)
            {
                VerifyRetainedVariant(type, id, variant);
            }

            var now = DateTimeOffset.UtcNow;
            var record = new ContentObjectRecord
            {
                Type = type,
                Id = id,
                Revision = NextObjectRevision(type, id, current),
                State = state,
                Metadata = proposedMetadata,
                Variants = variants,
                PublishedAtUtc = now,
            };
            var registryRevision = checked(ReadRegistryRevision() + 1);
            var change = new RegistryChange
            {
                RegistryRevision = registryRevision,
                Type = type,
                Id = id,
                ObjectRevision = record.Revision,
                State = state,
                PublishedAtUtc = now,
            };
            var transactionPath = Path.Combine(
                _staging, ".transaction-" + Guid.NewGuid().ToString("N") + ".json");
            WriteJsonNew(transactionPath, new PublishTransaction
            {
                Record = record,
                Change = change,
                RegistryRevision = registryRevision,
            });
            // On failure the intent deliberately remains for startup recovery.
            ApplyTransaction(record, change, registryRevision);
            File.Delete(transactionPath);

            return new ContentPublishResult
            {
                Changed = true,
                RegistryRevision = registryRevision,
                Record = record,
            };
        }
        finally
        {
            _publishGate.Release();
        }
    }

    /// <summary>
    /// §152.4: which platform/profile pairs the registry publishes for, and
    /// which active objects each one cannot load. This is the diagnostic the
    /// operator needs before believing a platform's content is complete.
    ///
    /// The pairs come from the CURRENT records, i.e. what publication targets
    /// today. Coverage inside a pair still honours the §152.1 history fallback,
    /// but a profile that exists only in history is not a pair of its own —
    /// per-request <see cref="GetIndex"/> answers for such a client.
    /// </summary>
    public AssetCoverageReport AuditCoverage() => AuditCoverage(ReadSnapshot());

    public AssetCoverageReport AuditCoverage(AssetRegistrySnapshot snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        var report = new AssetCoverageReport { RegistryRevision = snapshot.RegistryRevision };
        var active = new List<ContentObjectRecord>();
        foreach (var current in snapshot.Records)
        {
            if (string.Equals(current.State, "retired", StringComparison.Ordinal))
            {
                report.RetiredObjects++;
            }
            else
            {
                active.Add(current);
            }
        }

        report.ActiveObjects = active.Count;
        var pairs = active
            .SelectMany(record => record.Variants)
            .Select(variant => (variant.Platform, variant.RuntimeProfile))
            .Distinct()
            .OrderBy(pair => pair.Platform, StringComparer.Ordinal)
            .ThenBy(pair => pair.RuntimeProfile, StringComparer.Ordinal);

        foreach (var (platform, runtimeProfile) in pairs)
        {
            var coverage = new AssetPlatformCoverage
            {
                Platform = platform,
                RuntimeProfile = runtimeProfile,
            };
            foreach (var record in active
                         .OrderBy(value => value.Type, StringComparer.Ordinal)
                         .ThenBy(value => value.Id, StringComparer.Ordinal))
            {
                if (ResolveRecord(record, platform, runtimeProfile) is not null)
                {
                    coverage.Covered++;
                }
                else
                {
                    coverage.Missing.Add(
                        new AssetObjectKey { Type = record.Type, Id = record.Id });
                }
            }

            report.Platforms.Add(coverage);
        }

        return report;
    }

    public AssetResolvedObject? Resolve(
        string type, string id, string platform, string runtimeProfile)
    {
        ValidateIdentity(type, id);
        ValidateVariantSelection(platform, runtimeProfile);
        var current = ReadCurrent(type, id);
        return current is null ? null : ResolveRecord(current, platform, runtimeProfile);
    }

    /// <summary>
    /// Variant selection for an already-read current record. Null means this
    /// platform/profile has nothing to load for the object — the caller decides
    /// whether that is a miss to report or an object to skip.
    /// </summary>
    private AssetResolvedObject? ResolveRecord(
        ContentObjectRecord current, string platform, string runtimeProfile)
    {
        var type = current.Type;
        var id = current.Id;
        if (string.Equals(current.State, "retired", StringComparison.Ordinal))
        {
            return ToResolved(current, null);
        }

        var selected = FindVariant(current, platform, runtimeProfile);
        if (selected is not null)
        {
            return ToResolved(current, selected);
        }

        // A profile migration must not turn a still-valid old Player into a
        // content-release client. Find the newest history revision that has a
        // physically compatible payload (§152.1).
        foreach (var historical in ReadHistory(type, id).OrderByDescending(value => value.Revision))
        {
            if (historical.Revision >= current.Revision ||
                !string.Equals(historical.State, "active", StringComparison.Ordinal))
            {
                continue;
            }

            selected = FindVariant(historical, platform, runtimeProfile);
            if (selected is not null)
            {
                return ToResolved(historical, selected);
            }
        }

        return null;
    }

    public AssetResolveResponse ResolveMany(AssetResolveRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ValidateVariantSelection(request.Platform, request.RuntimeProfile);
        if (request.Objects is null || request.Objects.Count > 2048)
        {
            throw new ArgumentException("Resolve accepts at most 2048 object ids.", nameof(request));
        }

        var response = new AssetResolveResponse { RegistryRevision = ReadRegistryRevision() };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in request.Objects)
        {
            ValidateIdentity(key.Type, key.Id);
            if (!seen.Add(key.Type + "/" + key.Id))
            {
                continue;
            }

            var resolved = ResolveRequired(
                key.Type, key.Id, request.Platform, request.RuntimeProfile);
            if (resolved is null)
            {
                response.Missing.Add(new AssetObjectKey { Type = key.Type, Id = key.Id });
            }
            else
            {
                response.Objects.Add(resolved);
            }
        }

        return response;
    }

    /// <summary>
    /// §154.2: resolve is a current-world request, not catalogue discovery.
    /// A retired id may still be worn or stored in a save, so return its last
    /// compatible immutable payload as legacy without making it active again.
    /// </summary>
    private AssetResolvedObject? ResolveRequired(
        string type, string id, string platform, string runtimeProfile)
    {
        ValidateIdentity(type, id);
        ValidateVariantSelection(platform, runtimeProfile);
        var current = ReadCurrent(type, id);
        if (current is null || current.State != "retired")
        {
            return current is null ? null : ResolveRecord(current, platform, runtimeProfile);
        }

        var selected = FindVariant(current, platform, runtimeProfile);
        if (selected is null)
        {
            selected = ReadHistory(type, id)
                .Where(value => value.State == "active")
                .OrderByDescending(value => value.Revision)
                .Select(value => FindVariant(value, platform, runtimeProfile))
                .FirstOrDefault(value => value is not null);
        }
        return selected is null ? null : ToResolved(current, selected, "legacy");
    }

    public IReadOnlyList<ContentObjectRecord> ReadHistory(string type, string id)
    {
        ValidateIdentity(type, id);
        var directory = HistoryDirectory(type, id);
        if (!Directory.Exists(directory))
        {
            return Array.Empty<ContentObjectRecord>();
        }

        return Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(ReadJson<ContentObjectRecord>)
            .OrderBy(value => value.Revision)
            .ToArray();
    }

    public bool TryGetVerifiedBlob(string sha256, out string path, out long size)
    {
        path = string.Empty;
        size = 0;
        if (!ContentIdentity.IsSha256(sha256))
        {
            return false;
        }

        var candidate = Path.Combine(_blobs, sha256);
        if (!File.Exists(candidate))
        {
            return false;
        }

        var info = new FileInfo(candidate);
        var stamp = (info.Length, info.LastWriteTimeUtc.Ticks);
        if (!_verifiedBlobs.TryGetValue(sha256, out var verified) || verified != stamp)
        {
            if (!string.Equals(HashFile(candidate), sha256, StringComparison.Ordinal))
            {
                return false;
            }

            _verifiedBlobs[sha256] = stamp;
        }

        path = candidate;
        size = info.Length;
        return true;
    }

    private static AssetResolvedObject ToResolved(
        ContentObjectRecord record, ContentObjectVariant? variant, string? state = null) => new()
    {
        Type = record.Type,
        Id = record.Id,
        Revision = record.Revision,
        State = state ?? record.State,
        Metadata = CloneMetadata(record.Metadata),
        Variant = variant,
    };

    private static ContentObjectVariant? FindVariant(
        ContentObjectRecord record, string platform, string runtimeProfile) =>
        record.Variants.FirstOrDefault(value =>
            string.Equals(value.Platform, platform, StringComparison.Ordinal) &&
            string.Equals(value.RuntimeProfile, runtimeProfile, StringComparison.Ordinal));

    private IEnumerable<(string Type, string Id)> EnumerateCurrentKeys()
    {
        foreach (var typeDirectory in Directory.EnumerateDirectories(_records))
        {
            var type = Path.GetFileName(typeDirectory);
            if (!ContentIdentity.IsType(type))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(typeDirectory, "*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (ContentIdentity.IsId(id))
                {
                    yield return (type, id);
                }
            }
        }
    }

    private IEnumerable<RegistryChange> ReadChangesAfter(long after, long current)
    {
        for (var revision = after + 1; revision <= current; revision++)
        {
            var path = ChangePath(revision);
            if (!File.Exists(path))
            {
                throw new InvalidDataException(
                    $"Registry journal is missing change {revision}; refusing an incomplete delta.");
            }

            yield return ReadJson<RegistryChange>(path);
        }
    }

    private long ReadRegistryRevision()
    {
        var stateRevision = File.Exists(_statePath)
            ? ReadJson<RegistryState>(_statePath).RegistryRevision
            : 0;
        var journalRevision = Directory.EnumerateFiles(_changes, "*.json")
            .Select(path => long.TryParse(Path.GetFileNameWithoutExtension(path), out var value) ? value : 0)
            .DefaultIfEmpty(0)
            .Max();
        return Math.Max(stateRevision, journalRevision);
    }

    private ContentObjectRecord? ReadCurrent(string type, string id)
    {
        var path = RecordPath(type, id);
        return File.Exists(path) ? ReadJson<ContentObjectRecord>(path) : null;
    }

    private long NextObjectRevision(string type, string id, ContentObjectRecord? current)
    {
        var highest = current?.Revision ?? 0;
        var directory = HistoryDirectory(type, id);
        if (Directory.Exists(directory))
        {
            highest = Directory.EnumerateFiles(directory, "*.json")
                .Select(path => long.TryParse(Path.GetFileNameWithoutExtension(path), out var value) ? value : 0)
                .Append(highest)
                .Max();
        }

        return checked(highest + 1);
    }

    private void InstallBlob(string sha256, long size, string stagedPath)
    {
        var destination = Path.Combine(_blobs, sha256);
        if (File.Exists(destination))
        {
            if (new FileInfo(destination).Length != size ||
                !string.Equals(HashFile(destination), sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Immutable blob {sha256} exists with different bytes.");
            }

            return;
        }

        var temporary = Path.Combine(_blobs, "." + sha256 + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.Copy(stagedPath, temporary, overwrite: false);
            if (new FileInfo(temporary).Length != size ||
                !string.Equals(HashFile(temporary), sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Blob {sha256} changed while it was staged.");
            }

            File.Move(temporary, destination, overwrite: false);
            _verifiedBlobs[sha256] =
                (size, new FileInfo(destination).LastWriteTimeUtc.Ticks);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private void RecoverTransactions()
    {
        using var fileLock = AcquireFileLockSynchronously();
        foreach (var path in Directory.EnumerateFiles(
                     _staging, ".transaction-*.json", SearchOption.TopDirectoryOnly)
                 .OrderBy(value => value, StringComparer.Ordinal))
        {
            var transaction = ReadJson<PublishTransaction>(path);
            ValidateIdentity(transaction.Record.Type, transaction.Record.Id);
            if (transaction.RegistryRevision <= 0 ||
                transaction.Change.RegistryRevision != transaction.RegistryRevision ||
                transaction.Change.Type != transaction.Record.Type ||
                transaction.Change.Id != transaction.Record.Id ||
                transaction.Change.ObjectRevision != transaction.Record.Revision)
            {
                throw new InvalidDataException($"Invalid publish transaction '{path}'.");
            }

            ApplyTransaction(
                transaction.Record, transaction.Change, transaction.RegistryRevision);
            File.Delete(path);
        }
    }

    private void ApplyTransaction(
        ContentObjectRecord record, RegistryChange change, long registryRevision)
    {
        var historyPath = HistoryPath(record.Type, record.Id, record.Revision);
        if (!File.Exists(historyPath))
        {
            WriteJsonNew(historyPath, record);
        }

        var current = ReadCurrent(record.Type, record.Id);
        if (current is null || current.Revision <= record.Revision)
        {
            WriteJsonAtomic(RecordPath(record.Type, record.Id), record);
        }

        var changePath = ChangePath(registryRevision);
        if (!File.Exists(changePath))
        {
            WriteJsonNew(changePath, change);
        }

        var cursor = Math.Max(ReadRegistryRevision(), registryRevision);
        WriteJsonAtomic(_statePath, new RegistryState { RegistryRevision = cursor });
    }

    private FileStream AcquireFileLockSynchronously()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                return new FileStream(
                    _lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    private void VerifyCandidates(ContentPublishCandidate candidate)
    {
        foreach (var variant in candidate.Variants)
        {
            VerifyStagedBlob(
                candidate.Type, candidate.Id, variant.Platform + "/" + variant.RuntimeProfile,
                variant.StagedPath, variant.Sha256, variant.Size);
            foreach (var attachment in variant.Attachments)
            {
                VerifyStagedBlob(
                    candidate.Type, candidate.Id,
                    variant.Platform + "/" + variant.RuntimeProfile + "/" + attachment.Name,
                    attachment.StagedPath, attachment.Sha256, attachment.Size);
            }
        }
    }

    private void VerifyStagedBlob(
        string type, string id, string label, string stagedPath, string sha256, long expectedSize)
    {
        var path = Path.GetFullPath(stagedPath);
        if (!IsUnder(path, _staging) || !File.Exists(path))
        {
            throw new InvalidDataException(
                $"Candidate for {type}/{id} must be a file under staging.");
        }

        var size = new FileInfo(path).Length;
        if (size != expectedSize || !string.Equals(HashFile(path), sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Candidate {label} fails size or SHA-256 validation.");
        }
    }

    private static void ValidateCandidate(ContentPublishCandidate candidate)
    {
        if (candidate is null)
        {
            throw new ArgumentNullException(nameof(candidate));
        }

        ValidateIdentity(candidate.Type, candidate.Id);
        if (candidate.State is not ("active" or "retired"))
        {
            throw new ArgumentException("Content state must be active or retired.", nameof(candidate));
        }

        candidate.Metadata ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        candidate.Variants ??= new List<ContentPublishVariant>();
        if (candidate.State == "active" && candidate.Variants.Count == 0)
        {
            throw new ArgumentException("An active content object needs at least one variant.", nameof(candidate));
        }

        if (candidate.State == "retired" && candidate.Variants.Count != 0)
        {
            throw new ArgumentException("A retired content object must not publish payload variants.", nameof(candidate));
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variant in candidate.Variants)
        {
            variant.Attachments ??= new List<ContentPublishAttachment>();
            ValidateVariantSelection(variant.Platform, variant.RuntimeProfile);
            if (!keys.Add(variant.Platform + "\n" + variant.RuntimeProfile))
            {
                throw new ArgumentException("Candidate contains duplicate platform/profile variants.", nameof(candidate));
            }

            if (!ContentIdentity.IsSha256(variant.Sha256) || variant.Size < 0)
            {
                throw new ArgumentException("Candidate has an invalid SHA-256 or size.", nameof(candidate));
            }

            if (variant.PayloadType is not ("assetBundle" or "file"))
            {
                throw new ArgumentException("Payload type must be assetBundle or file.", nameof(candidate));
            }

            if (variant.PayloadType == "assetBundle" && variant.EntryAsset != "main")
            {
                throw new ArgumentException("Every AssetBundle must expose the root asset as 'main'.", nameof(candidate));
            }

            if (variant.IconAsset is not (null or "icon"))
            {
                throw new ArgumentException(
                    "An icon must be the 'icon' entry in its owning bundle.", nameof(candidate));
            }

            var attachmentNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var attachment in variant.Attachments)
            {
                if (!ContentIdentity.IsVariantName(attachment.Name) ||
                    !attachmentNames.Add(attachment.Name))
                {
                    throw new ArgumentException(
                        "Attachment names must be safe and unique within a variant.", nameof(candidate));
                }
                if (!ContentIdentity.IsSha256(attachment.Sha256) || attachment.Size < 0)
                {
                    throw new ArgumentException(
                        "Candidate attachment has an invalid SHA-256 or size.", nameof(candidate));
                }
            }
        }
    }

    private static void ValidateIdentity(string type, string id)
    {
        if (!ContentIdentity.IsType(type) || !ContentIdentity.IsId(id))
        {
            throw new ArgumentException("Invalid content type/id.");
        }
    }

    private static void ValidateVariantSelection(string platform, string runtimeProfile)
    {
        if (!ContentIdentity.IsVariantName(platform) || !ContentIdentity.IsVariantName(runtimeProfile))
        {
            throw new ArgumentException("Invalid platform/runtime profile.");
        }
    }

    private static ContentObjectVariant ToRecordVariant(ContentPublishVariant value) => new()
    {
        Platform = value.Platform,
        RuntimeProfile = value.RuntimeProfile,
        Sha256 = value.Sha256,
        Size = value.Size,
        PayloadType = value.PayloadType,
        EntryAsset = value.EntryAsset,
        IconAsset = value.IconAsset,
        Attachments = value.Attachments.Select(attachment => new ContentObjectAttachment
        {
            Name = attachment.Name,
            Sha256 = attachment.Sha256,
            Size = attachment.Size,
        }).OrderBy(attachment => attachment.Name, StringComparer.Ordinal).ToList(),
    };

    private static string VariantKey(ContentObjectVariant value) =>
        value.Platform + "\n" + value.RuntimeProfile;

    private void VerifyRetainedVariant(string type, string id, ContentObjectVariant variant)
    {
        if (!TryGetVerifiedBlob(variant.Sha256, out _, out var size) || size != variant.Size)
        {
            throw new InvalidDataException(
                $"Cannot retain missing or corrupt blob for {type}/{id} " +
                $"{variant.Platform}/{variant.RuntimeProfile}.");
        }
        foreach (var attachment in variant.Attachments)
        {
            if (!TryGetVerifiedBlob(attachment.Sha256, out _, out var attachmentSize) ||
                attachmentSize != attachment.Size)
            {
                throw new InvalidDataException(
                    $"Cannot retain missing or corrupt attachment '{attachment.Name}' " +
                    $"for {type}/{id} {variant.Platform}/{variant.RuntimeProfile}.");
            }
        }
    }

    private static ContentObjectVariant CloneVariant(ContentObjectVariant value) => new()
    {
        Platform = value.Platform,
        RuntimeProfile = value.RuntimeProfile,
        Sha256 = value.Sha256,
        Size = value.Size,
        PayloadType = value.PayloadType,
        EntryAsset = value.EntryAsset,
        IconAsset = value.IconAsset,
        Attachments = value.Attachments.Select(attachment => new ContentObjectAttachment
        {
            Name = attachment.Name,
            Sha256 = attachment.Sha256,
            Size = attachment.Size,
        }).ToList(),
    };

    private static bool SamePublishedValue(
        ContentObjectRecord current,
        ContentPublishCandidate candidate,
        IReadOnlyList<ContentObjectVariant> proposedVariants)
    {
        if (!string.Equals(current.State, candidate.State, StringComparison.Ordinal) ||
            !JsonEquals(current.Metadata, candidate.Metadata) ||
            current.Variants.Count != proposedVariants.Count)
        {
            return false;
        }

        var currentVariants = current.Variants
            .OrderBy(value => value.Platform, StringComparer.Ordinal)
            .ThenBy(value => value.RuntimeProfile, StringComparer.Ordinal)
            .ToArray();
        for (var i = 0; i < currentVariants.Length; i++)
        {
            var left = currentVariants[i];
            var right = proposedVariants[i];
            if (left.Platform != right.Platform || left.RuntimeProfile != right.RuntimeProfile ||
                left.Sha256 != right.Sha256 || left.Size != right.Size ||
                left.PayloadType != right.PayloadType || left.EntryAsset != right.EntryAsset ||
                left.IconAsset != right.IconAsset ||
                !SameAttachments(left.Attachments, right.Attachments))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameAttachments(
        IReadOnlyList<ContentObjectAttachment> left,
        IReadOnlyList<ContentObjectAttachment> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }
        for (var i = 0; i < left.Count; i++)
        {
            if (left[i].Name != right[i].Name || left[i].Sha256 != right[i].Sha256 ||
                left[i].Size != right[i].Size)
            {
                return false;
            }
        }
        return true;
    }

    private static bool JsonEquals(
        Dictionary<string, JsonElement> left, Dictionary<string, JsonElement> right) =>
        JsonSerializer.Serialize(left, JsonOptions) == JsonSerializer.Serialize(right, JsonOptions);

    private static Dictionary<string, JsonElement> CloneMetadata(
        Dictionary<string, JsonElement> source) => source.ToDictionary(
        pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);

    private static Dictionary<string, JsonElement> PreserveServerMetadata(
        ContentObjectRecord? current,
        Dictionary<string, JsonElement> incoming)
    {
        var merged = CloneMetadata(incoming);
        if (current is not null &&
            !merged.ContainsKey("simulation") &&
            current.Metadata.TryGetValue("simulation", out var simulation))
        {
            // §154.1: Unity/content.py owns visual metadata, the admin owns the
            // simulation block. Rebuilding a mesh may not reset gameplay tuning.
            merged["simulation"] = simulation.Clone();
        }

        return merged;
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    _lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private string RecordPath(string type, string id) =>
        Path.Combine(_records, type, id + ".json");
    private string HistoryDirectory(string type, string id) =>
        Path.Combine(_history, type, id);
    private string HistoryPath(string type, string id, long revision) =>
        Path.Combine(HistoryDirectory(type, id), revision + ".json");
    private string ChangePath(long revision) => Path.Combine(_changes, revision + ".json");

    private static bool IsUnder(string path, string root)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static T ReadJson<T>(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"JSON file '{path}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"JSON file '{path}' is invalid.", ex);
        }
    }

    private static void WriteJsonNew<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}

}
