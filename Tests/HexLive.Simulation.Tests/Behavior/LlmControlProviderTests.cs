using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class LlmControlProviderTests
{
    [Test]
    public void MockProvider_PublishesSameSafeDecisionForSameContext()
    {
        var context = new LlmDecisionContext(
            new EntityId(7),
            tick: 42,
            new Float2(1.5f, -2f),
            stateSummary: "idle",
            perceptionSummary: "Immediate THREAT nearby",
            memorySummary: "camp is north");
        using var provider = new MockLlmControlProvider();

        Assert.That(provider.TryRequest(Request(1, context)), Is.True);
        var first = WaitForResult(provider);
        Assert.That(provider.TryRequest(Request(2, context)), Is.True);
        var second = WaitForResult(provider);

        Assert.Multiple(() =>
        {
            Assert.That(first.Status, Is.EqualTo(LlmControlResultStatus.Completed));
            Assert.That(first.Decision.CommandKind, Is.EqualTo(LlmCommandKind.Stop));
            Assert.That(second.Decision.CommandKind, Is.EqualTo(first.Decision.CommandKind));
            Assert.That(second.Decision.Reason, Is.EqualTo(first.Decision.Reason));
            Assert.That(first.Decision.TargetNpcId, Is.Null);
            Assert.That(first.Decision.TargetObjectId, Is.Null);
            Assert.That(first.Decision.TargetMobId, Is.Null);
            Assert.That(first.Decision.TargetPosition, Is.Null);
            Assert.That(first.Decision.Interaction, Is.Null);
        });
    }

    [Test]
    public void MockProvider_PublishesNoneForNeutralSummaries()
    {
        var context = new LlmDecisionContext(
            new EntityId(3),
            tick: 8,
            Float2.Zero,
            stateSummary: "idle and healthy",
            perceptionSummary: "campfire nearby",
            memorySummary: "slept recently");
        using var provider = new MockLlmControlProvider();

        Assert.That(provider.TryRequest(Request(1, context)), Is.True);
        var result = WaitForResult(provider);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(LlmControlResultStatus.Completed));
            Assert.That(result.Decision.CommandKind, Is.EqualTo(LlmCommandKind.None));
            Assert.That(result.RequestId, Is.EqualTo(1));
            Assert.That(result.NpcId, Is.EqualTo(context.NpcId));
            Assert.That(result.IssuedTick, Is.EqualTo(context.Tick));
        });
    }

    [Test]
    public void QueuedProvider_ConvertsWorkerExceptionIntoFailedResult()
    {
        using var provider = new ThrowingProvider();
        var request = Request(9, new LlmDecisionContext(
            new EntityId(4), tick: 17, Float2.Zero));

        Assert.That(provider.TryRequest(request), Is.True);
        var result = WaitForResult(provider);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(LlmControlResultStatus.Failed));
            Assert.That(result.RequestId, Is.EqualTo(request.RequestId));
            Assert.That(result.NpcId, Is.EqualTo(request.NpcId));
            Assert.That(result.IssuedTick, Is.EqualTo(request.IssuedTick));
            Assert.That(result.Decision, Is.Null);
            Assert.That(result.ErrorType, Is.EqualTo(nameof(InvalidOperationException)));
            Assert.That(result.ErrorMessage, Is.EqualTo("fixture failure"));
        });
    }

    [Test]
    public void QueuedProvider_PublishesCancellationAndRejectsAfterDispose()
    {
        var provider = new NeverAnswerAsyncProvider();
        using var cancellation = new CancellationTokenSource();
        var request = new LlmControlRequest(
            3,
            new LlmDecisionContext(new EntityId(5), tick: 21, Float2.Zero),
            cancellation.Token);

        Assert.That(provider.TryRequest(request), Is.True);
        Assert.That(provider.Started.Wait(TimeSpan.FromSeconds(2)), Is.True);
        cancellation.Cancel();
        var result = WaitForResult(provider);

        provider.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(LlmControlResultStatus.Canceled));
            Assert.That(result.RequestId, Is.EqualTo(request.RequestId));
            Assert.That(provider.TryRequest(Request(
                4, new LlmDecisionContext(new EntityId(5), 22, Float2.Zero))), Is.False);
        });
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

    private static LlmControlRequest Request(long requestId, LlmDecisionContext context) =>
        new(requestId, context, CancellationToken.None);

    private static LlmControlResult WaitForResult(ILlmControlProvider provider)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(2))
        {
            if (provider.TryDequeueResult(out var result))
            {
                return result;
            }

            Thread.Sleep(1);
        }

        Assert.Fail("Provider did not publish a result within two seconds.");
        return null;
    }

    private sealed class ThrowingProvider : QueuedLlmControlProvider
    {
        protected override async Task<LlmDecision> DecideAsync(
            LlmDecisionContext context, CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new InvalidOperationException("fixture failure");
        }
    }

    private sealed class NeverAnswerAsyncProvider : QueuedLlmControlProvider
    {
        public ManualResetEventSlim Started { get; } = new(false);

        protected override async Task<LlmDecision> DecideAsync(
            LlmDecisionContext context, CancellationToken cancellationToken)
        {
            Started.Set();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return new LlmDecision(LlmCommandKind.None);
        }
    }
}

}
