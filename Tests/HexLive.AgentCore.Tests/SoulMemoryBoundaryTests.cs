using System.Text.Json;
using HexLive.AgentHost;
using HexLive.AgentCore.Studio;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

public sealed class SoulMemoryBoundaryTests
{
    private string _root = "";
    private const string Note = "Свободных мест в рюкзаке достаточно; разгружать его больше не требуется.";
    private const string Personality = "# Маша\r\nЯ любопытная и независимая.\r\n\r\n";
    private static string OldSoul => Personality + MemoryDocumentEdits.Start + "\n## Собственные выводы\n- " + Note +
        "\n- Ручная заметка внутри старого раздела\n" + MemoryDocumentEdits.End + "\r\nАвторское послесловие.\r\n";

    [SetUp] public void Setup() => _root = Directory.CreateTempSubdirectory("soul-boundary-").FullName;
    [TearDown] public void Cleanup() => Directory.Delete(_root, true);
    private static Task<MashaWorldHandle> Bind(MashaMemoryStore store) => store.BindHexLiveWorldAsync(
        JsonSerializer.SerializeToElement(new { worldId = "test", seed = 42, tick = 100 }), 901, "", default);

    private void SeedLegacy()
    {
        var workspace = new MashaMemoryWorkspace(_root);
        File.WriteAllText(workspace.StatePath, JsonSerializer.Serialize(new MashaArchive {
            CoreMemories = [new() { Key = "self:рюкзак_свободен", Value = Note, Source = "model-self", Importance = 1 }]
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.WriteAllText(Path.Combine(_root, "SOUL.md"), OldSoul);
    }

    [Test] public async Task NewSelfNoteIsWorldMemoryAndCannotRewritePersonality()
    {
        var store = new MashaMemoryStore(_root);
        File.WriteAllText(Path.Combine(_root, "SOUL.md"), Personality);
        var world = await Bind(store);
        await store.CommitTurnAsync(world, "note", "heartbeat", new() {
            MemoryUpserts = [new() { Key = "self:рюкзак_свободен", Value = Note, Importance = 1 }]
        }, default);
        var state = await new MashaMemoryStore(_root).SnapshotAsync(default);
        Assert.That(state.CoreMemories.Any(x => x.Value == Note), Is.False);
        Assert.That(state.Worlds.Single().Memories.Single(x => x.Value == Note).UpdatedAtTick, Is.EqualTo(100));
        Assert.That(File.ReadAllText(Path.Combine(_root, "SOUL.md")), Is.EqualTo(Personality));
    }

    [Test] public async Task LegacySoulIsArchivedWithoutLosingAuthoredTextOrFeedingStaleFactsToPersonality()
    {
        SeedLegacy();
        var store = new MashaMemoryStore(_root);
        var world = await Bind(store);
        var prompt = await store.BuildPromptContextAsync(world, "рюкзак разгружать", default);
        Assert.That(prompt.Text, Does.Not.Contain(Note).And.Not.Contain("Ручная заметка внутри"));
        Assert.That(File.ReadAllText(Path.Combine(_root, "SOUL.md")),
            Is.EqualTo(Personality + "\r\nАвторское послесловие.\r\n"));
        var saved = Directory.GetFiles(Path.Combine(_root, "memory", "legacy-soul"), "*.md");
        Assert.That(saved, Has.Length.EqualTo(1));
        Assert.That(File.ReadAllText(saved[0]), Does.Contain(OldSoul));
        Assert.That(File.ReadAllText(Path.Combine(_root, "memory", "legacy-self.md")), Does.Contain(Note));
        Assert.That(new WorkspaceDocuments(_root).ListDocuments(), Does.Contain("memory/legacy-self.md"));
        Assert.That((await store.SnapshotAsync(default)).CoreMemories.Single(x => x.Source == "model-self").Value, Is.EqualTo(Note));
        var search = new AgentMemorySearch(_root);
        search.Refresh();
        Assert.That(search.Search("Авторское послесловие").Hits.Any(h => h.Record.Source.StartsWith("memory/legacy-soul/")), Is.False);
        await new AgentMemoryRecall(_root).DecideAsync("рюкзак разгружать", world, await store.SnapshotAsync(default),
            (context, _) => {
                Assert.That(context, Does.Not.Contain(Note).And.Not.Contain("Ручная заметка внутри"));
                return Task.FromResult(new CompanionDecision());
            }, default);
        _ = new MashaMemoryStore(_root);
        Assert.That(Directory.GetFiles(Path.Combine(_root, "memory", "legacy-soul"), "*.md"), Is.EqualTo(saved));
    }

    [Test] public async Task QueuedDeletionBeforeMigrationDoesNotResurrectLegacyNote()
    {
        SeedLegacy();
        var documents = new WorkspaceDocuments(_root);
        var doc = await documents.ReadAsync("SOUL.md");
        await documents.SaveAsync(doc, doc.Text.Replace("- " + Note + "\n", ""));
        var store = new MashaMemoryStore(_root);
        Assert.That((await store.SnapshotAsync(default)).CoreMemories.Any(x => x.Value == Note), Is.False);
        var world = await Bind(store);
        await store.CommitTurnAsync(world, "stale", "heartbeat", new() {
            MemoryUpserts = [new() { Key = "self:рюкзак_свободен", Value = Note, Importance = 1 }]
        }, default);
        Assert.That((await store.SnapshotAsync(default)).Worlds.Single().Memories.Any(x => x.Value == Note), Is.False);
    }

    [Test] public void FailedArchiveWriteLeavesOriginalSoulIntact()
    {
        SeedLegacy();
        File.WriteAllText(Path.Combine(_root, "memory", "legacy-soul"), "Blocked by an existing file");
        Assert.Throws<IOException>(() => new MashaMemoryStore(_root));
        Assert.That(File.ReadAllText(Path.Combine(_root, "SOUL.md")), Is.EqualTo(OldSoul));
    }
}
