using System.Text.Json;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentExecutionPlanTests
{
    [Test]
    public void AFalseConditionExitsRemainingIterationsWithoutPretendingTheyCompleted()
    {
        var initial = Plan();
        var plan = AgentExecutionPlanPolicy.Create(Objective(), "world", 901,
            [initial.Steps[0] with { Repeat = 3, Condition = new("inventorySummary.freeSlots", "gte", 1, "unload") }, initial.Steps[1]]);
        plan = AgentExecutionPlanPolicy.Prepare(plan, plan.Revision, "world", 901, 7);
        plan = AgentExecutionPlanPolicy.Observe(plan, plan.Revision, new(plan.Command!.Id, "completed"));
        Assert.That(plan.Iteration, Is.EqualTo(1));
        Assert.That(AgentExecutionPlanPolicy.ConditionSatisfied(plan.Steps[0].Condition!,
            JsonSerializer.SerializeToElement(new { inventorySummary = new { freeSlots = 0 } })), Is.False);
        plan = AgentExecutionPlanPolicy.Branch(plan, plan.Revision);
        Assert.That(plan.Cursor, Is.EqualTo(1));
        Assert.That(plan.Iteration, Is.Zero);
        Assert.That(plan.Command, Is.Null);
        Assert.That(plan.Status, Is.EqualTo("active"));
    }

    [TestCase("adrenalineTicksRemaining")]
    [TestCase("idleRestCooldownTicksRemaining")]
    public void RecoveryTimerCanSkipAnUnavailableRestWithoutACommand(string timer)
    {
        var initial = Plan();
        var condition = new AgentExecutionCondition("restReadiness." + timer, "lte", 0, "unload");
        var plan = AgentExecutionPlanPolicy.Create(Objective(), "world", 901,
            [initial.Steps[0] with { Condition = condition }, initial.Steps[1]]);
        var waiting = JsonSerializer.SerializeToElement(new { restReadiness = new Dictionary<string, long> { [timer] = 100 } });
        Assert.That(AgentExecutionPlanPolicy.ConditionSatisfied(condition, waiting), Is.False);
        var skipped = AgentExecutionPlanPolicy.Branch(plan, plan.Revision);
        Assert.That(skipped.Cursor, Is.EqualTo(1));
        Assert.That(skipped.Command, Is.Null);
        Assert.That(AgentExecutionPlanPolicy.ConditionSatisfied(condition,
            JsonSerializer.SerializeToElement(new { restReadiness = new Dictionary<string, long> { [timer] = 0 } })), Is.True);
        Assert.That(Assert.Throws<InvalidDataException>(() => AgentExecutionPlanPolicy.ConditionSatisfied(
            condition, JsonSerializer.SerializeToElement(new { })))!.Message, Is.EqualTo("ExecutionObservationMissing"),
            "A server without readiness fields must not authorize a speculative rest.");
    }

    [Test]
    public void RepeatCheckpointSurvivesRestartAndNeverReusesThePreviousCommandId()
    {
        var plan = Plan() with { SchemaVersion = 3, Steps =
            [new("craft", "craft_item", JsonSerializer.SerializeToElement(new { recipeGoal = "CraftRope" })) { Repeat = 3 }] };
        plan = AgentExecutionPlanPolicy.Prepare(plan, plan.Revision, "world", 901, 7);
        var firstId = plan.Command!.Id;
        plan = AgentExecutionPlanPolicy.Observe(plan, plan.Revision, new(firstId, "completed"));
        Assert.That(plan.Cursor, Is.Zero);
        Assert.That(plan.Iteration, Is.EqualTo(1));
        plan = AgentExecutionPlanPolicy.Recover(JsonSerializer.Deserialize<AgentExecutionPlan>(JsonSerializer.Serialize(plan))!);
        plan = AgentExecutionPlanPolicy.Resume(plan, plan.Revision, "world", 901, 7);
        plan = AgentExecutionPlanPolicy.Prepare(plan, plan.Revision, "world", 901, 7);
        Assert.That(plan.Command!.Id, Is.Not.EqualTo(firstId));
        Assert.Throws<InvalidOperationException>(() => AgentExecutionPlanPolicy.Observe(plan, plan.Revision, new(firstId, "completed")));
        plan = AgentExecutionPlanPolicy.Observe(plan, plan.Revision, new(plan.Command.Id, "failed", "NoIngredients"));
        Assert.That(plan.Status, Is.EqualTo("paused"));
        Assert.That(plan.Iteration, Is.EqualTo(1));
        Assert.Throws<InvalidOperationException>(() => AgentExecutionPlanPolicy.Prepare(plan, plan.Revision, "world", 901, 7));
    }

    [Test]
    public void RepeatBudgetAndOldSchemaCannotSilentlyExecuteRepeatedCommands()
    {
        var steps = Enumerable.Range(0, 5).Select(i => new AgentExecutionStep("step-" + i, "stop",
            JsonSerializer.SerializeToElement(new { })) { Repeat = 64 }).ToArray();
        Assert.Throws<InvalidDataException>(() => AgentExecutionPlanPolicy.ValidateUpdate(
            new() { Operation = "replace", Reason = "Fixture", Steps = steps }));
        var old = Plan() with { Steps = [steps[0]] };
        Assert.Throws<InvalidDataException>(() => AgentExecutionPlanPolicy.Recover(old));
    }

    [TestCase("completed", true)]
    [TestCase("active", false)]
    [TestCase("unknown", false)]
    public void LegacyContinuationUsesDurableQueueWithoutOverwritingPendingWork(string status, bool allowed)
    {
        var plan = Plan();
        plan = status == "unknown" ? AgentExecutionPlanPolicy.Recover(
            AgentExecutionPlanPolicy.Prepare(plan, 0, "world", 901, 7)) : plan with { Status = status };
        var state = new MashaArchive { Objective = new() { Status = "active", Revision = 7, WorldKey = "world", AvatarNpcId = 901 }, ExecutionPlan = plan };
        var decision = new CompanionDecision { Action = new() { Tool = "move_to", Arguments = JsonSerializer.SerializeToElement(new { x = 1, y = 2 }) } };
        if (!allowed)
        {
            Assert.Throws<AgentActionValidationException>(() => AgentExecutionPlanPolicy.NormalizeContinuation(decision, state, "world", 901));
            Assert.That(decision.Action, Is.Not.Null);
            Assert.That(decision.ExecutionPlanUpdate, Is.Null);
            return;
        }
        AgentExecutionPlanPolicy.NormalizeContinuation(decision, state, "world", 901);
        Assert.That(decision.Action, Is.Null);
        Assert.That(decision.ExecutionPlanUpdate!.Steps.Single().Arguments.GetProperty("x").GetInt32(), Is.EqualTo(1));
        AgentExecutionPlanPolicy.ValidateUpdate(decision.ExecutionPlanUpdate);
    }

    [Test]
    public void EmergencyPauseKeepsOrdinaryActionOutsideThePausedObjective()
    {
        var state = new MashaArchive { Objective = new() { Status = "active", Revision = 7, WorldKey = "world", AvatarNpcId = 901 }, ExecutionPlan = Plan() };
        var decision = new CompanionDecision { ObjectiveUpdate = new() { Operation = "pause", Reason = "Threat" },
            Action = new() { Tool = "move_to", Arguments = JsonSerializer.SerializeToElement(new { x = 1, y = 2 }) } };
        AgentExecutionPlanPolicy.NormalizeContinuation(decision, state, "world", 901);
        Assert.That(decision.Action, Is.Not.Null);
        Assert.That(decision.ExecutionPlanUpdate, Is.Null);
    }

    [Test]
    public void AcknowledgementDoesNotCompleteStepAndDuplicateReceiptCannotCompleteNextStep()
    {
        var plan = Plan();
        plan = AgentExecutionPlanPolicy.Prepare(plan, 0, "world", 901, 7);
        var first = plan.Command!.Id;
        plan = AgentExecutionPlanPolicy.Observe(plan, plan.Revision, new(first, "accepted"));
        Assert.That(plan.Cursor, Is.Zero);
        Assert.Throws<InvalidOperationException>(() => AgentExecutionPlanPolicy.Prepare(plan, plan.Revision, "world", 901, 7));
        plan = AgentExecutionPlanPolicy.Observe(plan, plan.Revision, new(first, "completed"));
        plan = AgentExecutionPlanPolicy.Prepare(plan, plan.Revision, "world", 901, 7);
        Assert.That(plan.Command!.Id, Is.Not.EqualTo(first));
        Assert.Throws<InvalidOperationException>(() => AgentExecutionPlanPolicy.Observe(plan, plan.Revision, new(first, "completed")));
        Assert.That(plan.Cursor, Is.EqualTo(1));
        plan = AgentExecutionPlanPolicy.Observe(plan, plan.Revision, new(plan.Command.Id, "completed"));
        Assert.That(plan.Status, Is.EqualTo("completed"));
        Assert.That(plan.Cursor, Is.EqualTo(2));
    }

    [TestCase("sending")]
    [TestCase("accepted")]
    public void RestartAfterSendCannotReplayOrResumeUntilCommandSpecificOutcomeIsKnown(string status)
    {
        var plan = AgentExecutionPlanPolicy.Prepare(Plan(), 0, "world", 901, 7);
        if (status == "accepted") plan = AgentExecutionPlanPolicy.Observe(plan, plan.Revision, new(plan.Command!.Id, "accepted"));
        var persisted = JsonSerializer.Serialize(plan);
        plan = AgentExecutionPlanPolicy.Recover(JsonSerializer.Deserialize<AgentExecutionPlan>(persisted)!);
        Assert.That(plan.Status, Is.EqualTo("paused"));
        Assert.That(plan.Command!.Status, Is.EqualTo("unknown"));
        Assert.Throws<InvalidOperationException>(() => AgentExecutionPlanPolicy.Resume(plan, plan.Revision, "world", 901, 7));
        Assert.Throws<InvalidOperationException>(() => AgentExecutionPlanPolicy.Prepare(plan, plan.Revision, "world", 901, 7));
        plan = AgentExecutionPlanPolicy.Observe(plan, plan.Revision, new(plan.Command.Id, "completed"));
        Assert.That(plan.Cursor, Is.EqualTo(1));
        Assert.That(plan.Status, Is.EqualTo("paused"));
        plan = AgentExecutionPlanPolicy.Resume(plan, plan.Revision, "world", 901, 7);
        Assert.That(plan.Status, Is.EqualTo("active"));
    }

    [Test]
    public void RestPauseRetainsProgressAndCompletionWhilePausedDoesNotResumeAutomatically()
    {
        var plan = AgentExecutionPlanPolicy.Prepare(Plan(), 0, "world", 901, 7);
        plan = AgentExecutionPlanPolicy.Pause(plan, plan.Revision, "NeedsSleep");
        plan = AgentExecutionPlanPolicy.Observe(plan, plan.Revision, new(plan.Command!.Id, "completed"));
        Assert.That(plan.Status, Is.EqualTo("paused"));
        Assert.That(plan.Reason, Is.EqualTo("NeedsSleep"));
        Assert.That(plan.Cursor, Is.EqualTo(1));
        plan = AgentExecutionPlanPolicy.Resume(plan, plan.Revision, "world", 901, 7);
        plan = AgentExecutionPlanPolicy.Prepare(plan, plan.Revision, "world", 901, 7);
        Assert.That(plan.Command!.StepId, Is.EqualTo("unload"));
    }

    [Test]
    public void CancelIsTerminalAndDoesNotPretendToUndoAlreadySentCommand()
    {
        var plan = AgentExecutionPlanPolicy.Prepare(Plan(), 0, "world", 901, 7);
        var commandId = plan.Command!.Id;
        plan = AgentExecutionPlanPolicy.Cancel(plan, plan.Revision);
        plan = AgentExecutionPlanPolicy.Recover(plan);
        Assert.That(plan.Command!.Id, Is.EqualTo(commandId));
        Assert.That(plan.Status, Is.EqualTo("canceled"));
        Assert.Throws<InvalidOperationException>(() => AgentExecutionPlanPolicy.Observe(plan, plan.Revision, new(commandId, "completed")));
        Assert.Throws<InvalidOperationException>(() => AgentExecutionPlanPolicy.Resume(plan, plan.Revision, "world", 901, 7));
    }

    [TestCase("other", 901, 7)]
    [TestCase("world", 902, 7)]
    [TestCase("world", 901, 8)]
    public void DifferentBodyWorldOrObjectiveCannotDispatchOrResume(string world, int npc, long objective)
    {
        var plan = Plan();
        Assert.Throws<InvalidOperationException>(() => AgentExecutionPlanPolicy.Prepare(plan, plan.Revision, world, npc, objective));
        plan = AgentExecutionPlanPolicy.Recover(plan);
        Assert.Throws<InvalidOperationException>(() => AgentExecutionPlanPolicy.Resume(plan, plan.Revision, world, npc, objective));
        Assert.That(plan.Cursor, Is.Zero);
    }

    [Test]
    public void FailureAndOldRevisionCannotAdvancePlan()
    {
        var plan = AgentExecutionPlanPolicy.Prepare(Plan(), 0, "world", 901, 7);
        Assert.Throws<InvalidOperationException>(() => AgentExecutionPlanPolicy.Observe(plan, 0, new(plan.Command!.Id, "completed")));
        plan = AgentExecutionPlanPolicy.Observe(plan, plan.Revision, new(plan.Command!.Id, "failed"));
        Assert.That(plan.Status, Is.EqualTo("paused"));
        Assert.That(plan.Cursor, Is.Zero);
        Assert.That(AgentExecutionPlanPolicy.Observe(plan, plan.Revision, new(plan.Command!.Id, "failed")), Is.SameAs(plan));
        Assert.Throws<InvalidOperationException>(() => AgentExecutionPlanPolicy.Observe(plan, plan.Revision, new(plan.Command!.Id, "accepted")));
        Assert.Throws<InvalidOperationException>(() => AgentExecutionPlanPolicy.Resume(plan, plan.Revision, "world", 901, 7));
    }

    [Test]
    public void CorruptedOrFutureStateIsRejectedBeforeRecoveryCanDispatchAnything()
    {
        var plan = Plan();
        Assert.Throws<InvalidDataException>(() => AgentExecutionPlanPolicy.Recover(plan with { SchemaVersion = 99 }));
        Assert.Throws<InvalidDataException>(() => AgentExecutionPlanPolicy.Recover(plan with { Cursor = -1 }));
        Assert.Throws<InvalidDataException>(() => AgentExecutionPlanPolicy.Recover(plan with { Status = "completed" }));
        Assert.Throws<InvalidDataException>(() => AgentExecutionPlanPolicy.Recover(plan with { Command = new("command", "other-step", "accepted") }));
    }

    [Test]
    public void DefinitionOwnsItsJsonAndRejectsDuplicateStepsOrUnboundedQueue()
    {
        var objective = Objective();
        AgentExecutionPlan plan;
        using (var document = JsonDocument.Parse("{\"x\":1,\"y\":2}"))
            plan = AgentExecutionPlanPolicy.Create(objective, "world", 901,
                [new("move", "move_to", document.RootElement)]);
        Assert.That(plan.Steps[0].Arguments.GetProperty("x").GetInt32(), Is.EqualTo(1));
        Assert.Throws<InvalidDataException>(() => AgentExecutionPlanPolicy.Create(objective, "world", 901,
            [plan.Steps[0], plan.Steps[0]]));
        Assert.Throws<InvalidDataException>(() => AgentExecutionPlanPolicy.Create(objective, "world", 901,
            Enumerable.Range(0, 65).Select(i => plan.Steps[0] with { Id = "step-" + i })));
        Assert.Throws<InvalidDataException>(() => AgentExecutionPlanPolicy.Create(objective, "world", 901,
            [plan.Steps[0] with { Tool = "query_known_objects" }]));
    }

    [Test]
    public void ReplacingPlanAllocatesANewCommandIdentityEvenForIdenticalSteps()
    {
        var first = AgentExecutionPlanPolicy.Prepare(Plan(), 0, "world", 901, 7);
        var replacement = AgentExecutionPlanPolicy.Prepare(Plan(), 0, "world", 901, 7);
        Assert.That(first.Command!.Id, Is.Not.EqualTo(replacement.Command!.Id));
        Assert.Throws<InvalidOperationException>(() => AgentExecutionPlanPolicy.Observe(replacement, replacement.Revision,
            new(first.Command.Id, "completed")));
    }

    private static AgentObjective Objective() => new()
        { Status = "active", Revision = 7, WorldKey = "world", AvatarNpcId = 901, Text = "Bring coconuts to camp" };
    private static AgentExecutionPlan Plan() => AgentExecutionPlanPolicy.Create(Objective(), "world", 901,
        [new("collect", "interact", JsonSerializer.SerializeToElement(new { objectId = 17 })),
         new("unload", "manage_inventory", JsonSerializer.SerializeToElement(new { action = "Drop" }))]);
}
