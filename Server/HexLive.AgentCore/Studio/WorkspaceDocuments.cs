using System.Security.Cryptography;
using System.Text;
using HexLive.AgentHost;

namespace HexLive.AgentCore.Studio;

public sealed record WorkspaceDocument(string RelativePath, string Text, string Revision);
public sealed record WorkspaceRevision(string Id, DateTime ModifiedUtc)
{
    public override string ToString() => ModifiedUtc.ToLocalTime().ToString("g");
}

/// <summary>Editor and runtime writers must share this repository; private state is not editable.</summary>
public sealed class WorkspaceDocuments
{
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public WorkspaceDocuments(string root) => _root = Path.GetFullPath(root);

    // Never enumerate private state, backups or linked directories in the document browser.
    public IReadOnlyList<string> ListDocuments()
    {
        if (!Directory.Exists(_root)) return Array.Empty<string>();
        if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("LinkedWorkspaceNotAllowed");
        var result = new List<string>();
        void Visit(string directory, int depth)
        {
            if (depth > 8 || result.Count >= 512) return;
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).OrderBy(x => x, StringComparer.Ordinal))
            {
                if (result.Count >= 512) break;
                if (Path.GetFileName(path).StartsWith('.')) continue;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) Visit(path, depth + 1);
                else if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    result.Add(Path.GetRelativePath(_root, path).Replace('\\', '/'));
            }
        }
        Visit(_root, 0);
        return result.OrderBy(x => x == "MEMORY.md" ? 0 : 1).ThenBy(x => x, StringComparer.Ordinal).ToArray();
    }

    public async Task<WorkspaceDocument> ReadAsync(string relativePath, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { return await ReadUnlockedAsync(relativePath, token); }
        finally { _gate.Release(); }
    }

    public async Task<WorkspaceDocument> SaveAsync(WorkspaceDocument original, string text,
        CancellationToken token = default)
    {
        text = MemoryDocumentEdits.ForEditor(original.RelativePath, MemoryDocumentEdits.NormalizeEdit(original.Text, text));
        MemoryDocumentEdits.Validate(original.RelativePath, text);
        if (Encoding.UTF8.GetByteCount(text) > 1024 * 1024) throw new InvalidDataException("DocumentTooLarge");
        await _gate.WaitAsync(token);
        try
        {
            using var lease = new FileStream(Path.Combine(_root, ".documents.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var current = await ReadUnlockedAsync(original.RelativePath, token);
            if (current.Revision != original.Revision) throw new IOException("DocumentVersionConflict");
            var path = Resolve(original.RelativePath);
            var versions = Path.Combine(_root, ".history", Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(original.RelativePath))));
            var history = Path.Combine(_root, ".history");
            if (Directory.Exists(history) && (File.GetAttributes(history) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("LinkedHistoryNotAllowed");
            if (Directory.Exists(versions) && (File.GetAttributes(versions) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("LinkedHistoryNotAllowed");
            Directory.CreateDirectory(versions);
            // A unique name prevents replacing an earlier revision.
            var backup = Path.Combine(versions, Guid.NewGuid().ToString("N") + ".md");
            await File.WriteAllTextAsync(backup,
                current.Text, token);
            Protect(backup);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string? pending = null;
            var committed = false;
            try
            {
                await File.WriteAllTextAsync(temporary, text, token);
                Protect(temporary);
                // Catch ordinary external edits that happened while writing the backup.
                if ((await ReadUnlockedAsync(original.RelativePath, token)).Revision != current.Revision)
                    throw new IOException("DocumentVersionConflict");
                pending = MemoryDocumentEdits.Prepare(_root, original.RelativePath, current.Text, text);
                File.Move(temporary, path, overwrite: true);
                committed = true;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                if (!committed && pending != null) File.Delete(pending);
            }
            return await ReadUnlockedAsync(original.RelativePath, token);
        }
        finally { _gate.Release(); }
    }

    private async Task<WorkspaceDocument> ReadUnlockedAsync(string relative, CancellationToken token)
    {
        var path = Resolve(relative);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("DocumentTooLarge");
        var bytes = await File.ReadAllBytesAsync(path, token);
        return new(relative, new UTF8Encoding(false, true).GetString(bytes), Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public IReadOnlyList<WorkspaceRevision> History(string relative)
    {
        _ = Resolve(relative);
        var directory = HistoryDirectory(relative);
        if (!Directory.Exists(directory)) return Array.Empty<WorkspaceRevision>();
        return Directory.EnumerateFiles(directory, "*.md").Select(path => new FileInfo(path))
            .Where(info => (info.Attributes & FileAttributes.ReparsePoint) == 0 && Guid.TryParseExact(Path.GetFileNameWithoutExtension(info.Name), "N", out _))
            .OrderByDescending(info => info.LastWriteTimeUtc).Take(32)
            .Select(info => new WorkspaceRevision(Path.GetFileNameWithoutExtension(info.Name), info.LastWriteTimeUtc)).ToArray();
    }
    public async Task<string> ReadRevisionAsync(string relative, string revisionId, CancellationToken token = default)
    {
        _ = Resolve(relative);
        if (!Guid.TryParseExact(revisionId, "N", out _)) throw new InvalidDataException("InvalidRevision");
        var path = Path.Combine(HistoryDirectory(relative), revisionId + ".md");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 1024 * 1024)
            throw new InvalidDataException("InvalidRevision");
        return new UTF8Encoding(false, true).GetString(await File.ReadAllBytesAsync(path, token));
    }
    private string HistoryDirectory(string relative)
    {
        var history = Path.Combine(_root, ".history");
        var directory = Path.Combine(history, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative))));
        foreach (var path in new[] { history, directory })
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("LinkedHistoryNotAllowed");
        return directory;
    }

    private string Resolve(string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (Path.IsPathRooted(relative) || parts.Any(p => p.Length == 0 || p.StartsWith('.') || p.Contains(':')) ||
            !relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("DocumentPathNotAllowed");
        var path = _root;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("LinkedWorkspaceNotAllowed");
        foreach (var part in parts)
        {
            path = Path.Combine(path, part);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("LinkedDocumentNotAllowed");
        }
        return path;
    }
    private static void Protect(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
