using HexLive.AgentCore.Studio;
using HexLive.AgentHost;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

public sealed class MemoryEditingTests
{
    [Test] public async Task EditedGeneratedMemorySurvivesRuntimeRefreshAndHistoryCanBeRestored()
    {
        var root = Directory.CreateTempSubdirectory("studio-memory-edit-");
        try
        {
            _ = new MashaMemoryStore(root.FullName);
            var documents = new WorkspaceDocuments(root.FullName);
            var initial = await documents.ReadAsync("MEMORY.md");
            var changed = await documents.SaveAsync(initial, "My editable long-term memories");
            _ = new MashaMemoryStore(root.FullName);
            Assert.That((await documents.ReadAsync("MEMORY.md")).Text, Does.Contain(changed.Text));
            var versions = await Task.WhenAll(documents.History("MEMORY.md").Select(x => documents.ReadRevisionAsync("MEMORY.md", x.Id)));
            Assert.That(versions, Does.Contain(initial.Text));
            Assert.ThrowsAsync<InvalidDataException>(() => documents.ReadRevisionAsync("MEMORY.md", "../escape"));
        }
        finally { root.Delete(true); }
    }
}
