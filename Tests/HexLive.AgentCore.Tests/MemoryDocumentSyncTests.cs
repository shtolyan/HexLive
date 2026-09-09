using System.Text.Json;
using HexLive.AgentCore.Studio;
using HexLive.AgentHost;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

public sealed class MemoryDocumentSyncTests
{
    private string _root = "";
    [SetUp] public void Setup() => _root = Directory.CreateTempSubdirectory("memory-sync-373-").FullName;
    [TearDown] public void Cleanup() => Directory.Delete(_root, true);
    private static Task<MashaWorldHandle> World(MashaMemoryStore store) => store.BindHexLiveWorldAsync(
        JsonSerializer.SerializeToElement(new { worldId = "test", seed = 42, tick = 100, dayLengthTicks = 24000 }), 901, "", default);

    [Test] public void FreshAndLegacyDocumentsContainOnlyOneCopyAndKeepHistory()
    {
        _ = new MashaMemoryStore(_root);
        var user = Path.Combine(_root, "USER.md");
        Assert.That(File.ReadAllText(user).Split("# Знакомый невидимый голос").Length - 1, Is.EqualTo(1));
        var canonical = File.ReadAllText(user);
        var body = canonical.Replace(MemoryDocumentEdits.Start, "").Replace(MemoryDocumentEdits.End, "").Trim();
        File.WriteAllText(user, body + "\n\n" + canonical);
        _ = new MashaMemoryStore(_root);
        Assert.That(File.ReadAllText(user), Is.EqualTo(canonical));
        Assert.That(Directory.EnumerateFiles(Path.Combine(_root, ".history"), "*.md", SearchOption.AllDirectories)
            .Select(File.ReadAllText), Does.Contain(body + "\n\n" + canonical));
        File.WriteAllText(user, "Моя отдельная заметка\n\n" + canonical);
        _ = new MashaMemoryStore(_root);
        Assert.That(File.ReadAllText(user), Does.StartWith("Моя отдельная заметка"));
    }

    [Test] public async Task DeleteSeedAndReplaceFactsSurviveNextTurnRestartAndStaleModelUpsert()
    {
        var store = new MashaMemoryStore(_root); var world = await World(store);
        var documents = new WorkspaceDocuments(_root);
        var original = await documents.ReadAsync("MEMORY.md");
        var removed = (await store.SnapshotAsync(default)).CoreMemories.Single(x => x.Key == "origin.strange_room").Value;
        await documents.SaveAsync(original, original.Text.Replace("- " + removed + "\n", "").Replace("настоящей и моей", "моей прежней жизнью"));
        var prompt = await store.BuildPromptContextAsync(world, "комната", default);
        Assert.That(prompt.Text, Does.Not.Contain(removed).And.Contain("моей прежней жизнью"));
        await store.CommitTurnAsync(world, "stale", "heartbeat", new CompanionDecision {
            MemoryUpserts = [new() { Key = "core:old", Value = removed, Importance = 1 }] }, default);
        var reloaded = new MashaMemoryStore(_root);
        Assert.That((await reloaded.SnapshotAsync(default)).CoreMemories.Any(x => x.Value == removed), Is.False);
        Assert.That((await documents.ReadAsync("MEMORY.md")).Text, Does.Not.Contain(removed).And.Not.Contain("Сохранённая ручная правка"));
        Assert.That(Directory.EnumerateFiles(Path.Combine(_root, ".state", "backups"), "before-document-edit-*.json"), Is.Not.Empty);
        await documents.SaveAsync(await documents.ReadAsync("MEMORY.md"), original.Text);
        _ = await reloaded.SnapshotAsync(default);
        var restored = new MashaMemoryStore(_root);
        Assert.That((await restored.SnapshotAsync(default)).CoreMemories.Count(x => x.Value == removed), Is.EqualTo(1));
    }

    [Test] public async Task TwoQueuedEditsAreAppliedInOrderWithoutResurrectingIntermediateText()
    {
        var store = new MashaMemoryStore(_root); var documents = new WorkspaceDocuments(_root);
        var first = await documents.SaveAsync(await documents.ReadAsync("MEMORY.md"), "# Память\n- Первая редакция");
        await documents.SaveAsync(first, first.Text.Replace("Первая редакция", "Окончательная редакция"));
        var archive = await store.SnapshotAsync(default);
        Assert.That(archive.CoreMemories.Select(x => x.Value), Is.EqualTo(new[] { "Окончательная редакция" }));
        Assert.That((await documents.ReadAsync("MEMORY.md")).Text, Does.Not.Contain("Первая редакция"));
        Assert.That(MemoryDocumentEdits.HasPending(_root), Is.False);
    }

