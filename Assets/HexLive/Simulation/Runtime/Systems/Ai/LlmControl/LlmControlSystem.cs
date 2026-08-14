using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §32.15: opt-in adapter between an <see cref="ILlmControlProvider"/> and the
/// existing §121 manual-command path. It does not replace decision or planning,
/// and providers never receive the mutable world.
/// </summary>
public sealed class LlmControlSystem : ISimulationSystem
{
    private readonly bool _enabled;
    private readonly List<EntityId> _eligibleNpcIds;
    private readonly ILlmControlProvider _provider;
    private readonly int _decisionCooldownTicks;
    private readonly Dictionary<EntityId, int> _nextDecisionTicks = new();

    /// <summary>
    /// Shipped registration path. Both safeguards are deliberate: the feature
    /// gate defaults false and no NPC is selected. This constructor never creates
    /// a provider while disabled, so ordinary simulation has no hidden API path.
    /// </summary>
    public LlmControlSystem()
        : this(
            SpecLlmControl.Enabled,
            Array.Empty<EntityId>(),
            provider: null,
            decisionCooldownTicks: SpecLlmControl.DecisionCooldownTicks)
    {
    }

    /// <summary>
    /// Explicit host/test path. When enabled without a provider it uses the
    /// deterministic offline mock; there is no network-backed implementation in
    /// the simulation assembly.
    /// </summary>
    public LlmControlSystem(
        bool enabled,
        IEnumerable<EntityId> eligibleNpcIds,
        ILlmControlProvider provider = null,
        int decisionCooldownTicks = SpecLlmControl.DecisionCooldownTicks)
    {
        if (eligibleNpcIds is null)
        {
            throw new ArgumentNullException(nameof(eligibleNpcIds));
        }

        if (decisionCooldownTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(decisionCooldownTicks), "Decision cooldown must be positive.");
        }

        _enabled = enabled;
        _decisionCooldownTicks = decisionCooldownTicks;
        _provider = enabled ? provider ?? new MockLlmControlProvider() : provider;

        var uniqueIds = new HashSet<EntityId>(eligibleNpcIds);
        _eligibleNpcIds = new List<EntityId>(uniqueIds);
        _eligibleNpcIds.Sort((left, right) => left.Value.CompareTo(right.Value));
    }

    public string Name => nameof(LlmControlSystem);

    public TickLayer Layer => TickLayer.Medium;

    public void Run(WorldState world)
    {
        if (!_enabled ||
            _provider is null ||
            _eligibleNpcIds.Count == 0 ||
            !Spec121.ManualControlEnabled)
        {
            return;
        }

        foreach (var npcId in _eligibleNpcIds)
        {
            if (!world.Entities.Npcs.TryGetValue(npcId, out var npc) ||
                !CanRequestDecision(world, npc) ||
                IsCoolingDown(world.Tick, npcId))
            {
                continue;
            }

            // Arm before calling provider code. A failed provider is still
            // throttled instead of being retried on every Medium pass.
            _nextDecisionTicks[npcId] = world.Tick + _decisionCooldownTicks;

            LlmDecision decision;
            try
            {
                decision = _provider.Decide(LlmDecisionContextBuilder.Build(world, npc));
            }
            catch (Exception exception)
            {
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "LlmControlProviderFailed",
                        $"Provider={_provider.GetType().Name} Error={exception.GetType().Name}: {exception.Message}");
                }

                continue;
            }

            if (!LlmCommandTranslator.TryTranslate(
                    decision, npc.Id, out var command, out var errorReason))
            {
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "LlmControlRejected",
                        $"Reason=InvalidDecision Detail={errorReason}");
                }

                continue;
            }

            if (command is null)
            {
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "LlmControlAccepted", "Command=None");
                }

                continue;
            }

            var eventWatermark = world.Events.HighestSeq;
            var primedManualMode = false;
            if (command is not SetManualControlCommand && !npc.Mind.ManualControl)
            {
                ManualCommandExecutor.Apply(
                    world, new SetManualControlCommand(npc.Id, enabled: true));
                primedManualMode = true;

                // Fail closed if the ordinary command path declined ownership.
                if (!npc.Mind.ManualControl)
                {
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "LlmControlRejected",
                            "Reason=ManualModeNotEnabled");
                    }

                    continue;
                }
            }

            ManualCommandExecutor.Apply(world, command);

            if (WasManualCommandRejected(world, npc.Id, eventWatermark))
            {
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "LlmControlRejected",
                        $"Reason=ManualCommandRejected Command={decision.CommandKind}");
                }

                continue;
            }

            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "LlmControlAccepted",
                    $"Command={decision.CommandKind} ManualPrimed={(primedManualMode ? 1 : 0)}");
            }
        }
    }

    private bool IsCoolingDown(int tick, EntityId npcId) =>
        _nextDecisionTicks.TryGetValue(npcId, out var nextTick) && tick < nextTick;

    private static bool CanRequestDecision(WorldState world, NPCState npc) =>
        npc.Health > 0f &&
        npc.Faction == Faction.Colony &&
        !npc.IsUnconscious(world.Tick) &&
        !npc.IsFighting &&
        !npc.IsCarryingPerson &&
        !npc.IsBeingCarried &&
        npc.Mind.CurrentGoal == GoalType.None &&
        npc.Mind.ManualAttackNpcId is null &&
        npc.Mind.ManualAttackMobId is null &&
        npc.Plan.Status != PlanStatus.Active &&
        npc.Execution.Status != ExecutionStatus.Starting &&
        npc.Execution.Status != ExecutionStatus.InProgress &&
        npc.Execution.Status != ExecutionStatus.Waiting &&
        !npc.Movement.IsMoving;

    private static bool WasManualCommandRejected(
        WorldState world, EntityId npcId, long eventWatermark)
    {
        foreach (var simulationEvent in world.Events.Items)
        {
            if (simulationEvent.Seq > eventWatermark &&
                simulationEvent.Type == "ManualOrderRejected" &&
                simulationEvent.EntityId == npcId.Value)
            {
                return true;
            }
        }

        return false;
    }
}

}
