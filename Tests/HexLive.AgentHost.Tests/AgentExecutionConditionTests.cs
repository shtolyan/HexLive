using System.Text.Json;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentExecutionConditionTests
{
    [Test]
    public void BranchSurvivesSerializationAndDoesNotPretendToExecuteSkippedCommand()
    {
        var plan = Plan("return");
        Assert.That(AgentExecutionPlanPolicy.ConditionSatisfied(plan.Steps[0].Condition!,
            JsonSerializer.SerializeToElement(new { inventorySummary = new { freeSlots = 0 } })), Is.False);
        plan = AgentExecutionPlanPolicy.Branch(plan, plan.Revision);
        plan = JsonSerializer.Deserialize<AgentExecutionPlan>(JsonSerializer.Serialize(plan))!;
        Assert.That(plan.Cursor, Is.EqualTo(1));
        Assert.That(plan.Command, Is.Null);
        plan = AgentExecutionPlanPolicy.Prepare(plan, plan.Revision, "world", 901, 1);
        Assert.That(plan.Command!.StepId, Is.EqualTo("return"));
    }

    [Test]
    public void MissingObservationCannotBeTreatedAsZeroAndEmptyBranchPauses()
    {
        var plan = Plan("");
        Assert.Throws<InvalidDataException>(() => AgentExecutionPlanPolicy.ConditionSatisfied(
            plan.Steps[0].Condition!, JsonSerializer.SerializeToElement(new { })));
        plan = AgentExecutionPlanPolicy.Branch(plan, 0);
        Assert.That(plan.Status, Is.EqualTo("paused"));
        Assert.That(plan.Cursor, Is.Zero);
        Assert.That(plan.Reason, Is.EqualTo("ConditionsNotMet"));
    }

    [TestCase("collect")]
    [TestCase("missing")]
    public void SelfAndMissingTargetsAreRejected(string target) =>
        Assert.Throws<InvalidDataException>(() => Plan(target));

    [Test]
    public void ConditionsCannotBeHiddenInsideTheLegacySchema()
    {
        Assert.Throws<InvalidDataException>(() => AgentExecutionPlanPolicy.Recover(Plan("return") with { SchemaVersion = 1 }));
    }

    [Test]
    public void AcceptedCommandCannotBeSkippedWhenConditionsChange()
    {
        var plan = AgentExecutionPlanPolicy.Prepare(Plan("return"), 0, "world", 901, 1);
        Assert.Throws<InvalidOperationException>(() => AgentExecutionPlanPolicy.Branch(plan, plan.Revision));
    }

    [Test]
    public void ConditionPassesThroughModelContract()
    {
        var decision = new CompanionDecision { ExecutionPlanUpdate = new()
            { Operation = "replace", Reason = "Delivery", Steps = Plan("return").Steps } };
        var parsed = AgentProviders.ParseDecision(JsonSerializer.Serialize(decision), "heartbeat");
        Assert.That(parsed.ExecutionPlanUpdate!.Steps[0].Condition!.OnFalseStepId, Is.EqualTo("return"));
    }

    private static AgentExecutionPlan Plan(string target) => new()
    {
        SchemaVersion = 2, Id = "plan", WorldKey = "world", NpcId = 901, ObjectiveRevision = 1,
        Steps = Validated(target)
    };
    private static AgentExecutionStep[] Validated(string target)
    {
        AgentExecutionStep[] steps = [new("collect", "stop", JsonSerializer.SerializeToElement(new { }))
            { Condition = new("inventorySummary.freeSlots", "gte", 1, target) },
            new("return", "stop", JsonSerializer.SerializeToElement(new { }))];
        AgentExecutionPlanPolicy.ValidateUpdate(new() { Operation = "replace", Reason = "Fixture", Steps = steps });
        return steps;
    }
}
