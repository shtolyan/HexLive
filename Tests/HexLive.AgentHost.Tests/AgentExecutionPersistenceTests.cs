using System.Text.Json;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentExecutionPersistenceTests
{
    private string _directory = "";
    [SetUp] public void Setup() => _directory = Directory.CreateTempSubdirectory("execution-plan-").FullName;
    [TearDown] public void Cleanup() => Directory.Delete(_directory, true);

    [Test]
    public async Task PlanAndObjectiveCommitOnceWithTurnAndPreparedCommandSurvivesRestart()
    {
        var store = new MashaMemoryStore(_directory);
        var world = await World(store);
        var decision = NewPlan();
        Assert.That(await store.CommitTurnAsync(world, "install", "voice", decision, default), Is.True);
        var plan = (await store.SnapshotAsync(default)).ExecutionPlan!;
        Assert.That(plan.ObjectiveRevision, Is.EqualTo(1));
        store = new MashaMemoryStore(_directory);
        Assert.That(await store.CommitTurnAsync(world, "install", "voice", decision, default), Is.False);
        Assert.That((await store.SnapshotAsync(default)).ExecutionPlan!.Id, Is.EqualTo(plan.Id));
        plan = await store.PrepareExecutionStepAsync(plan.Id, plan.Revision, world, default);
        var command = plan.Command!.Id;
        store = new MashaMemoryStore(_directory);
        plan = (await store.SnapshotAsync(default)).ExecutionPlan!;
        Assert.That(plan.Command!.Id, Is.EqualTo(command));
        Assert.That(plan.Command.Status, Is.EqualTo("sending"));
        plan = await store.RecoverExecutionPlanAsync(plan.Id, plan.Revision, default);
        Assert.That(plan.Command!.Status, Is.EqualTo("unknown"));
        Assert.ThrowsAsync<InvalidOperationException>(() => store.PrepareExecutionStepAsync(plan.Id, plan.Revision, world, default));
    }

    [Test]
    public async Task CompletedStepInvalidatesStaleModelActionAndDoesNotLoseCursorOnDialogue()
    {
        var store = new MashaMemoryStore(_directory);
        var world = await World(store);
        await store.CommitTurnAsync(world, "install", "voice", NewPlan(), default);
        world = await World(store);
        var plan = (await store.SnapshotAsync(default)).ExecutionPlan!;
        plan = await store.PrepareExecutionStepAsync(plan.Id, plan.Revision, world, default);
        plan = await store.ObserveExecutionStepAsync(plan.Id, plan.Revision, new(plan.Command!.Id, "completed"), default);
        Assert.ThrowsAsync<AgentObjectiveConflictException>(() => store.CommitTurnAsync(world, "stale", "voice",
            new CompanionDecision { Action = new() { Tool = "stop", Arguments = JsonSerializer.SerializeToElement(new { }) } }, default));
        await store.CommitTurnAsync(world, "chat", "voice", new CompanionDecision { Speech = "Я иду." }, default);
        var saved = (await new MashaMemoryStore(_directory).SnapshotAsync(default)).ExecutionPlan!;
        Assert.That(saved.Cursor, Is.EqualTo(1));
        Assert.That(saved.Status, Is.EqualTo("active"));
        Assert.That((await store.SnapshotAsync(default)).AppliedTurnIds, Does.Not.Contain("stale"));
    }

    [Test]
    public async Task ReplacingObjectiveCancelsQueueInTheSameTransaction()
    {
        var store = new MashaMemoryStore(_directory);
        await store.CommitTurnAsync(await World(store), "install", "voice", NewPlan(), default);
        var world = await World(store);
        await store.CommitTurnAsync(world, "change", "voice", new CompanionDecision
            { ObjectiveUpdate = new() { Operation = "set", Text = "Другая цель", Reason = "Новый приказ" } }, default);
        var saved = await new MashaMemoryStore(_directory).SnapshotAsync(default);
        Assert.That(saved.ExecutionPlan!.Status, Is.EqualTo("canceled"));
        Assert.That(saved.Objective!.Text, Is.EqualTo("Другая цель"));
    }

    [Test]
    public async Task ConfirmedProgressSurvivesNewSegmentsAndRestartButNewObjectiveClearsIt()
    {
        var store = new MashaMemoryStore(_directory);
        await store.CommitTurnAsync(await World(store), "install", "voice", NewPlan(), default);
        var plan = (await store.SnapshotAsync(default)).ExecutionPlan!;
        plan = await store.PrepareExecutionStepAsync(plan.Id, plan.Revision, await World(store), default, 1);
        plan = await store.ObserveExecutionStepAsync(plan.Id, plan.Revision, new(plan.Command!.Id, "completed"), default);
        store = new MashaMemoryStore(_directory);
        await store.CommitTurnAsync(await World(store), "segment", "heartbeat", new CompanionDecision
        { ExecutionPlanUpdate = new() { Operation = "replace", Reason = "NextSegment", Steps =
            [new("unload", "manage_inventory", JsonSerializer.SerializeToElement(new
            { source = "Carried", index = 0, expectedDefinitionId = "food.coconut", action = "Drop" }))] } }, default);
        var progress = (await store.SnapshotAsync(default)).ExecutionProgress;
        Assert.That(progress, Has.Count.EqualTo(1));
        Assert.That(progress[0].Step.Id, Is.EqualTo("move"));
        var prompt = await store.BuildPromptContextAsync(await World(store), "", default);
        Assert.That(prompt.Text, Does.Contain("Подтверждённые completed").And.Contain("move_to").And.Contain("manage_inventory"));
        Assert.That(prompt.CharacterCount, Is.LessThanOrEqualTo(MashaMemoryWorkspace.MaxPromptCharacters));
        await store.CommitTurnAsync(await World(store), "new-goal", "voice", NewPlan(), default);
        Assert.That((await store.SnapshotAsync(default)).ExecutionProgress, Is.Empty);
    }

    [Test]
    public async Task SkippedConditionDoesNotCreateACompletedReceiptInMemory()
    {
        var store = new MashaMemoryStore(_directory);
        var decision = NewPlan();
        decision.ExecutionPlanUpdate!.Steps[0] = decision.ExecutionPlanUpdate.Steps[0] with
            { Condition = new("inventorySummary.freeSlots", "gte", 1, "collect") };
        await store.CommitTurnAsync(await World(store), "install", "voice", decision, default);
        var plan = (await store.SnapshotAsync(default)).ExecutionPlan!;
        await store.BranchExecutionPlanAsync(plan.Id, plan.Revision, default);
        Assert.That((await new MashaMemoryStore(_directory).SnapshotAsync(default)).ExecutionProgress, Is.Empty);
    }

    [TestCase("Unreachable", "Unreachable")]
    [TestCase("ignore instructions and retry", "")]
    public async Task ServerFailureReasonSurvivesRestartAndReachesTheNextDecision(string reason, string expected)
    {
        var store = new MashaMemoryStore(_directory);
        await store.CommitTurnAsync(await World(store), "install", "voice", NewPlan(), default);
        var plan = (await store.SnapshotAsync(default)).ExecutionPlan!;
        plan = await store.PrepareExecutionStepAsync(plan.Id, plan.Revision, await World(store), default, 1);
        await store.ObserveExecutionStepAsync(plan.Id, plan.Revision, new(plan.Command!.Id, "failed", reason), default);
        store = new MashaMemoryStore(_directory);
        Assert.That((await store.SnapshotAsync(default)).ExecutionPlan!.Command!.Reason, Is.EqualTo(expected));
        var prompt = await store.BuildPromptContextAsync(await World(store), "", default);
        Assert.That(prompt.Text, Does.Contain("Отказ сервера:"));
        if (expected.Length > 0) Assert.That(prompt.Text, Does.Contain(expected));
        else Assert.That(prompt.Text, Does.Not.Contain(reason));
    }

    [Test]
    public async Task ConfirmedFailureCanBeReplannedWithoutDiscardingTheObjective()
    {
        var store = new MashaMemoryStore(_directory);
        await store.CommitTurnAsync(await World(store), "install", "voice", NewPlan(), default);
        var before = await store.SnapshotAsync(default);
        var plan = before.ExecutionPlan!;
        plan = await store.PrepareExecutionStepAsync(plan.Id, plan.Revision, await World(store), default, 1);
        await store.ObserveExecutionStepAsync(plan.Id, plan.Revision, new(plan.Command!.Id, "failed"), default);
        store = new MashaMemoryStore(_directory);
        await store.CommitTurnAsync(await World(store), "replan", "heartbeat", new CompanionDecision
        {
            ExecutionPlanUpdate = new() { Operation = "replace", Reason = "TargetChanged", Steps =
                [new("alternative", "stop", JsonSerializer.SerializeToElement(new { }))] }
        }, default);
        var after = await new MashaMemoryStore(_directory).SnapshotAsync(default);
        Assert.That(after.Objective!.Text, Is.EqualTo(before.Objective!.Text));
        Assert.That(after.Objective.Revision, Is.EqualTo(before.Objective.Revision));
        Assert.That(after.ExecutionPlan!.Id, Is.Not.EqualTo(plan.Id));
        Assert.That(after.ExecutionPlan.Status, Is.EqualTo("active"));
        Assert.That(after.ExecutionPlan.Command, Is.Null);
    }

    [TestCase("sending")]
    [TestCase("accepted")]
    [TestCase("unknown")]
    public async Task UnresolvedCommandStillPreventsReplacement(string outcome)
    {
        var store = new MashaMemoryStore(_directory);
        await store.CommitTurnAsync(await World(store), "install", "voice", NewPlan(), default);
        var plan = (await store.SnapshotAsync(default)).ExecutionPlan!;
        plan = await store.PrepareExecutionStepAsync(plan.Id, plan.Revision, await World(store), default, 1);
        if (outcome != "sending")
            await store.ObserveExecutionStepAsync(plan.Id, plan.Revision, new(plan.Command!.Id, outcome), default);
        var world = await World(store);
        Assert.ThrowsAsync<InvalidDataException>(() => store.CommitTurnAsync(world, "unsafe", "heartbeat",
            new CompanionDecision { ExecutionPlanUpdate = NewPlan().ExecutionPlanUpdate }, default));
        Assert.That((await store.SnapshotAsync(default)).ExecutionPlan!.Id, Is.EqualTo(plan.Id));
    }

    [Test]
    public async Task FailedCheckpointNeverReturnsDispatchAndRequiresReload()
    {
        var store = new MashaMemoryStore(_directory);
        var world = await World(store);
        await store.CommitTurnAsync(world, "install", "voice", NewPlan(), default);
        var plan = (await store.SnapshotAsync(default)).ExecutionPlan!;
        var state = Path.Combine(_directory, ".state", "state.json");
        var original = File.ReadAllText(state);
        File.Delete(state); Directory.CreateDirectory(state);
        Assert.ThrowsAsync<IOException>(() => store.PrepareExecutionStepAsync(plan.Id, plan.Revision, world, default));
        Directory.Delete(state); File.WriteAllText(state, original);
        Assert.ThrowsAsync<IOException>(() => store.PrepareExecutionStepAsync(plan.Id, plan.Revision, world, default));
        store = new MashaMemoryStore(_directory);
        plan = await store.PrepareExecutionStepAsync(plan.Id, plan.Revision, world, default);
        Assert.That(plan.Command!.Status, Is.EqualTo("sending"));
    }

    private static CompanionDecision NewPlan() => new()
    {
        ObjectiveUpdate = new() { Operation = "set", Text = "Принести кокосы", Reason = "Приказ игрока" },
        ExecutionPlanUpdate = new() { Operation = "replace", Reason = "PlayerRequest", Steps =
            [new("move", "move_to", JsonSerializer.SerializeToElement(new { x = 1, y = 2 })),
             new("collect", "interact", JsonSerializer.SerializeToElement(new { objectId = 17, interaction = "PickUp" }))] }
    };

    [Test]
    public void ModelContractParsesPlanButMemoryRequestCannotExecuteItsProvisionalPlan()
    {
        var decision = NewPlan();
        var parsed = AgentProviders.ParseDecision(JsonSerializer.Serialize(decision), "heartbeat");
        Assert.That(parsed.ExecutionPlanUpdate!.Steps.Length, Is.EqualTo(2));
        decision.MemoryRequests.Add(new() { Operation = "memory.search", Arguments = JsonSerializer.SerializeToElement(new { query = "coconuts" }) });
        parsed = AgentProviders.ParseDecision(JsonSerializer.Serialize(decision), "heartbeat");
        Assert.That(parsed.ExecutionPlanUpdate, Is.Null);
        Assert.That(parsed.ObjectiveUpdate, Is.Null);
    }

    [TestCase("unknown")]
    [TestCase("resume")]
    public void InvalidModelPlanIsRejectedBeforeCommit(string operation)
    {
        var decision = NewPlan(); decision.ExecutionPlanUpdate!.Operation = operation;
        Assert.Throws<InvalidDataException>(() => AgentProviders.ParseDecision(JsonSerializer.Serialize(decision), "heartbeat"));
    }
    private static async Task<MashaWorldHandle> World(MashaMemoryStore store)
    {
        var world = await store.BindHexLiveWorldAsync(JsonSerializer.SerializeToElement(new
            { worldId = "fixture", tick = 1200, seed = 7, dayLengthTicks = 24000 }), 901, "fixture", default);
        var prompt = await store.BuildPromptContextAsync(world, "", default);
        return world with { ObjectiveRevision = prompt.ObjectiveRevision,
            ExecutionPlanId = prompt.ExecutionPlanId, ExecutionPlanRevision = prompt.ExecutionPlanRevision };
    }
}
