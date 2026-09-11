using System.Text.Json;
using HexLive.AgentHost;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

public sealed class AgentManagedMemoryTests
{
    [Test] public async Task ScopedNotesPersistAcrossReloadAndPreserveLegacyDocuments()
    {
        var root = Directory.CreateTempSubdirectory("studio-scoped-memory-");
        try
        {
            foreach (var name in new[] { "SOUL.md", "USER.md", "MEMORY.md" })
                await File.WriteAllTextAsync(Path.Combine(root.FullName, name), "Manual text: " + name + "\n");
            var store = new MashaMemoryStore(root.FullName);
            using var state = JsonDocument.Parse("{\"tick\":100,\"seed\":42}");
            var world = await store.BindHexLiveWorldAsync(state.RootElement, 901, "test-world", default);
            var decision = new CompanionDecision { MemoryUpserts =
            [
                new() { Key = "user:hobby", Value = "User enjoys sailing", Importance = .8f },
                new() { Key = "core:rescue", Value = "Remember rescuing Elsa", Importance = .8f },
                new() { Key = "self:habit", Value = "Ask before borrowing tools", Importance = .8f }
            ] };
            Assert.That(await store.CommitTurnAsync(world, "unique", "voice", decision, default), Is.True);
            var before = await File.ReadAllTextAsync(Path.Combine(root.FullName, "SOUL.md"));
            Assert.That(await store.CommitTurnAsync(world, "unique", "voice", decision, default), Is.False);
            Assert.That(await File.ReadAllTextAsync(Path.Combine(root.FullName, "SOUL.md")), Is.EqualTo(before));
            var reloaded = new MashaMemoryStore(root.FullName);
            var archive = await reloaded.SnapshotAsync(default);
            Assert.That(archive.CoreMemories.Count(x => x.Source.StartsWith("model-")), Is.EqualTo(2));
            foreach (var entry in new[] { ("USER.md", "User enjoys sailing"), ("MEMORY.md", "Remember rescuing Elsa") })
            {
                var text = await File.ReadAllTextAsync(Path.Combine(root.FullName, entry.Item1));
                Assert.That(text, Does.Contain("Manual text: " + entry.Item1));
                Assert.That(text, Does.Contain(entry.Item2));
            }
            var prompt = await reloaded.BuildPromptContextAsync(world, "sailing", default);
            Assert.That(prompt.Text, Does.Contain("User enjoys sailing"));
            Assert.That(prompt.Text, Does.Contain("Ask before borrowing tools"));
            Assert.That(archive.Worlds.Single().Memories.Single(x => x.Key == "self:habit").Source, Is.EqualTo("model"));
            Assert.That(before, Is.EqualTo("Manual text: SOUL.md\n"));
            var soulPath = Path.Combine(root.FullName, "SOUL.md");
            await File.WriteAllTextAsync(soulPath, "My revised personality\n");
            decision.MemoryUpserts[2].Value = "Return borrowed tools";
            await reloaded.CommitTurnAsync(world, "second", "heartbeat", decision, default);
            var edited = await File.ReadAllTextAsync(soulPath);
            Assert.That(edited, Is.EqualTo("My revised personality\n"));
            Assert.That((await reloaded.SnapshotAsync(default)).Worlds.Single().Memories.Single(x => x.Key == "self:habit").Value,
                Is.EqualTo("Return borrowed tools"));
            _ = new MashaMemoryStore(root.FullName);
            Assert.That(await File.ReadAllTextAsync(soulPath), Is.EqualTo(edited));
            Assert.That(Directory.EnumerateFiles(Path.Combine(root.FullName, ".history"), "*.md", SearchOption.AllDirectories), Is.Not.Empty);
        }
        finally { root.Delete(true); }
    }
}
