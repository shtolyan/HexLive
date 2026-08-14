using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class LlmControlProviderTests
{
    [Test]
    public void MockProvider_ReturnsSameSafeDecisionForSameContext()
    {
        var context = new LlmDecisionContext(
            new EntityId(7),
            tick: 42,
            new Float2(1.5f, -2f),
            stateSummary: "idle",
            perceptionSummary: "Immediate THREAT nearby",
            memorySummary: "camp is north");
        var provider = new MockLlmControlProvider();

        var first = provider.Decide(context);
        var second = provider.Decide(context);

        Assert.Multiple(() =>
        {
            Assert.That(first.CommandKind, Is.EqualTo(LlmCommandKind.Stop));
            Assert.That(second.CommandKind, Is.EqualTo(first.CommandKind));
            Assert.That(second.Reason, Is.EqualTo(first.Reason));
            Assert.That(first.TargetNpcId, Is.Null);
            Assert.That(first.TargetObjectId, Is.Null);
            Assert.That(first.TargetMobId, Is.Null);
            Assert.That(first.TargetPosition, Is.Null);
            Assert.That(first.Interaction, Is.Null);
        });
    }

    [Test]
    public void MockProvider_ReturnsNoneForNeutralSummaries()
    {
        var context = new LlmDecisionContext(
            new EntityId(3),
            tick: 8,
            Float2.Zero,
            stateSummary: "idle and healthy",
            perceptionSummary: "campfire nearby",
            memorySummary: "slept recently");

        var decision = new MockLlmControlProvider().Decide(context);

        Assert.That(decision.CommandKind, Is.EqualTo(LlmCommandKind.None));
    }

    [Test]
    public void Decision_PreservesOptionalCommandPayload()
    {
        var position = new Float2(4f, 5f);
        var decision = new LlmDecision(
            LlmCommandKind.Interact,
            targetNpcId: new EntityId(2),
            targetObjectId: new ObjectId(11),
            targetMobId: 13,
            targetPosition: position,
            interaction: InteractionType.Harvest,
            manualControlEnabled: true,
            reason: "fixture");

        Assert.Multiple(() =>
        {
            Assert.That(decision.TargetNpcId, Is.EqualTo(new EntityId(2)));
            Assert.That(decision.TargetObjectId, Is.EqualTo(new ObjectId(11)));
            Assert.That(decision.TargetMobId, Is.EqualTo(13));
            Assert.That(decision.TargetPosition, Is.EqualTo(position));
            Assert.That(decision.Interaction, Is.EqualTo(InteractionType.Harvest));
            Assert.That(decision.ManualControlEnabled, Is.True);
            Assert.That(decision.Reason, Is.EqualTo("fixture"));
        });
    }
}

}