    [Test] public async Task AuthorRelationshipEditTargetsOnlyPrimaryAndDoesNotFreezeLaterAssessments()
    {
        var store = new MashaMemoryStore(_root); var world = await World(store);
        await store.BindSpeakerAsync("server:alice", true, default);
        await store.BindSpeakerAsync("server:bob", false, default);
        var documents = new WorkspaceDocuments(_root);
        await documents.SaveAsync(await documents.ReadAsync("USER.md"), "# Голос\n- Player's name is Алекс\n- Знакомство: 80%; доверие: 50%; привязанность: -20%.");
        var archive = await store.SnapshotAsync(default);
        Assert.That(archive.Speakers["server:alice"].Bond.Trust, Is.EqualTo(.5f));
        Assert.That(archive.Speakers["server:bob"].Bond.Trust, Is.Zero);
        Assert.That(archive.Speakers["server:bob"].Facts, Is.Empty);
        Assert.That((await store.BuildPromptContextAsync(world with { SpeakerKey = "server:bob" }, "Алекс", default)).Text, Does.Not.Contain("Алекс"));
        await store.CommitTurnAsync(world with { SpeakerKey = "server:alice", MessageIds = ["warm"] }, "warm", "voice",
            new() { RelationshipAssessment = new(false, RelationshipDirection.Increase, RelationshipDirection.Increase, false, "Помог") }, default);
        Assert.That((await store.SnapshotAsync(default)).Speakers["server:alice"].Bond.Trust, Is.EqualTo(.53f).Within(.0001));
        var text = (await documents.ReadAsync("USER.md")).Text;
        Assert.That(text, Does.Contain("доверие: 53%").And.Not.Contain("доверие: 50%"));
    }

    [Test] public async Task MalformedRelationshipsOrMarkersDoNotWriteOrQueueAnEdit()
    {
        _ = new MashaMemoryStore(_root); var documents = new WorkspaceDocuments(_root);
        var original = await documents.ReadAsync("USER.md");
        Assert.ThrowsAsync<InvalidDataException>(() => documents.SaveAsync(original, original.Text.Replace("доверие: 0%", "доверие: 500%")));
        Assert.ThrowsAsync<InvalidDataException>(() => documents.SaveAsync(original, original.Text.Replace(MemoryDocumentEdits.End, "")));
        Assert.That((await documents.ReadAsync("USER.md")).Text, Is.EqualTo(original.Text));
        Assert.That(MemoryDocumentEdits.HasPending(_root), Is.False);
    }

    [Test] public async Task RemovingMetricLineDoesNotResetRelationship()
    {
        var store = new MashaMemoryStore(_root); var documents = new WorkspaceDocuments(_root);
        await documents.SaveAsync(await documents.ReadAsync("USER.md"), "- Знакомство: 100%; доверие: 100%; привязанность: 100%.");
        _ = await store.SnapshotAsync(default);
        await documents.SaveAsync(await documents.ReadAsync("USER.md"), "- Player's name is Алекс");
        Assert.That((await store.SnapshotAsync(default)).PlayerBond.Trust, Is.EqualTo(1));
    }

    [Test] public async Task ReplayAfterArchiveCommitDoesNotApplyEditTwice()
    {
        var store = new MashaMemoryStore(_root); var documents = new WorkspaceDocuments(_root);
        await documents.SaveAsync(await documents.ReadAsync("MEMORY.md"), "- Единственная заметка");
        var path = Directory.GetFiles(Path.Combine(_root, ".state", "document-edits"), "*.json").Single();
        var bytes = File.ReadAllBytes(path);
        _ = await store.SnapshotAsync(default);
        File.WriteAllBytes(path, bytes); // Simulate a crash before journal cleanup.
        var reloaded = new MashaMemoryStore(_root);
        Assert.That((await reloaded.SnapshotAsync(default)).CoreMemories.Select(x => x.Value), Is.EqualTo(new[] { "Единственная заметка" }));
    }

    [Test] public async Task ManualPrefixIsNotCopiedIntoGeneratedFacts()
    {
        var store = new MashaMemoryStore(_root); var documents = new WorkspaceDocuments(_root);
        var doc = await documents.ReadAsync("MEMORY.md");
        await documents.SaveAsync(doc, "Моя отдельная заметка\n\n" + doc.Text);
        var state = await store.SnapshotAsync(default);
        Assert.That(state.CoreMemories.Any(m => m.Value == "Моя отдельная заметка"), Is.False);
        Assert.That((await documents.ReadAsync("MEMORY.md")).Text.Split("Моя отдельная заметка").Length - 1, Is.EqualTo(1));
    }

    [Test] public async Task DeletedFactIsNotRecalledFromImportedOriginal()
    {
        var store = new MashaMemoryStore(_root); var world = await World(store);
        var documents = new WorkspaceDocuments(_root);
        var value = (await store.SnapshotAsync(default)).CoreMemories.Single(x => x.Key == "origin.strange_room").Value;
        var import = Path.Combine(_root, "memory", "imports", "old"); Directory.CreateDirectory(import);
        File.WriteAllText(Path.Combine(import, "MEMORY.md"), "- " + value);
        var doc = await documents.ReadAsync("MEMORY.md");
        await documents.SaveAsync(doc, doc.Text.Replace("- " + value + "\n", ""));
        Assert.That((await store.BuildPromptContextAsync(world, "запертой незнакомой комнате", default)).Text, Does.Not.Contain(value));
        Assert.That(File.ReadAllText(Path.Combine(import, "MEMORY.md")), Does.Contain(value), "Historical source remains recoverable");
    }
}
