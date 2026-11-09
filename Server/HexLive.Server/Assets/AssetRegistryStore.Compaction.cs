using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HexLive.Server.Assets;

public sealed class AssetCompactionResult
{
    public bool Applied { get; set; }
    public int CurrentRecords { get; set; }
    public int PromotedVariants { get; set; }
    public int HistoryRecords { get; set; }
    public int RemovedBlobs { get; set; }
    public long RemovedBlobBytes { get; set; }
    public long RegistryRevision { get; set; }
}

public sealed partial class AssetRegistryStore
{
    /// <summary>§152.4: one-time offline migration, never a public HTTP mutation.</summary>
    public AssetCompactionResult CompactStorage(bool apply = false)
    {
        _publishGate.Wait();
        try
        {
            using var fileLock = AcquireFileLockSynchronously();
            var historyRoot = Path.Combine(_root, "history");
            RejectLinks(_records);
            RejectLinks(_blobs);
            if (Directory.Exists(historyRoot)) RejectLinks(historyRoot);
            var historyFiles = Directory.Exists(historyRoot)
                ? Directory.GetFiles(historyRoot, "*.json", SearchOption.AllDirectories)
                : Array.Empty<string>();
            var history = historyFiles.Select(ReadJson<ContentObjectRecord>)
                .GroupBy(record => (record.Type, record.Id))
                .ToDictionary(group => group.Key,
                    group => group.OrderByDescending(record => record.Revision).ToArray());
            var records = EnumerateCurrentKeys().Select(key => ReadCurrent(key.Type, key.Id)!)
                .OrderBy(record => record.Type, StringComparer.Ordinal)
                .ThenBy(record => record.Id, StringComparer.Ordinal).ToArray();
            var changed = new List<ContentObjectRecord>();
            var result = new AssetCompactionResult
            {
                CurrentRecords = records.Length,
                HistoryRecords = historyFiles.Length,
                RegistryRevision = ReadRegistryRevision(),
            };
            // Build and validate the whole plan BEFORE changing any pointer.
            foreach (var current in records)
            {
                ValidateIdentity(current.Type, current.Id);
                var seen = current.Variants.Select(VariantKey).ToHashSet(StringComparer.Ordinal);
                if (history.TryGetValue((current.Type, current.Id), out var previous))
                {
                    foreach (var old in previous.Where(record => record.Revision < current.Revision &&
                                 record.State == "active"))
                    {
                        foreach (var variant in old.Variants)
                        {
                            if (!seen.Add(VariantKey(variant))) continue;
                            var retained = CloneVariant(variant);
                            var metadata = variant.Metadata ?? old.Metadata;
                            retained.Metadata = current.State == "retired" || JsonEquals(metadata, current.Metadata)
                                ? null : CloneMetadata(metadata);
                            current.Variants.Add(retained);
                            result.PromotedVariants++;
                            if (!changed.Contains(current)) changed.Add(current);
                        }
                    }
                }
                foreach (var variant in current.Variants)
                    VerifyRetainedVariant(current.Type, current.Id, variant);
            }
            var retainedHashes = ReferencedHashes(records);
            var garbage = Directory.EnumerateFiles(_blobs)
                .Where(path => ContentIdentity.IsSha256(Path.GetFileName(path)) &&
                    !retainedHashes.Contains(Path.GetFileName(path))).ToArray();
            result.RemovedBlobs = garbage.Length;
            result.RemovedBlobBytes = garbage.Sum(path => new FileInfo(path).Length);
            if (!apply) return result;

            foreach (var record in changed)
            {
                record.Revision = checked(record.Revision + 1);
                record.PublishedAtUtc = DateTimeOffset.UtcNow;
                var revision = checked(ReadRegistryRevision() + 1);
                var change = new RegistryChange
                {
                    RegistryRevision = revision, Type = record.Type, Id = record.Id,
                    ObjectRevision = record.Revision, State = record.State,
                    PublishedAtUtc = record.PublishedAtUtc,
                };
                var intent = Path.Combine(_staging, ".transaction-" + Guid.NewGuid().ToString("N") + ".json");
                WriteJsonNew(intent, new PublishTransaction
                {
                    Record = record, Change = change, RegistryRevision = revision,
                });
                ApplyTransaction(record, change, revision);
                File.Delete(intent);
            }
            // Until this point the legacy archive is intact. A retry after a
            // failed/interrupted promotion resumes from the current pointers.
            if (Directory.Exists(historyRoot)) Directory.Delete(historyRoot, recursive: true);
            CollectUnreferencedBlobs();
            result.Applied = true;
            result.RegistryRevision = ReadRegistryRevision();
            return result;
        }
        finally { _publishGate.Release(); }
    }

    public void RequireCompactedStorage()
    {
        var history = Path.Combine(_root, "history");
        if (Directory.Exists(history) && Directory.EnumerateFiles(history, "*.json", SearchOption.AllDirectories).Any())
            throw new InvalidDataException("Legacy asset history must be compacted with --compact-assets --apply-asset-compaction before serving or publishing content.");
    }

    private static HashSet<string> ReferencedHashes(IEnumerable<ContentObjectRecord> records)
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variant in records.SelectMany(record => record.Variants))
        {
            hashes.Add(variant.Sha256);
            foreach (var attachment in variant.Attachments) hashes.Add(attachment.Sha256);
        }
        return hashes;
    }

    private void CollectUnreferencedBlobs()
    {
        var records = EnumerateCurrentKeys().Select(key => ReadCurrent(key.Type, key.Id)!).ToList();
        foreach (var intent in Directory.EnumerateFiles(_staging, ".transaction-*.json"))
            records.Add(ReadJson<PublishTransaction>(intent).Record);
        var retained = ReferencedHashes(records);
        foreach (var path in Directory.EnumerateFiles(_blobs))
        {
            var sha = Path.GetFileName(path);
            if (!ContentIdentity.IsSha256(sha) || retained.Contains(sha)) continue;
            File.Delete(path);
            _verifiedBlobs.TryRemove(sha, out _);
        }
    }

    private static void RejectLinks(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Asset compaction refuses symbolic links: " + root);
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Asset compaction refuses symbolic links: " + path);
            if ((attributes & FileAttributes.Directory) != 0) RejectLinks(path);
        }
    }
}
