using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class LlmControlSystemTests
{
    private bool _previousTraceEnabled;

    [SetUp]
    public void EnableDiagnosticTrace()
    {
        _previousTraceEnabled = SimTrace.Enabled;
        SimTrace.Enabled = true;
    }

    [TearDown]
    public void RestoreDiagnosticTrace()
    {
        SimTrace.Enabled = _previousTraceEnabled;
    }

    [Test]
    public void DefaultSystemIsDisabledAndInert()
    {
        var (world, npc) = Arena(tick: 23);
        using var system = new LlmControlSystem();

        var eventsBefore = world.Events.HighestSeq;
        var leaseBefore = npc.Mind.ManualControlLeaseRenewedAtSeconds;

        system.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(SpecLlmControl.Enabled, Is.False,
                "The shipped feature gate must remain opt-in.");
            Assert.That(npc.Mind.ManualControl, Is.False);
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.None));
            Assert.That(npc.Mind.ManualControlLeaseRenewedAtSeconds,
                Is.EqualTo(leaseBefore));
            Assert.That(world.Events.HighestSeq, Is.EqualTo(eventsBefore));
        });
    }

    [Test]
    public void HostDelayBudgetEndsAtLastNonStaleMediumPump()
    {
        var settings = new SimulationSettings();

        Assert.Multiple(() =>
        {
            Assert.That(settings.TickDeltaTime,
                Is.EqualTo(1f / SpecLlmControl.SimulationTicksPerSecond));
            Assert.That(settings.MediumInterval,
                Is.EqualTo(SpecLlmControl.ResultDrainIntervalTicks));
            Assert.That(SpecLlmControl.RequestTimeoutTicks, Is.EqualTo(64));
            Assert.That(SpecLlmControl.LastApplicableResultAgeTicks, Is.EqualTo(60));
            Assert.That(SpecLlmControl.RequestTimeoutTicks * settings.TickDeltaTime,
                Is.EqualTo(16f));
            Assert.That(SpecLlmControl.LastApplicableResultAgeTicks * settings.TickDeltaTime,
                Is.EqualTo(SpecLlmControl.MaxProviderDelaySeconds));
        });
    }

    [Test]
    public void ExplicitlyDisabledSystemDoesNotRequestProviderWork()
    {
        var (world, npc) = Arena(tick: 23);
        var provider = new ControllableProvider();
        var system = new LlmControlSystem(
            enabled: false,
            eligibleNpcIds: new[] { npc.Id },
            provider: provider);

        system.Run(world);
        system.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(provider.Requests, Is.Empty);
            Assert.That(provider.Disposed, Is.True);
            Assert.That(npc.Mind.ManualControl, Is.False);
            Assert.That(npc.Mind.ManualControlLeaseRenewedAtSeconds, Is.Null);
        });
    }

    [Test]
    public void FirstPumpOnlyRequestsAndLaterPumpAppliesThroughManualExecutor()
    {
        var (world, npc) = Arena(tick: 41);
        var provider = new ControllableProvider();
        using var system = SystemFor(
            provider, new[] { npc.Id }, cooldownTicks: 64, timeoutTicks: 64, cap: 1);

        system.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(provider.Requests, Has.Count.EqualTo(1));
            Assert.That(provider.Requests[0].IssuedTick, Is.EqualTo(41));
            Assert.That(npc.Mind.ManualControl, Is.False,
                "Issuing a request must not apply a same-pump result.");
        });

        provider.Complete(0, new LlmDecision(LlmCommandKind.Stop));
        system.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Mind.ManualControl, Is.True,
                "Stop must acquire manual mode through SetManualControlCommand.");
            Assert.That(npc.Mind.ManualControlLeaseRenewedAtSeconds, Is.Not.Null);
            Assert.That(world.Events.Items.Any(e =>
                e.EntityId == npc.Id.Value && e.Type == "ManualControlChanged"), Is.True);
            Assert.That(world.Events.Items.Any(e =>
                e.EntityId == npc.Id.Value && e.Type == "ManualOrderStopped"), Is.True);
            Assert.That(world.Events.Items.Any(e =>
                e.EntityId == npc.Id.Value && e.Type == "LlmControlAccepted" &&
                e.Message.Contains("Command=Stop") && e.Message.Contains("IssuedTick=41")), Is.True);
        });
    }

    [Test]
    public void BlockingAsyncProviderCannotBlockSimulationPump()
    {
        var (world, npc) = Arena(tick: 12);
        var provider = new BlockingAsyncProvider();
        using var system = SystemFor(
            provider, new[] { npc.Id }, cooldownTicks: 64, timeoutTicks: 64, cap: 1);

        var pump = Task.Run(() => system.Run(world));
        try
        {
            Assert.That(pump.Wait(TimeSpan.FromSeconds(1)), Is.True,
                "The pump must only enqueue provider work and return.");
            Assert.That(provider.Started.Wait(TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(npc.Mind.ManualControl, Is.False);
        }
        finally
        {
            provider.Release.Set();
            pump.Wait(TimeSpan.FromSeconds(2));
        }
    }

    [Test]
    public void SingleFlightPreventsDuplicateRequestsForOneNpc()
    {
        var (world, npc) = Arena(tick: 0);
        var provider = new ControllableProvider();
        using var system = SystemFor(
            provider, new[] { npc.Id }, cooldownTicks: 1, timeoutTicks: 20, cap: 2);

        system.Run(world);
        world.Tick = 1;
        system.Run(world);
        world.Tick = 5;
        system.Run(world);

        Assert.That(provider.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public void GlobalCapAndSortedRoundRobinRosterAreDeterministicAndFair()
    {
        var world = new WorldState { Tick = 0 };
        var one = AddNpc(world, 1);
        var two = AddNpc(world, 2);
        var three = AddNpc(world, 3);
        var four = AddNpc(world, 4);
        var provider = new ControllableProvider();
        using var system = SystemFor(
            provider,
            new[] { three.Id, one.Id, four.Id, two.Id, one.Id },
            cooldownTicks: 1,
            timeoutTicks: 100,
            cap: 2);

        system.Run(world);
        Assert.That(RequestedNpcValues(provider), Is.EqualTo(new[] { 1, 2 }));

        provider.Complete(0, new LlmDecision(LlmCommandKind.None));
        provider.Complete(1, new LlmDecision(LlmCommandKind.None));
        world.Tick = 1;
        system.Run(world);
        Assert.That(RequestedNpcValues(provider), Is.EqualTo(new[] { 1, 2, 3, 4 }));

        provider.Complete(2, new LlmDecision(LlmCommandKind.None));
        provider.Complete(3, new LlmDecision(LlmCommandKind.None));
        world.Tick = 2;
        system.Run(world);

        Assert.That(RequestedNpcValues(provider),
            Is.EqualTo(new[] { 1, 2, 3, 4, 1, 2 }));
    }

    [Test]
    public void ReturnedDecisionRevalidatesAllAdmissionGatesBeforeApplying()
    {
        var (world, npc) = Arena(tick: 10);
        var provider = new ControllableProvider();
        using var system = SystemFor(
            provider, new[] { npc.Id }, cooldownTicks: 64, timeoutTicks: 64, cap: 1);

        system.Run(world);
        npc.Mind.CurrentGoal = GoalType.GetFood;
        npc.Plan.Goal = GoalType.GetFood;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.Wait });
        provider.Complete(0, new LlmDecision(LlmCommandKind.Stop));
        world.Tick = 11;

        system.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Mind.ManualControl, Is.False);
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.GetFood));
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active));
            Assert.That(npc.Plan.Steps, Has.Count.EqualTo(1));
            Assert.That(world.Events.Items.Any(e =>
                e.EntityId == npc.Id.Value && e.Type == "LlmControlRejected" &&
                e.Message.Contains("Reason=GateChanged") &&
                e.Message.Contains("IssuedTick=10")), Is.True);
        });
    }

    [Test]
    public void ResultAtTimeoutBoundaryIsDroppedByIssuedTick()
    {
        var (world, npc) = Arena(tick: 30);
        var provider = new ControllableProvider();
        using var system = SystemFor(
            provider, new[] { npc.Id }, cooldownTicks: 100, timeoutTicks: 4, cap: 1);

        system.Run(world);
        provider.Complete(0, new LlmDecision(LlmCommandKind.Stop));
        world.Tick = 34;

        system.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(provider.Requests, Has.Count.EqualTo(1));
            Assert.That(npc.Mind.ManualControl, Is.False);
            Assert.That(npc.Mind.ManualControlLeaseRenewedAtSeconds, Is.Null);
            Assert.That(world.Events.Items.Any(e =>
                e.EntityId == npc.Id.Value && e.Type == "LlmControlDropped" &&
                e.Message.Contains("Reason=StaleDecision") &&
                e.Message.Contains("IssuedTick=30") &&
                e.Message.Contains("CurrentTick=34")), Is.True);
        });
    }

    [Test]
    public void NeverAnswerRequestTimesOutCancelsAndYieldsCapToNextNpc()
    {
        var world = new WorldState { Tick = 0 };
        var one = AddNpc(world, 1);
        var two = AddNpc(world, 2);
        var provider = new NeverAnswerProvider();
        using var system = SystemFor(
            provider, new[] { two.Id, one.Id }, cooldownTicks: 1, timeoutTicks: 4, cap: 1);

        system.Run(world);
        world.Tick = 3;
        system.Run(world);
        Assert.That(RequestedNpcValues(provider), Is.EqualTo(new[] { 1 }));

        world.Tick = 4;
        system.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(RequestedNpcValues(provider), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(provider.Requests[0].CancellationToken.IsCancellationRequested, Is.True);
            Assert.That(provider.Requests[1].CancellationToken.IsCancellationRequested, Is.False);
            Assert.That(world.Events.Items.Any(e =>
                e.EntityId == one.Id.Value && e.Type == "LlmControlTimedOut" &&
                e.Message.Contains("IssuedTick=0") && e.Message.Contains("CurrentTick=4")), Is.True);
        });
    }

    [Test]
    public void DisposeCancelsInflightDisposesProviderAndMakesPumpInert()
    {
        var (world, npc) = Arena(tick: 5);
        var provider = new NeverAnswerProvider();
        var system = SystemFor(
            provider, new[] { npc.Id }, cooldownTicks: 1, timeoutTicks: 20, cap: 1);

        system.Run(world);
        var request = provider.Requests.Single();
        system.Dispose();
        world.Tick = 25;
        system.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(request.CancellationToken.IsCancellationRequested, Is.True);
            Assert.That(provider.Disposed, Is.True);
            Assert.That(provider.Requests, Has.Count.EqualTo(1));
            Assert.That(npc.Mind.ManualControl, Is.False);
        });
    }

    [Test]
    public void InvalidReturnedDecisionIsRejectedWithoutManualDispatch()
    {
        var (world, npc) = Arena(tick: 53);
        var provider = new ControllableProvider();
        using var system = SystemFor(
            provider, new[] { npc.Id }, cooldownTicks: 64, timeoutTicks: 64, cap: 1);

        system.Run(world);
        var eventsBefore = world.Events.HighestSeq;
        provider.Complete(0, new LlmDecision(LlmCommandKind.MoveTo));
        system.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Mind.ManualControl, Is.False);
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.None));
            Assert.That(npc.Mind.ManualControlLeaseRenewedAtSeconds, Is.Null);
            Assert.That(world.Events.Items.Any(e =>
                e.Seq > eventsBefore && e.EntityId == npc.Id.Value &&
                e.Type == "LlmControlRejected" &&
                e.Message.Contains("Reason=InvalidDecision") &&
                e.Message.Contains("TargetPosition")), Is.True);
            Assert.That(world.Events.Items.Any(e =>
                e.Seq > eventsBefore && e.EntityId == npc.Id.Value &&
                e.Type.StartsWith("Manual", StringComparison.Ordinal)), Is.False);
        });
    }

    [Test]
    public void ManualAdmissionReasonIsPropagatedWithoutScanningTraceRing()
    {
        var (world, npc) = Arena(tick: 61);
        var provider = new ControllableProvider();
        using var system = SystemFor(
            provider, new[] { npc.Id }, cooldownTicks: 64, timeoutTicks: 64, cap: 1);

        system.Run(world);
        provider.Complete(0, new LlmDecision(
            LlmCommandKind.MoveTo,
            targetPosition: new Float2(100f, 100f)));
        system.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Mind.ManualControl, Is.True,
                "The control-mode command was admitted before MoveTo validation.");
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            Assert.That(world.Events.Items.Any(e =>
                e.EntityId == npc.Id.Value &&
                e.Type == "ManualCommandAdmission" &&
                e.Message.Contains("Order=MoveTo") &&
                e.Message.Contains("Status=Rejected") &&
                e.Message.Contains("Reason=Unreachable")), Is.True);
            Assert.That(world.Events.Items.Any(e =>
                e.EntityId == npc.Id.Value &&
                e.Type == "LlmControlRejected" &&
                e.Message.Contains("Reason=ManualCommandRejected") &&
                e.Message.Contains("AdmissionReason=Unreachable")), Is.True,
                "The LLM adapter must consume the typed admission result, including reason.");
        });
    }

    [Test]
    public void CooldownStartsAtRequestIssueAndPreventsImmediateRequery()
    {
        var (world, npc) = Arena(tick: 100);
        npc.Mind.ManualControl = true;
        var provider = new ControllableProvider();
        using var system = SystemFor(
            provider, new[] { npc.Id }, cooldownTicks: 4, timeoutTicks: 20, cap: 1);

        system.Run(world);
        provider.Complete(0, new LlmDecision(LlmCommandKind.None));
        world.Tick = 101;
        system.Run(world);
        world.Tick = 103;
        system.Run(world);
        Assert.That(provider.Requests, Has.Count.EqualTo(1));

        world.Tick = 104;
        system.Run(world);
        Assert.That(provider.Requests, Has.Count.EqualTo(2));
    }

    private static LlmControlSystem SystemFor(
        ILlmControlProvider provider,
        IEnumerable<EntityId> eligibleNpcIds,
        int cooldownTicks,
        int timeoutTicks,
        int cap) =>
        new(
            enabled: true,
            eligibleNpcIds: eligibleNpcIds,
            provider: provider,
            decisionCooldownTicks: cooldownTicks,
            requestTimeoutTicks: timeoutTicks,
            maxInFlightRequests: cap);

    private static int[] RequestedNpcValues(RecordingProvider provider) =>
        provider.Requests.Select(request => request.NpcId.Value).ToArray();

    private static (WorldState world, NPCState npc) Arena(int tick)
    {
        var world = new WorldState { Tick = tick };
        return (world, AddNpc(world, id: 7));
    }

    private static NPCState AddNpc(WorldState world, int id)
    {
        var npc = new NPCState
        {
            Id = new EntityId(id),
            Faction = Faction.Colony,
            Health = 1f
        };
        world.Entities.Npcs.Add(npc.Id, npc);
        return npc;
    }

    private abstract class RecordingProvider : ILlmControlProvider
    {
        private readonly object _gate = new();
        private readonly List<LlmControlRequest> _requests = new();

        public IReadOnlyList<LlmControlRequest> Requests
        {
            get
            {
                lock (_gate)
                {
                    return _requests.ToArray();
                }
            }
        }

        public bool Disposed { get; private set; }

        public virtual bool TryRequest(LlmControlRequest request)
        {
            lock (_gate)
            {
                if (Disposed)
                {
                    return false;
                }

                _requests.Add(request);
                return true;
            }
        }

        public abstract bool TryDequeueResult(out LlmControlResult result);

        public virtual void Dispose()
        {
            lock (_gate)
            {
                Disposed = true;
            }
        }
    }

    private sealed class ControllableProvider : RecordingProvider
    {
        private readonly ConcurrentQueue<LlmControlResult> _results = new();

        public void Complete(int requestIndex, LlmDecision decision)
        {
            var request = Requests[requestIndex];
            _results.Enqueue(LlmControlResult.Completed(request, decision));
        }

        public override bool TryDequeueResult(out LlmControlResult result) =>
            _results.TryDequeue(out result);
    }

    private sealed class NeverAnswerProvider : RecordingProvider
    {
        public override bool TryDequeueResult(out LlmControlResult result)
        {
            result = null;
            return false;
        }
    }

    private sealed class BlockingAsyncProvider : QueuedLlmControlProvider
    {
        public ManualResetEventSlim Started { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);

        protected override Task<LlmDecision> DecideAsync(
            LlmDecisionContext context, CancellationToken cancellationToken)
        {
            Started.Set();
            Release.Wait(cancellationToken);
            return Task.FromResult(new LlmDecision(LlmCommandKind.None));
        }
    }
}

}
