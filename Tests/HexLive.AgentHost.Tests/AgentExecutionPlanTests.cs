using System.Text.Json;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentExecutionPlanTests
{
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
        Assert.Throws<InvalidDataException>(() => AgentExecutionPlanPolicy.Recover(plan with { SchemaVersion = 2 }));
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
