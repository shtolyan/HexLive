using System.Text.Json;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentObjectiveTests
{
    private string _directory = "";
    [SetUp] public void Setup() => _directory = Directory.CreateTempSubdirectory("bug390-").FullName;
    [TearDown] public void Cleanup() => Directory.Delete(_directory, true);

    [Test]
    public async Task GoalSurvivesRestartAndUnrelatedIntentionWithoutBeingTreatedAsBodyState()
    {
        var store = new MashaMemoryStore(_directory);
        var world = await Bind(store);
        await store.CommitTurnAsync(world with { ObjectiveRevision = 0 }, "set", "voice", Decision("set", "Спасти подругу на другом берегу"), default);
        store = new MashaMemoryStore(_directory);
        world = await Bind(store);
        await store.CommitTurnAsync(world, "idle", "heartbeat", new CompanionDecision { IntentSummary = "Я без сознания" }, default);
        var prompt = await store.BuildPromptContextAsync(world, "еда", default);
        Assert.That(prompt.Text, Does.Contain("Спасти подругу на другом берегу").And.Not.Contain("Я без сознания"));
        var goal = (await store.SnapshotAsync(default)).Objective!;
        Assert.That(goal.Status, Is.EqualTo("active"));
        Assert.That(goal.Revision, Is.EqualTo(1));
        Assert.That(prompt.ObjectiveRevision, Is.EqualTo(1));
    }

    [Test]
    public async Task PauseResumeCompleteAndReplaceKeepOneGoalAndMonotonicRevision()
    {
        var store = new MashaMemoryStore(_directory);
        var world = await Bind(store);
        var operations = new[] { "set", "pause", "resume", "complete", "clear", "set" };
        for (var i = 0; i < operations.Length; i++)
            await store.CommitTurnAsync(world with { ObjectiveRevision = i }, "turn-" + i, "voice",
                Decision(operations[i], operations[i] == "set" ? "goal-" + i : ""), default);
        var goal = (await store.SnapshotAsync(default)).Objective!;
        Assert.That(goal.Text, Is.EqualTo("goal-5"));
        Assert.That(goal.Status, Is.EqualTo("active"));
        Assert.That(goal.Revision, Is.EqualTo(6));
    }

    [Test]
    public async Task DurableTurnReplayCannotAdvanceObjectiveTwice()
    {
        var store = new MashaMemoryStore(_directory);
        var world = (await Bind(store)) with { ObjectiveRevision = 0 };
        var decision = Decision("set", "Find water");
        await store.CommitTurnAsync(world, "same-turn", "voice", decision, default);
        store = new MashaMemoryStore(_directory);
        Assert.That(await store.CommitTurnAsync(world, "same-turn", "voice", decision, default), Is.False);
        Assert.That((await store.SnapshotAsync(default)).Objective!.Revision, Is.EqualTo(1));
    }

    [Test]
    public async Task RejectedStaleMutationCannotWriteMemoryOrMarkTurnApplied()
    {
        var store = new MashaMemoryStore(_directory);
        var world = (await Bind(store)) with { ObjectiveRevision = 0 };
        await store.CommitTurnAsync(world, "first", "voice", Decision("set", "Rescue"), default);
        var stale = Decision("set", "Wrong late goal");
        stale.MemoryUpserts.Add(new() { Key = "late", Value = "MUST-NOT-COMMIT", Importance = 1 });
        Assert.ThrowsAsync<AgentObjectiveConflictException>(() => store.CommitTurnAsync(world, "stale", "voice", stale, default));
        var archive = await store.SnapshotAsync(default);
        Assert.That(archive.Objective!.Text, Is.EqualTo("Rescue"));
        Assert.That(archive.AppliedTurnIds, Does.Not.Contain("stale"));
        Assert.That(JsonSerializer.Serialize(archive), Does.Not.Contain("MUST-NOT-COMMIT"));
    }

    [TestCase(902, "fixture", "resume")]
    [TestCase(901, "other", "resume")]
    [TestCase(902, "fixture", "complete")]
    [TestCase(901, "other", "complete")]
    public async Task GoalCannotResumeOrCompleteUnderDifferentAvatarOrWorld(int npcId, string worldId, string operation)
    {
        var store = new MashaMemoryStore(_directory);
        var original = await Bind(store);
        await store.CommitTurnAsync(original with { ObjectiveRevision = 0 }, "set", "voice", Decision("set", "Rescue"), default);
        await store.CommitTurnAsync(original with { ObjectiveRevision = 1 }, "pause", "voice", Decision("pause"), default);
        var other = await Bind(store, npcId, worldId);
        Assert.ThrowsAsync<InvalidDataException>(() => store.CommitTurnAsync(other with { ObjectiveRevision = 2 }, "invalid", "voice", Decision(operation), default));
        Assert.That((await store.SnapshotAsync(default)).Objective!.Status, Is.EqualTo("paused"));
    }

    [TestCase("pause", "paused")]
    [TestCase("clear", "canceled")]
    public async Task DifferentBodyCanReconsiderGoalWithoutErasingItsOriginalTextOrBinding(string operation, string status)
    {
        var store = new MashaMemoryStore(_directory);
        var original = await Bind(store);
        await store.CommitTurnAsync(original with { ObjectiveRevision = 0 }, "set", "heartbeat", Decision("set", "Rescue"), default);
        var other = await Bind(store, 902, "other");
        await store.CommitTurnAsync(other with { ObjectiveRevision = 1 }, operation, "heartbeat", Decision(operation), default);
        var goal = (await store.SnapshotAsync(default)).Objective!;
        Assert.That(goal.Status, Is.EqualTo(status));
        Assert.That(goal.Text, Is.EqualTo("Rescue"));
        Assert.That(goal.WorldKey, Is.EqualTo(original.WorldKey));
        Assert.That(goal.AvatarNpcId, Is.EqualTo(901));
        await store.CommitTurnAsync(other with { ObjectiveRevision = 2 }, "replace", "heartbeat", Decision("set", "Find local water"), default);
        goal = (await store.SnapshotAsync(default)).Objective!;
        Assert.That(goal.Status, Is.EqualTo("active"));
        Assert.That(goal.WorldKey, Is.EqualTo(other.WorldKey));
        Assert.That(goal.AvatarNpcId, Is.EqualTo(902));
    }

    [Test]
    public async Task GoalRemainsMandatoryWhenMemoryPromptIsFull()
    {
        var store = new MashaMemoryStore(_directory);
        var world = await Bind(store);
        await store.CommitTurnAsync(world with { ObjectiveRevision = 0 }, "set", "voice", Decision("set", "GOAL-MUST-REMAIN"), default);
        for (var i = 0; i < 24; i++)
            await store.CommitTurnAsync(world, "facts-" + i, "heartbeat", new CompanionDecision
            {
                MemoryUpserts = Enumerable.Range(0, 3).Select(j => new MemoryUpdate { Key = $"fact-{i}-{j}", Value = new string('x', 390) + i + j, Importance = 1 }).ToList(),
                JournalText = new string('y', 390),
            }, default);
        var prompt = await store.BuildPromptContextAsync(world, "xxxxx", default);
        Assert.That(prompt.Text, Does.Contain("GOAL-MUST-REMAIN"));
        Assert.That(prompt.CharacterCount, Is.LessThanOrEqualTo(MashaMemoryWorkspace.MaxPromptCharacters));
    }

    [Test]
    public async Task OldArchiveAndPersistedTurnWithoutObjectiveFieldsRemainCompatible()
    {
        var store = new MashaMemoryStore(_directory);
        var world = await Bind(store);
        var statePath = Path.Combine(_directory, ".state", "state.json");
        var oldState = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(statePath))!.AsObject();
        foreach (var name in oldState.Select(p => p.Key).Where(n => n.Equals("objective", StringComparison.OrdinalIgnoreCase)).ToArray())
            oldState.Remove(name);
        await File.WriteAllTextAsync(statePath, oldState.ToJsonString());
        var pending = new PendingAgentTurn { TurnId = "legacy", Trigger = "heartbeat", World = world,
            Decision = new CompanionDecision { IntentSummary = "Legacy intention" } };
        var oldTurn = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(pending))!.AsObject();
        oldTurn["World"]!.AsObject().Remove("ObjectiveRevision");
        oldTurn["Decision"]!.AsObject().Remove("objectiveUpdate");
        var restored = JsonSerializer.Deserialize<PendingAgentTurn>(oldTurn.ToJsonString())!;
        store = new MashaMemoryStore(_directory);
        Assert.That(await store.CommitTurnAsync(restored.World, restored.TurnId, restored.Trigger, restored.Decision, default), Is.True);
        Assert.That((await store.SnapshotAsync(default)).Objective, Is.Null);
    }

    [Test]
    public async Task StaleActionWithoutObjectiveUpdateAlsoFailsBeforeMemoryWrite()
    {
        var store = new MashaMemoryStore(_directory);
        var world = (await Bind(store)) with { ObjectiveRevision = 0 };
        await store.CommitTurnAsync(world, "goal", "heartbeat", Decision("set", "Rescue"), default);
        var stale = new CompanionDecision { Action = new()
            { Tool = "move_to", Arguments = JsonSerializer.SerializeToElement(new { x = 1, y = 2 }) } };
        Assert.ThrowsAsync<AgentObjectiveConflictException>(() => store.ValidateObjectiveUpdateAsync(world, stale, default));
        Assert.ThrowsAsync<AgentObjectiveConflictException>(() => store.CommitTurnAsync(world, "late", "heartbeat", stale, default));
        Assert.That((await store.SnapshotAsync(default)).AppliedTurnIds, Does.Not.Contain("late"));
    }

    [Test]
    public async Task InvalidTransitionAndMissingSnapshotCannotMutateGoalOrJournal()
    {
        var store = new MashaMemoryStore(_directory);
        var world = await Bind(store);
        var invalid = Decision("resume");
        invalid.JournalText = "UNCOMMITTED";
        Assert.ThrowsAsync<InvalidDataException>(() => store.CommitTurnAsync(world with { ObjectiveRevision = 0 }, "invalid", "heartbeat", invalid, default));
        Assert.ThrowsAsync<AgentObjectiveConflictException>(() => store.CommitTurnAsync(world, "unstamped", "heartbeat", Decision("set", "Rescue"), default));
        var archive = await store.SnapshotAsync(default);
        Assert.That(archive.Objective, Is.Null);
        Assert.That(archive.AppliedTurnIds, Is.Empty);
        Assert.That(archive.Worlds.SelectMany(w => w.Journal), Is.Empty);
    }

    [Test]
    public void ExternallyOversizedGoalCannotOverflowMandatoryPromptBudget()
    {
        var workspace = new MashaMemoryWorkspace(_directory);
        var episode = new MashaWorldEpisode { Id = "one", WorldKey = "world", AvatarNpcId = 901 };
        var archive = new MashaArchive { Worlds = [episode], Objective = new()
            { Revision = 1, Status = "active", Text = new string('x', 20000), Reason = new string('y', 20000), WorldKey = "world", AvatarNpcId = 901 } };
        var prompt = workspace.BuildPrompt(archive, episode, "");
        Assert.That(prompt.CharacterCount, Is.LessThanOrEqualTo(MashaMemoryWorkspace.MaxPromptCharacters));
        Assert.That(prompt.Text, Does.Contain(new string('x', AgentObjectivePolicy.TextLimit)));
        Assert.That(prompt.ObjectiveRevision, Is.EqualTo(1));
    }

    private static CompanionDecision Decision(string operation, string text = "") => new()
    {
        ObjectiveUpdate = new AgentObjectiveUpdate { Operation = operation, Text = text, Reason = "Observed situation requires this change" },
    };
    private static Task<MashaWorldHandle> Bind(MashaMemoryStore store, int npcId = 901, string worldId = "fixture") =>
        store.BindHexLiveWorldAsync(JsonSerializer.SerializeToElement(new { worldId, tick = 1200, seed = 7, dayLengthTicks = 24000 }), npcId, worldId, default);
}
