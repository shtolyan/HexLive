using System.Text;
using System.Security.Cryptography;

namespace HexLive.AgentHost;

public sealed partial class MashaMemoryWorkspace
{
    /// <summary>§163.3: preserve the old document before removing only its managed notes.</summary>
    private void ArchiveLegacySoulNotes(string soulPath)
    {
        var files = new AgentMemoryArchive(_root);
        files.SafePath("SOUL.md");
        var original = File.ReadAllText(soulPath);
        if (!original.Contains(MemoryDocumentEdits.Start, StringComparison.Ordinal)) return;
        // Ambiguous/malformed boundaries must never delete authored text.
        MemoryDocumentEdits.Validate("SOUL.md", original);
        var from = original.IndexOf(MemoryDocumentEdits.Start, StringComparison.Ordinal);
        var end = original.IndexOf(MemoryDocumentEdits.End, StringComparison.Ordinal) + MemoryDocumentEdits.End.Length;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original)));
        var relative = "memory/legacy-soul/" + hash + ".md";
        var archived = AgentPromptFiles.Text("LegacySoulDocument") + original;
        // Both backups precede the replacement. Deterministic names make crash retries idempotent.
        Backup(relative, archived);
        Backup(".history/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("SOUL.md"))) +
            "/legacy-" + hash + ".md", original);
        if (File.ReadAllText(soulPath) != original) throw new IOException("SoulDocumentVersionConflict");
        files.Atomic("SOUL.md", original[..from] + original[end..]);

        void Backup(string path, string content)
        {
            var full = files.SafePath(path);
            if (!File.Exists(full)) files.Atomic(path, content);
            else if (File.ReadAllText(full) != content) throw new IOException("SoulArchiveVersionConflict");
        }
    }
}
