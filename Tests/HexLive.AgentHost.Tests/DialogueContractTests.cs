using System.Text.Json;
using HexLive.AgentCore.Studio;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class DialogueContractTests
{
    [Test]
    public void ObjectiveIsOptionalForOldDecisionsAndNullableForNewOnes()
    {
        var json = JsonSerializer.Serialize(new CompanionDecision());
        Assert.That(AgentProviders.ParseDecision(json, "heartbeat").ObjectiveUpdate, Is.Null);
        Assert.That(AgentProviders.ParseDecision(json.Replace("\"objectiveUpdate\":null,", ""), "heartbeat").ObjectiveUpdate, Is.Null);
        var updated = JsonSerializer.Serialize(new CompanionDecision { ObjectiveUpdate = new()
            { Operation = "set", Text = "Rescue friend", Reason = "Observed distress" },
            Action = new() { Tool = "query_known_objects", Arguments = JsonSerializer.SerializeToElement(new { }) } });
        var parsed = AgentProviders.ParseDecision(updated, "heartbeat");
        Assert.That(parsed.ObjectiveUpdate!.Text, Is.EqualTo("Rescue friend"));
        Assert.That(parsed.Action!.Tool, Is.EqualTo("query_known_objects"));
        var format = typeof(AgentProviders).GetMethod("DecisionResponseFormat",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, null);
        var schema = JsonSerializer.SerializeToElement(format).GetProperty("json_schema").GetProperty("schema");
        Assert.That(schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()), Does.Contain("objectiveUpdate"));
        var objectiveSchema = schema.GetProperty("properties").GetProperty("objectiveUpdate").GetProperty("anyOf");
        Assert.That(objectiveSchema[0].GetProperty("type").GetString(), Is.EqualTo("null"));
        Assert.That(objectiveSchema[1].GetProperty("additionalProperties").GetBoolean(), Is.False);
    }

    [TestCase("[]")]
    [TestCase("{\"operation\":\"run\",\"text\":\"x\",\"reason\":\"why\"}")]
    [TestCase("{\"operation\":\"set\",\"text\":12,\"reason\":\"why\"}")]
    [TestCase("{\"operation\":\"set\",\"text\":\"x\",\"text\":\"y\",\"reason\":\"why\"}")]
    [TestCase("{\"operation\":\"pause\",\"text\":\"replacement\",\"reason\":\"why\"}")]
    [TestCase("{\"operation\":\"set\",\"text\":\"x\",\"reason\":\" \"}")]
    public void MalformedObjectiveCannotEnterDecision(string objective)
    {
        var json = JsonSerializer.Serialize(new CompanionDecision()).Replace("\"objectiveUpdate\":null", "\"objectiveUpdate\":" + objective);
        Assert.Throws<InvalidDataException>(() => AgentProviders.ParseDecision(json, "heartbeat"));
    }

    [TestCase(600, 240, true)]
    [TestCase(601, 240, false)]
    [TestCase(600, 241, false)]
    public void ObjectiveContractHasBoundedText(int textLength, int reasonLength, bool accepted)
    {
        var json = JsonSerializer.Serialize(new CompanionDecision { ObjectiveUpdate = new()
            { Operation = "set", Text = new string('x', textLength), Reason = new string('r', reasonLength) } });
        if (accepted) Assert.That(AgentProviders.ParseDecision(json, "heartbeat").ObjectiveUpdate, Is.Not.Null);
        else Assert.Throws<InvalidDataException>(() => AgentProviders.ParseDecision(json, "heartbeat"));
    }

    private string _root = "";
    private static readonly CancellationToken Token = CancellationToken.None;
    private static RelationshipAssessment Warm => new(true, RelationshipDirection.Increase,
        RelationshipDirection.Increase, false, "Совет помог", "Любимый голос", "Поддержал в опасности");

    [SetUp] public void Setup() => _root = Directory.CreateTempSubdirectory("dialogue-contract-").FullName;
    [TearDown] public void Cleanup() => Directory.Delete(_root, true);

    [TestCase(240, true)] [TestCase(241, true)] [TestCase(600, true)] [TestCase(601, false)]
    public void SpeechBoundaryAppliesToActualProviderParser(int count, bool accepted)
    {
        var value = new CompanionDecision { Speech = new string('я', count), RelationshipAssessment = Warm };
        var json = JsonSerializer.Serialize(value);
        if (accepted) Assert.That(AgentProviders.ParseDecision(json, "voice").Speech, Has.Length.EqualTo(count));
        else Assert.Throws<InvalidDataException>(() => AgentProviders.ParseDecision(json, "voice"));
    }

    [Test]
    public void AssessmentCannotBeOmittedDuplicatedOrReplacePrivileges()
    {
        var json = JsonSerializer.Serialize(new CompanionDecision { RelationshipAssessment = Warm });
        Assert.Throws<InvalidDataException>(() => AgentProviders.ParseDecision(
            json.Replace("\"trust\":\"Increase\"", "\"trust\":\"Increase\",\"trust\":\"Decrease\""), "voice"));
        Assert.Throws<InvalidDataException>(() => AgentProviders.ParseDecision(
            json.Replace("\"reason\":", "\"permissions\":true,\"reason\":"), "voice"));
        Assert.Throws<InvalidDataException>(() => AgentProviders.ParseDecision(JsonSerializer.Serialize(new CompanionDecision()), "voice"));
    }

    [Test]
    public async Task AffectionateProfanityHasIndependentAssessmentAndSurvivesReplay()
    {
        var store = new MashaMemoryStore(_root);
        var world = await World(store, 1000);
        await store.BindSpeakerAsync("server:alice", false, Token);
        world = world with { SpeakerKey = "server:alice", MessageIds = ["one", "two"], PlayerText = "У меня кот Барсик" };
        var answer = new CompanionDecision { Speech = "Спасибо, засранец.", Reaction = "Hostile", RelationshipAssessment = Warm,
            MemoryUpserts = [new() { Key = "user:pet", Value = "У него кот Барсик.", Importance = 1 }] };
        await store.CommitTurnAsync(world, "turn", "voice", answer, Token);
        store = new MashaMemoryStore(_root);
        Assert.That(await store.CommitTurnAsync(world, "reconnect-turn", "voice", answer, Token), Is.False);
        var speaker = (await store.SnapshotAsync(Token)).Speakers["server:alice"];
        Assert.Multiple(() => {
            Assert.That(speaker.Bond.Trust, Is.EqualTo(.03f).Within(.0001f));
            Assert.That(speaker.Bond.Affinity, Is.EqualTo(.02f).Within(.0001f));
            Assert.That(speaker.VoiceName, Is.EqualTo("Любимый голос"));
            Assert.That(speaker.RecentConversation, Has.Count.EqualTo(2));
            Assert.That(speaker.RecentConversation[0], Does.Contain("Барсик"));
        });
        await store.BindSpeakerAsync("server:bob", false, Token);
        var alice = await store.BuildPromptContextAsync(world, "кот", Token);
        var bob = await store.BuildPromptContextAsync(world with { SpeakerKey = "server:bob" }, "кот", Token);
        Assert.That(alice.Text, Does.Contain("Барсик").And.Contain("Любимый голос"));
        Assert.That(bob.Text, Does.Not.Contain("Барсик").And.Not.Contain("Любимый голос"));
        Assert.That((await store.SnapshotAsync(Token)).Speakers["server:bob"].Bond.Trust, Is.Zero);
        Assert.That((await store.SnapshotAsync(Token)).Speakers["server:bob"].RecentConversation, Is.Empty);
    }

    [Test]
    public async Task SeriousHarmPersistsNegativeSympathyAndInvalidAssessmentCannotWriteFacts()
    {
        var store = new MashaMemoryStore(_root);
        var world = (await World(store, 1000)) with { SpeakerKey = "server:alice", MessageIds = ["harm"] };
        await store.BindSpeakerAsync(world.SpeakerKey, false, Token);
        var bad = new CompanionDecision {
            RelationshipAssessment = Warm with { VoiceName = "", NamingReason = null },
            MemoryUpserts = [new() { Key = "user:invalid", Value = "Не сохранять" }] };
        Assert.ThrowsAsync<InvalidDataException>(async () => await store.CommitTurnAsync(world, "bad", "voice", bad, Token));
        Assert.That((await store.SnapshotAsync(Token)).Speakers[world.SpeakerKey].Facts, Is.Empty);
        await store.CommitTurnAsync(world, "valid", "voice", new() { Reaction = "Warm",
            RelationshipAssessment = new(false, RelationshipDirection.Decrease, RelationshipDirection.Decrease,
                true, "Подтверждённое серьёзное воздействие") }, Token);
        var speaker = (await new MashaMemoryStore(_root).SnapshotAsync(Token)).Speakers[world.SpeakerKey];
        Assert.That(speaker.Bond.Trust, Is.Zero);
        Assert.That(speaker.Bond.Affinity, Is.EqualTo(-.04f).Within(.0001));
    }

    [Test]
    public async Task LegacyValuesMigrateOnlyToConfirmedOwnerWithoutRescaling()
    {
        var store = new MashaMemoryStore(_root);
        var world = await World(store, 1000);
        var archive = await store.SnapshotAsync(Token);
        archive.PlayerBond.Trust = .7f; archive.PlayerBond.Affinity = .8f;
        archive.PlayerBond.LastInteractionEpisodeId = world.EpisodeId;
        archive.PlayerBond.LastInteractionTick = 1000;
        archive.Worlds[0].Memories.Add(new() { Key = "old-person", Value = "Старый собеседник — архитектор", Source = "hexlive-legacy", Importance = 1 });
        File.WriteAllText(store.FilePath, JsonSerializer.Serialize(archive, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        store = new MashaMemoryStore(_root);
        await store.BindSpeakerAsync("other-server:bob", false, Token);
        await store.BindSpeakerAsync("server:alice", true, Token);
        await store.BindSpeakerAsync("server:charlie", true, Token);
        var restored = await new MashaMemoryStore(_root).SnapshotAsync(Token);
        Assert.That(restored.Speakers["server:alice"].Bond.Affinity, Is.EqualTo(.8f));
        Assert.That(restored.Speakers["server:charlie"].Bond.Affinity, Is.Zero);
        Assert.That(restored.PlayerBond.Affinity, Is.EqualTo(.8f), "Legacy archive retained");
        Assert.That(restored.Speakers["server:alice"].Bond.LastVoiceTick, Is.EqualTo(1000));
        Assert.That((await store.BuildPromptContextAsync(world with { SpeakerKey = "server:alice" }, "архитектор", Token)).Text,
            Does.Contain("архитектор"));
        Assert.That((await store.BuildPromptContextAsync(world with { SpeakerKey = "server:charlie" }, "архитектор", Token)).Text,
            Does.Not.Contain("архитектор"));
    }

    [Test]
    public async Task PauseAndRestartUseOnlyGameTicksAndReturnIsConsumedOnce()
    {
        var store = new MashaMemoryStore(_root);
        var world = await World(store, 1000);
        await store.BindSpeakerAsync("server:alice", false, Token);
        world = world with { SpeakerKey = "server:alice", MessageIds = ["hello"] };
        await store.ObserveSpeakerPresenceAsync(world.SpeakerKey, true, world, Token);
        await store.CommitTurnAsync(world, "first", "voice", new() { RelationshipAssessment = Warm }, Token);
        await store.ObserveSpeakerPresenceAsync(world.SpeakerKey, false, world, Token);
        store = new MashaMemoryStore(_root);
        // Three real days could have passed. Same tick means zero subjective time.
        await store.ObserveSpeakerPresenceAsync(world.SpeakerKey, true, world, Token);
        var paused = await store.BuildPromptContextAsync(world, "привет", Token);
        Assert.That(paused.Text, Does.Contain("0 игровых минут").And.Not.Contain("UTC"));
        await store.CommitTurnAsync(world, "autonomous", "heartbeat", new() { RelationshipAssessment = Warm }, Token);
        Assert.That((await store.SnapshotAsync(Token)).Speakers[world.SpeakerKey].Bond.AwaitingReturnVoice, Is.True);
        var later = world with { Tick = 1167, MessageIds = ["returned"] };
        Assert.That((await store.BuildPromptContextAsync(later, "", Token)).Text, Does.Contain("10 игровых минут"));
        await store.CommitTurnAsync(later, "second", "voice", new(), Token);
        Assert.That((await store.SnapshotAsync(Token)).Speakers[world.SpeakerKey].Bond.AwaitingReturnVoice, Is.False);
    }

    [Test]
    public async Task ReplyStartedBeforeDepartureCannotConsumeTheNextReturnGreeting()
    {
        var store = new MashaMemoryStore(_root);
        var world = (await World(store, 900)) with
        { SpeakerKey = "server:alice", MessageIds = ["first"] };
        await store.BindSpeakerAsync(world.SpeakerKey, false, Token);
        await store.ObserveSpeakerPresenceAsync(world.SpeakerKey, true, world, Token);
        await store.CommitTurnAsync(world, "first", "voice", new(), Token);

        var inFlight = world with { Tick = 1000, MessageIds = ["before-departure"] };
        await store.ObserveSpeakerPresenceAsync(world.SpeakerKey, false, world with { Tick = 1100 }, Token);
        await store.ObserveSpeakerPresenceAsync(world.SpeakerKey, true, world with { Tick = 1200 }, Token);
        await store.CommitTurnAsync(inFlight, "late-answer", "voice", new(), Token);

        store = new MashaMemoryStore(_root);
        await store.CommitTurnAsync(world with { Tick = 1250 }, "heartbeat", "heartbeat", new(), Token);
        var waiting = (await store.SnapshotAsync(Token)).Speakers[world.SpeakerKey].Bond;
        Assert.That(waiting.AwaitingReturnVoice, Is.True,
            "Finishing an older request is not the first voice input after returning");
        Assert.That(waiting.LastReturnTick, Is.EqualTo(1200));
        Assert.That(waiting.Trust, Is.Zero);
        Assert.That(waiting.Familiarity, Is.Zero);

        await store.CommitTurnAsync(world with { Tick = 1300, MessageIds = ["after-return"] },
            "new-answer", "voice", new(), Token);
        Assert.That((await store.SnapshotAsync(Token)).Speakers[world.SpeakerKey].Bond.AwaitingReturnVoice,
            Is.False);
    }

    [Test]
    public void DifferentServerPathsCannotShareTheSameSpeakerRecord()
    {
        const string sender = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var first = AgentHostRuntime.SpeakerKeyFor(new("https://example.test/one/mcp"), sender);
        var second = AgentHostRuntime.SpeakerKeyFor(new("https://example.test/two/mcp"), sender);
        Assert.That(first, Is.Not.EqualTo(second));
        Assert.That(AgentHostRuntime.SpeakerKeyFor(new("https://example.test/one/mcp/"), sender), Is.EqualTo(first));
        Assert.That(AgentHostRuntime.SpeakerKeyFor(new("https://example.test/one/mcp"), "pretend-to-be-alice"),
            Does.EndWith(":unidentified"));
    }

    [Test]
    public async Task AuthoritativeWorldIdSeparatesWorldsWithIdenticalSeedsForCliToo()
    {
        var store = new MashaMemoryStore(_root);
        var first = await store.BindHexLiveWorldAsync(JsonSerializer.SerializeToElement(new {
            worldId = "world-a", seed = 42, tick = 1000, dayLengthTicks = 24000 }), 901, "", Token);
        var second = await store.BindHexLiveWorldAsync(JsonSerializer.SerializeToElement(new {
            worldId = "world-b", seed = 42, tick = 1001, dayLengthTicks = 24000 }), 901, "", Token);
        Assert.That(second.EpisodeId, Is.Not.EqualTo(first.EpisodeId));
        var bond = new PortablePlayerBond { ClockWorldKey = first.EpisodeId, LastVoiceTick = 1000, LastClockTick = 1000 };
        Assert.That(AgentGameTime.Describe(bond, second), Does.Contain("неизвестно"));
    }

    [Test]
    public void UnknownWorldRollbackAndUnknownCalendarNeverUseWallClock()
    {
        var bond = new PortablePlayerBond { ClockWorldKey = "a", LastVoiceTick = 100, LastClockTick = 100,
            LastInteractionUtc = DateTimeOffset.UtcNow.AddDays(-3) };
        var world = new MashaWorldHandle("a", "a", 24100, 24) { DayLengthTicks = 24000 };
        Assert.That(AgentGameTime.Describe(bond, world), Does.Contain("1440 игровых минут"));
        Assert.That(AgentGameTime.Describe(bond, world with { EpisodeId = "b" }), Does.Contain("неизвестно"));
        Assert.That(AgentGameTime.Describe(bond, world with { Tick = 50 }), Does.Contain("неизвестно"));
        Assert.That(AgentGameTime.Describe(bond, world with { DayLengthTicks = 0 }), Does.Contain("неизвестно"));
    }

    [Test]
    public void MandatoryContextSurvivesLargeMemoryAndExamplesDoNotBecomeMemories()
    {
        var episode = new MashaWorldEpisode { Id = "a", WorldKey = "a" };
        var archive = new MashaArchive { Worlds = [episode], PlayerBond = new() { Familiarity = 1, Trust = 1, Affinity = 1 } };
        var workspace = new MashaMemoryWorkspace(_root);
        File.WriteAllText(Path.Combine(_root, "MEMORY.md"), new string('я', 15000));
        var context = workspace.BuildPrompt(archive, episode, "", new("a", "a", 100, 0) { DayLengthTicks = 24000 });
        var system = AgentPromptBuilder.Build(DialogueStyles.Masha, context.Text, "Он помог");
        Assert.That(context.Text, Does.Contain("<voice_relationship>").And.Contain("<game_contact_time>"));
        Assert.That(context.CharacterCount, Is.LessThanOrEqualTo(MashaMemoryWorkspace.MaxPromptCharacters));
        Assert.That(system, Does.Contain("<authored_voice").And.Contain("speech_examples_not_memories"));
        Assert.That(context.Text, Does.Not.Contain("Кшиштоф"));
        Assert.That(AgentPromptBuilder.Build(null, context.Text, "привет"), Does.Not.Contain("masha-sharp-v1"));
        Assert.That(DialogueStyles.Select(1, 1, "help").Select(e => e.Id),
            Is.EqualTo(new[] { "saved-approved", "bluff-approved" }));
    }

    [Test]
    public void MixedSpeakersAreNotMergedOrAcknowledgedTogether()
    {
        using var json = JsonDocument.Parse("""
        {"messages":[{"seq":1,"messageId":"a","senderId":"alice"},
        {"seq":2,"messageId":"b","senderId":"bob"},{"seq":3,"messageId":"c","senderId":"alice"}],"watermark":3}
        """);
        var first = AgentHostRuntime.FirstSpeakerInbox(json.RootElement);
        Assert.That(first.GetProperty("watermark").GetInt64(), Is.EqualTo(1));
        Assert.That(AgentHostRuntime.MessageIdsOf(first), Is.EqualTo(new[] { "a" }));
    }

    private static Task<MashaWorldHandle> World(MashaMemoryStore store, long tick) => store.BindHexLiveWorldAsync(
        JsonSerializer.SerializeToElement(new { tick, seed = 7, dayLengthTicks = 24000 }), 901, "world", Token);
}
