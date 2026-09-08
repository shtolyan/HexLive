using HexLive.AgentCore.Studio;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

public sealed class WorkspaceTests
{
    [Test]
    public async Task ConflictingEditPreservesNewerContent()
    {
        var root = Directory.CreateTempSubdirectory("agent-studio-test-");
        try
        {
            var path = Path.Combine(root.FullName, "SOUL.md");
            await File.WriteAllTextAsync(path, "original");
            var store = new WorkspaceDocuments(root.FullName);
            var original = await store.ReadAsync("SOUL.md");
            await store.SaveAsync(original, "first editor");
            Assert.ThrowsAsync<IOException>(() => store.SaveAsync(original, "stale editor"));
            Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo("first editor"));
            Assert.That(Directory.GetFiles(Path.Combine(root.FullName, ".history"), "*.md",
                SearchOption.AllDirectories), Has.Length.EqualTo(1));
            Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync("../SOUL.md"));
            Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(".state/state.md"));
        }
        finally { root.Delete(recursive: true); }
    }
}
