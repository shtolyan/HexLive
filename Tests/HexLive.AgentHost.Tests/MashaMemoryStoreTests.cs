using System.Text.Json;
using HexLive.AgentHost;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

[NonParallelizable]
public sealed class MashaMemoryStoreTests
{
    private string _directory = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "masha-memory-tests-" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Test]
    public async Task ReturnVoiceSurvivesReloadAndHeartbeatButIsConsumedOnceByVoice()
    {
        var store = new MashaMemoryStore(_directory);
        var world = await store.BindHexLiveWorldAsync(Json("{\"tick\":1200,\"seed\":7}"),
            901, "", CancellationToken.None);
        await store.ObservePlayerPresenceAsync(true, CancellationToken.None);
        await store.CommitTurnAsync(world, "first", "voice", new CompanionDecision(), CancellationToken.None);
        var before = await store.SnapshotAsync(CancellationToken.None);
        Assert.That(before.PlayerBond.LastInteractionUtc, Is.Not.Null, "None reaction still heard the player");
        await store.ObservePlayerPresenceAsync(false, CancellationToken.None);
        store = new MashaMemoryStore(_directory);
        await store.ObservePlayerPresenceAsync(true, CancellationToken.None);
        await store.CommitTurnAsync(world, "heartbeat", "heartbeat", new CompanionDecision(), CancellationToken.None);
        var returning = await store.SnapshotAsync(CancellationToken.None);
        var prompt = await store.BuildPromptContextAsync(world, "", CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(returning.PlayerBond.AwaitingReturnVoice, Is.True);
            Assert.That(returning.PlayerBond.LastInteractionUtc, Is.EqualTo(before.PlayerBond.LastInteractionUtc));
            Assert.That(prompt.Text, Does.Contain("Последнее завершённое общение UTC"));
            Assert.That(prompt.Text, Does.Contain("возвращения: да"));
            Assert.That(prompt.CharacterCount, Is.LessThanOrEqualTo(MashaMemoryWorkspace.MaxPromptCharacters));
        });
        await store.CommitTurnAsync(world, "return-voice", "voice", new CompanionDecision(), CancellationToken.None);
        var heard = await store.SnapshotAsync(CancellationToken.None);
        await store.CommitTurnAsync(world, "return-voice", "voice", new CompanionDecision(), CancellationToken.None);
        await store.ObservePlayerPresenceAsync(true, CancellationToken.None);
        var repeat = await new MashaMemoryStore(_directory).SnapshotAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(repeat.PlayerBond.AwaitingReturnVoice, Is.False);
            Assert.That(repeat.PlayerBond.LastInteractionUtc, Is.EqualTo(heard.PlayerBond.LastInteractionUtc));
            Assert.That(repeat.PlayerBond.Familiarity, Is.Zero, "No artificial relationship boost");
        });
    }

    [Test]
    public async Task NewArchiveHasPortableOriginAndSurvivesReload()
    {
        var store = new MashaMemoryStore(_directory);
        var first = await store.SnapshotAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(first.Identity.Name, Is.EqualTo("Маша"));
            Assert.That(first.CoreMemories.Any(x => x.Key == "origin.life_before_room"), Is.True);
            Assert.That(first.CoreMemories.Any(x => x.Key == "origin.strange_room"), Is.True);
            Assert.That(File.Exists(store.FilePath), Is.True);
            Assert.That(store.FilePath, Does.EndWith(Path.Combine(".state", "state.json")));
            Assert.That(File.Exists(Path.Combine(_directory, "SOUL.md")), Is.True);
            Assert.That(File.Exists(Path.Combine(_directory, "USER.md")), Is.True);
            Assert.That(File.Exists(Path.Combine(_directory, "MEMORY.md")), Is.True);
        });

        var reloaded = await new MashaMemoryStore(_directory).SnapshotAsync(CancellationToken.None);
        Assert.That(reloaded.CoreMemories.Select(x => x.Key),
            Is.EquivalentTo(first.CoreMemories.Select(x => x.Key)));
    }

    [Test]
    public async Task LegacyHexLiveImportIsIdempotentAndPreservesWorldHistory()
    {
        var store = new MashaMemoryStore(_directory);
        var world = await store.BindHexLiveWorldAsync(Json("""
            {"tick":1200,"seed":12345,"mode":"HugeIsland"}
            """), 901, "", CancellationToken.None);
        var legacy = Json("""
            {
              "hexkufaExposure":16,
              "lastIntentSummary":"Ищу воду.",
              "playerVoiceBond":{"familiarity":0.3,"trust":0.2,"affinity":0.1},
              "memories":[{"key":"arrival.island","value":"Очнулась на берегу.","importance":1.0,"lastUpdatedTick":10}],
              "journal":[{"tick":20,"text":"Первый вечер у костра."}]
            }
            """);

        var first = await store.ImportHexLiveLegacyAsync(world, legacy, CancellationToken.None);
        var second = await store.ImportHexLiveLegacyAsync(world, legacy, CancellationToken.None);
        var archive = await store.SnapshotAsync(CancellationToken.None);
        var episode = archive.Worlds.Single();

        Assert.Multiple(() =>
        {
            Assert.That(first.Changed, Is.True);
            Assert.That(second.Changed, Is.False);
            Assert.That(episode.LanguageExposure, Is.EqualTo(16));
            Assert.That(episode.Memories.Single(x => x.Key == "arrival.island").Value,
                Is.EqualTo("Очнулась на берегу."));
            Assert.That(episode.Journal, Has.Count.EqualTo(1));
            Assert.That(archive.PlayerBond.Familiarity, Is.EqualTo(0.3f).Within(0.0001f));
        });
    }

    [Test]
    public async Task LocalTurnDeduplicatesBondAndLimitsJournalToOnePerGameHour()
    {
        var store = new MashaMemoryStore(_directory);
        var world = await store.BindHexLiveWorldAsync(Json("{" +
            "\"tick\":1200,\"seed\":7}"), 901, "", CancellationToken.None);
        var decision = new CompanionDecision
        {
            Reaction = "Warm",
            IntentSummary = "Остаюсь у костра.",
            JournalText = "Голос сегодня помог.",
            MemoryUpserts = [new MemoryUpdate { Key = "voice.help", Value = "Он помог.", Importance = 0.8f }]
        };

        Assert.That(await store.CommitTurnAsync(
            world, "turn-a", "voice", decision, CancellationToken.None), Is.True);
        Assert.That(await store.CommitTurnAsync(
            world, "turn-a", "voice", decision, CancellationToken.None), Is.False);
        Assert.That(await store.CommitTurnAsync(
            world with { Tick = 1250 }, "turn-b", "heartbeat", decision, CancellationToken.None), Is.True);

        var archive = await store.SnapshotAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(archive.PlayerBond.Familiarity, Is.EqualTo(0.03f).Within(0.0001f));
            Assert.That(archive.Worlds.Single().Journal, Has.Count.EqualTo(1));
            Assert.That(archive.Worlds.Single().Memories.Single(x => x.Key == "voice.help").Value,
                Is.EqualTo("Он помог."));
        });
    }

    [Test]
    public async Task MollyImportKeepsCuratedFilesButNeverPersistsConversationTranscript()
    {
        var molly = Path.Combine(_directory, "iphone", "masha");
        Directory.CreateDirectory(Path.Combine(molly, "diary"));
        File.WriteAllText(Path.Combine(molly, "USER.md"),
            "- Игрок любит море\n- Player's name is ЛЛМ-модель\n");
        File.WriteAllText(Path.Combine(molly, "MEMORY.md"), "- Я помню комнату\n");
        File.WriteAllText(Path.Combine(molly, "conversations.md"),
            "Player: FULL_TRANSCRIPT_MUST_NOT_PERSIST\nMasha: ответ\n");
        File.WriteAllText(Path.Combine(molly, "diary", "2026-01-01.md"), "[12:00] Я нашла дверь.\n");
        File.WriteAllText(Path.Combine(molly, "state.txt"),
            "dead: true\nrel_friendship: 54.9\nrel_love: 3.5\nrel_hostility: 0.0\n" +
            "saved: 2026-09-03T22:06:03.9689490+07:00\n");

        var store = new MashaMemoryStore(Path.Combine(_directory, "archive"));
        var result = await store.ImportMollyDirectoryAsync(
            Path.Combine(_directory, "iphone"), "Molly iPhone", CancellationToken.None);
        var snapshot = await store.SnapshotAsync(CancellationToken.None);
        var persisted = File.ReadAllText(store.FilePath);

        Assert.Multiple(() =>
        {
            Assert.That(result.Changed, Is.True);
            Assert.That(snapshot.Worlds.Single().Status, Is.EqualTo("died"));
            Assert.That(snapshot.Worlds.Single().Journal.Single().Text, Does.Contain("Я нашла дверь"));
            Assert.That(snapshot.CoreMemories.Any(x => x.Value == "Игрок любит море"), Is.True);
            Assert.That(snapshot.CoreMemories.Any(x => x.Value.Contains("ЛЛМ-модель")), Is.True);
            Assert.That(snapshot.PlayerBond.Familiarity, Is.EqualTo(0.549f).Within(0.0001f));
            Assert.That(snapshot.PlayerBond.Trust, Is.EqualTo(0.549f).Within(0.0001f));
            Assert.That(snapshot.PlayerBond.Affinity, Is.EqualTo(0.035f).Within(0.0001f));
            Assert.That(snapshot.PlayerBond.LastInteractionEpisodeId, Is.EqualTo(snapshot.Worlds.Single().Id));
            Assert.That(persisted, Does.Not.Contain("FULL_TRANSCRIPT_MUST_NOT_PERSIST"));
        });
    }

    [Test]
    public async Task MollyImportAcceptsAnXcodeAppDataContainer()
    {
        var container = Path.Combine(_directory, "Molly.xcappdata");
        var molly = Path.Combine(container, "AppData", "Documents", "masha");
        Directory.CreateDirectory(molly);
        File.WriteAllText(Path.Combine(molly, "MEMORY.md"), "- Я жила в комнате\n");

        var store = new MashaMemoryStore(Path.Combine(_directory, "archive"));
        var result = await store.ImportMollyDirectoryAsync(
            container, "Molly iPhone", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Changed, Is.True);
            Assert.That(result.Memories, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ChangedMollySnapshotKeepsTheLatestBoundedDiaryWindow()
    {
        var molly = Path.Combine(_directory, "iphone", "masha");
        var diary = Path.Combine(molly, "diary");
        Directory.CreateDirectory(diary);
        File.WriteAllText(Path.Combine(molly, "USER.md"), "- Старое прозвище\n");
        var older = Path.Combine(diary, "2026-01-01.md");
        var newer = Path.Combine(diary, "2026-02-01.md");
        File.WriteAllLines(older, Enumerable.Range(0, 50).Select(x => $"old-{x:00}"));
        File.WriteAllLines(newer, Enumerable.Range(0, 50).Select(x => $"new-{x:00}"));
        File.SetLastWriteTimeUtc(older, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc));
        var conversations = Path.Combine(molly, "conversations.md");
        File.WriteAllText(conversations, "first fingerprint\n");

        var store = new MashaMemoryStore(Path.Combine(_directory, "archive"));
        await store.ImportMollyDirectoryAsync(
            Path.Combine(_directory, "iphone"), "Molly iPhone", CancellationToken.None);
        var first = (await store.SnapshotAsync(CancellationToken.None)).Worlds.Single().Journal
            .Select(x => x.Text).ToArray();

        File.WriteAllText(conversations, "second fingerprint\n");
        await store.ImportMollyDirectoryAsync(
            Path.Combine(_directory, "iphone"), "Molly iPhone", CancellationToken.None);
        var second = (await store.SnapshotAsync(CancellationToken.None)).Worlds.Single().Journal
            .Select(x => x.Text).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(second, Is.EqualTo(first));
            Assert.That(second, Has.Length.EqualTo(MashaMemoryStore.MaxJournalEntriesPerWorld));
            Assert.That(second[0], Is.EqualTo("new-02"));
            Assert.That(second[^1], Is.EqualTo("new-49"));
        });
    }

    [Test]
    public async Task PromptUsesBoundedMarkdownAndRecallsOnlyRelevantImportedNotes()
    {
        var molly = Path.Combine(_directory, "iphone", "masha");
        Directory.CreateDirectory(Path.Combine(molly, "diary"));
        File.WriteAllText(Path.Combine(molly, "USER.md"), "- Player's name is ЛЛМ-модель\n");
        File.WriteAllText(Path.Combine(molly, "diary", "2026-03-01.md"),
            "Я спрятала медный ключ возле красного маяка.\n" +
            "Совсем другая заметка про старое кресло.\n");

        var store = new MashaMemoryStore(Path.Combine(_directory, "archive"));
        await store.ImportMollyDirectoryAsync(
            Path.Combine(_directory, "iphone"), "Molly iPhone", CancellationToken.None);
        var world = await store.BindHexLiveWorldAsync(
            Json("{\"tick\":100,\"seed\":42,\"mode\":\"HugeIsland\"}"),
            901, "", CancellationToken.None);

        var prompt = await store.BuildPromptContextAsync(
            world, "Я вижу маяк. Где ключ?", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(prompt.CharacterCount, Is.LessThanOrEqualTo(MashaMemoryWorkspace.MaxPromptCharacters));
            Assert.That(prompt.RecalledFragments, Is.LessThanOrEqualTo(MashaMemoryWorkspace.MaxRecallFragments));
            Assert.That(prompt.Text, Does.Contain("медный ключ"));
            Assert.That(prompt.Text, Does.Contain("ЛЛМ-модель"));
            Assert.That(prompt.Text.TrimStart(), Does.StartWith("# Память Маши"));
            Assert.That(prompt.Text, Does.Not.Contain("schemaVersion"));
        });
    }

    [Test]
    public async Task ReusedSeedAfterLargeTickRollbackStartsAnotherEpisode()
    {
        var store = new MashaMemoryStore(_directory);
        var first = await store.BindHexLiveWorldAsync(
            Json("{\"tick\":5000,\"seed\":9}"), 901, "", CancellationToken.None);
        var second = await store.BindHexLiveWorldAsync(
            Json("{\"tick\":10,\"seed\":9}"), 901, "", CancellationToken.None);

        Assert.That(second.EpisodeId, Is.Not.EqualTo(first.EpisodeId));
        Assert.That((await store.SnapshotAsync(CancellationToken.None)).Worlds, Has.Count.EqualTo(2));
    }

    private static JsonElement Json(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
