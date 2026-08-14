using System.Linq;
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
        var system = new LlmControlSystem();

        var eventsBefore = world.Events.HighestSeq;
        var inputTickBefore = npc.Mind.LastManualInputTick;

        system.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(SpecLlmControl.Enabled, Is.False,
                "The shipped feature gate must remain opt-in.");
            Assert.That(npc.Mind.ManualControl, Is.False);
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.None));
            Assert.That(npc.Mind.LastManualInputTick, Is.EqualTo(inputTickBefore));
            Assert.That(world.Events.HighestSeq, Is.EqualTo(eventsBefore));
        });
    }

    [Test]
    public void ExplicitlyDisabledSystemDoesNotCallProvider()
    {
        var (world, npc) = Arena(tick: 23);
        var provider = new CountingProvider(new LlmDecision(LlmCommandKind.Stop));
        var system = new LlmControlSystem(
            enabled: false,
            eligibleNpcIds: new[] { npc.Id },
            provider: provider);

        var eventsBefore = world.Events.HighestSeq;
        var inputTickBefore = npc.Mind.LastManualInputTick;

        system.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(provider.CallCount, Is.Zero);
            Assert.That(npc.Mind.ManualControl, Is.False);
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.None));
            Assert.That(npc.Mind.LastManualInputTick, Is.EqualTo(inputTickBefore));
            Assert.That(world.Events.HighestSeq, Is.EqualTo(eventsBefore));
        });
    }

    [Test]
    public void OnlySelectedIdleNpcIsEligibleForProviderCall()
    {
        var (world, selected) = Arena(tick: 31);
        var selectedBusy = AddNpc(world, id: 8);
        selectedBusy.Mind.CurrentGoal = GoalType.GetFood;
        selectedBusy.Plan.Goal = GoalType.GetFood;
        selectedBusy.Plan.Status = PlanStatus.Active;
        selectedBusy.Plan.Steps.Add(new PlanStep { Type = PlanStepType.Wait });
        var unselectedIdle = AddNpc(world, id: 9);

        var provider = new RecordingProvider(new LlmDecision(LlmCommandKind.None));
        var system = new LlmControlSystem(
            enabled: true,
            eligibleNpcIds: new[] { selected.Id, selectedBusy.Id },
            provider: provider);

        system.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(provider.NpcIds, Is.EqualTo(new[] { selected.Id }));
            Assert.That(selected.Mind.ManualControl, Is.False,
                "A valid None decision must not acquire manual control.");
            Assert.That(selectedBusy.Mind.CurrentGoal, Is.EqualTo(GoalType.GetFood));
            Assert.That(selectedBusy.Plan.Status, Is.EqualTo(PlanStatus.Active));
            Assert.That(unselectedIdle.Mind.ManualControl, Is.False);
            Assert.That(unselectedIdle.Mind.LastManualInputTick, Is.EqualTo(0));
        });
    }

    [Test]
    public void ValidProviderDecisionDispatchesThroughManualCommandExecutor()
    {
        var (world, npc) = Arena(tick: 41);
        var provider = new CountingProvider(new LlmDecision(LlmCommandKind.Stop));
        var system = new LlmControlSystem(
            enabled: true,
            eligibleNpcIds: new[] { npc.Id },
            provider: provider);

        system.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(provider.CallCount, Is.EqualTo(1));
            Assert.That(npc.Mind.ManualControl, Is.True,
                "Stop must first acquire manual mode through SetManualControlCommand.");
            Assert.That(npc.Mind.LastManualInputTick, Is.EqualTo(world.Tick));
            Assert.That(world.Events.Items.Any(e =>
                e.EntityId == npc.Id.Value && e.Type == "ManualControlChanged"), Is.True);
            Assert.That(world.Events.Items.Any(e =>
                e.EntityId == npc.Id.Value && e.Type == "ManualOrderStopped"), Is.True);
            Assert.That(world.Events.Items.Any(e =>
                e.EntityId == npc.Id.Value && e.Type == "LlmControlAccepted" &&
                e.Message.Contains("Command=Stop")), Is.True);
        });
    }

    [Test]
    public void InvalidProviderDecisionIsRejectedWithoutDispatch()
    {
        var (world, npc) = Arena(tick: 53);
        var provider = new CountingProvider(new LlmDecision(LlmCommandKind.MoveTo));
        var system = new LlmControlSystem(
            enabled: true,
            eligibleNpcIds: new[] { npc.Id },
            provider: provider);

        var eventsBefore = world.Events.HighestSeq;
        var inputTickBefore = npc.Mind.LastManualInputTick;

        system.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(provider.CallCount, Is.EqualTo(1));
            Assert.That(npc.Mind.ManualControl, Is.False);
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.None));
            Assert.That(npc.Mind.LastManualInputTick, Is.EqualTo(inputTickBefore));
            Assert.That(world.Events.Items.Any(e =>
                e.Seq > eventsBefore && e.EntityId == npc.Id.Value &&
                e.Type == "LlmControlRejected" &&
                e.Message.Contains("Reason=InvalidDecision") &&
                e.Message.Contains("TargetPosition")), Is.True);
            Assert.That(world.Events.Items.Any(e =>
                e.Seq > eventsBefore && e.EntityId == npc.Id.Value &&
                e.Type.StartsWith("Manual", System.StringComparison.Ordinal)), Is.False,
                "Structurally invalid provider output must not enter the manual-command path.");
        });
    }

    [Test]
    public void ActivePlanIsNotProviderReplacedOrSpammed()
    {
        var (world, npc) = Arena(tick: 10);
        npc.Mind.ManualControl = true;
        npc.Mind.CurrentGoal = GoalType.PlayerOrder;
        npc.Plan.Goal = GoalType.PlayerOrder;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.Wait });
        npc.Mind.LastManualInputTick = 7;

        var provider = new CountingProvider(new LlmDecision(LlmCommandKind.Stop));
        var system = new LlmControlSystem(
            enabled: true,
            eligibleNpcIds: new[] { npc.Id },
            provider: provider);

        for (var i = 0; i < 5; i++)
        {
            world.Tick += SpecLlmControl.DecisionCooldownTicks;
            system.Run(world);
        }

        Assert.Multiple(() =>
        {
            Assert.That(provider.CallCount, Is.Zero);
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active));
            Assert.That(npc.Plan.Steps, Has.Count.EqualTo(1));
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.PlayerOrder));
            Assert.That(npc.Mind.LastManualInputTick, Is.EqualTo(7));
        });
    }

    [Test]
    public void CooldownPreventsRepeatedIdleDecisionsEachRun()
    {
        var (world, npc) = Arena(tick: 100);
        npc.Mind.ManualControl = true;

        var provider = new CountingProvider(new LlmDecision(LlmCommandKind.Stop));
        var system = new LlmControlSystem(
            enabled: true,
            eligibleNpcIds: new[] { npc.Id },
            provider: provider);

        system.Run(world);
        world.Tick += 1;
        system.Run(world);
        world.Tick += SpecLlmControl.DecisionCooldownTicks - 1;
        system.Run(world);

        Assert.That(provider.CallCount, Is.EqualTo(2),
            "The second run is inside the cooldown; the boundary run is eligible again.");
    }

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

    private sealed class CountingProvider : ILlmControlProvider
    {
        private readonly LlmDecision _decision;

        public CountingProvider(LlmDecision decision)
        {
            _decision = decision;
        }

        public int CallCount { get; private set; }

        public LlmDecision Decide(LlmDecisionContext context)
        {
            CallCount += 1;
            return _decision;
        }
    }

    private sealed class RecordingProvider : ILlmControlProvider
    {
        private readonly LlmDecision _decision;

        public RecordingProvider(LlmDecision decision)
        {
            _decision = decision;
        }

        public System.Collections.Generic.List<EntityId> NpcIds { get; } = new();

        public LlmDecision Decide(LlmDecisionContext context)
        {
            NpcIds.Add(context.NpcId);
            return _decision;
        }
    }
}

}
