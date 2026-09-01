using System;
using System.Collections.Generic;
using System.Threading;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §32.15: opt-in, two-phase adapter between a non-blocking
/// <see cref="ILlmControlProvider"/> and the existing §121 manual-command path.
/// A pump first drains completed work, then issues bounded new work; provider
/// latency never runs on the simulation thread.
/// </summary>
public sealed class LlmControlSystem : ISimulationSystem, IDisposable
{
    private sealed class InFlightRequest
    {
        public InFlightRequest(
            LlmControlRequest request, CancellationTokenSource cancellation)
        {
            Request = request;
            Cancellation = cancellation;
        }

        public LlmControlRequest Request { get; }
        public CancellationTokenSource Cancellation { get; }
    }

    private readonly bool _enabled;
    private readonly List<EntityId> _eligibleNpcIds;
    private readonly ILlmControlProvider _provider;
    private readonly int _decisionCooldownTicks;
    private readonly int _requestTimeoutTicks;
    private readonly int _maxInFlightRequests;
    private readonly Dictionary<EntityId, int> _nextDecisionTicks = new();
    private readonly Dictionary<EntityId, InFlightRequest> _inFlightByNpc = new();
    private long _nextRequestId = 1;
    private int _roundRobinCursor;
    private bool _disposed;

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
            decisionCooldownTicks: SpecLlmControl.DecisionCooldownTicks,
            requestTimeoutTicks: SpecLlmControl.RequestTimeoutTicks,
            maxInFlightRequests: SpecLlmControl.MaxInFlightRequests)
    {
    }

    /// <summary>
    /// Explicit host/test path. The system owns and disposes the provider. When
    /// enabled without one it uses the deterministic offline mock; the simulation
    /// assembly contains no network-backed implementation.
    /// </summary>
    public LlmControlSystem(
        bool enabled,
        IEnumerable<EntityId> eligibleNpcIds,
        ILlmControlProvider provider = null,
        int decisionCooldownTicks = SpecLlmControl.DecisionCooldownTicks,
        int requestTimeoutTicks = SpecLlmControl.RequestTimeoutTicks,
        int maxInFlightRequests = SpecLlmControl.MaxInFlightRequests)
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

        if (requestTimeoutTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeoutTicks), "Request timeout must be positive.");
        }

        if (maxInFlightRequests <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxInFlightRequests), "The in-flight cap must be positive.");
        }

        _enabled = enabled;
        _decisionCooldownTicks = decisionCooldownTicks;
        _requestTimeoutTicks = requestTimeoutTicks;
        _maxInFlightRequests = maxInFlightRequests;
        _provider = enabled ? provider ?? new MockLlmControlProvider() : provider;

        var uniqueIds = new HashSet<EntityId>(eligibleNpcIds);
        _eligibleNpcIds = new List<EntityId>(uniqueIds);
        _eligibleNpcIds.Sort((left, right) => left.Value.CompareTo(right.Value));
    }

    public string Name => nameof(LlmControlSystem);

    public TickLayer Layer => TickLayer.Medium;

    public ChunkPolicy ChunkPolicy => ChunkPolicy.NpcDriven;

    public void Run(WorldState world)
    {
        if (_disposed || !_enabled || _provider is null)
        {
            return;
        }

        // Phase 1 only consumes work issued by an earlier pump. Even a provider
        // that completes immediately cannot issue and apply in the same Run.
        DrainProviderResults(world);
        ExpireTimedOutRequests(world);

        if (_eligibleNpcIds.Count == 0 || !Spec121.ManualControlEnabled)
        {
            if (!Spec121.ManualControlEnabled)
            {
                CancelAllInFlight();
            }

            return;
        }

        // Phase 2 walks the stable sorted roster from a rotating cursor and only
        // enqueues enough requests to fill the global cap.
        RequestEligibleDecisions(world);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelAllInFlight();
        _provider?.Dispose();
    }

    private void DrainProviderResults(WorldState world)
    {
        // A legitimate provider can produce at most one result per accepted
        // request. A fixed budget also protects a tick from a faulty provider
        // that floods its result queue.
        var drainBudget = Math.Max(4, _maxInFlightRequests * 4);
        for (var i = 0; i < drainBudget; i++)
        {
            LlmControlResult result;
            try
            {
                if (!_provider.TryDequeueResult(out result))
                {
                    return;
                }
            }
            catch (Exception exception)
            {
                TraceProviderFailure(world, _eligibleNpcIds.Count > 0
                    ? _eligibleNpcIds[_roundRobinCursor % _eligibleNpcIds.Count]
                    : new EntityId(0), exception.GetType().Name, exception.Message);
                return;
            }

            if (result is not null)
            {
                ProcessResult(world, result);
            }
        }
    }

    private void ProcessResult(WorldState world, LlmControlResult result)
    {
        if (!_inFlightByNpc.TryGetValue(result.NpcId, out var inFlight) ||
            inFlight.Request.RequestId != result.RequestId ||
            inFlight.Request.IssuedTick != result.IssuedTick)
        {
            TraceDropped(world, result.NpcId, "NoMatchingRequest", result.IssuedTick);
            return;
        }

        CompleteInFlight(result.NpcId, inFlight, cancel: false);

        if (IsExpired(world.Tick, result.IssuedTick))
        {
            TraceDropped(world, result.NpcId, "StaleDecision", result.IssuedTick);
            return;
        }

        switch (result.Status)
        {
            case LlmControlResultStatus.Canceled:
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, result.NpcId, "LlmControlCanceled",
                        $"IssuedTick={result.IssuedTick}");
                }

                return;

            case LlmControlResultStatus.Failed:
                TraceProviderFailure(
                    world, result.NpcId, result.ErrorType, result.ErrorMessage);
                return;

            case LlmControlResultStatus.Completed:
                break;

            default:
                TraceDropped(world, result.NpcId, "UnknownResultStatus", result.IssuedTick);
                return;
        }

        // The world may have advanced arbitrarily while the provider worked.
        // Re-run every state gate from request admission before translation or
        // manual-mode acquisition; the ordinary executor then re-validates the
        // command's world targets and semantics.
        if (!Spec121.ManualControlEnabled ||
            !world.Entities.Npcs.TryGetValue(result.NpcId, out var npc) ||
            !CanRequestDecision(world, npc))
        {
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, result.NpcId, "LlmControlRejected",
                    $"Reason=GateChanged IssuedTick={result.IssuedTick}");
            }

            return;
        }

        ApplyDecision(world, npc, result.Decision, result.IssuedTick);
    }

    private void ApplyDecision(
        WorldState world, NPCState npc, LlmDecision decision, int issuedTick)
    {
        if (!LlmCommandTranslator.TryTranslate(
                decision, npc.Id, out var command, out var errorReason))
        {
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "LlmControlRejected",
                    $"Reason=InvalidDecision IssuedTick={issuedTick} Detail={errorReason}");
            }

            return;
        }

        if (command is null)
        {
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "LlmControlAccepted",
                    $"Command=None IssuedTick={issuedTick}");
            }

            return;
        }

        var primedManualMode = false;
        if (command is not SetManualControlCommand && !npc.Mind.ManualControl)
        {
            var manualModeAdmission = ManualCommandExecutor.Apply(
                world, new SetManualControlCommand(npc.Id, enabled: true));
            primedManualMode = true;

            if (!manualModeAdmission.Accepted || !npc.Mind.ManualControl)
            {
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "LlmControlRejected",
                        $"Reason=ManualModeNotEnabled " +
                        $"AdmissionReason={manualModeAdmission.Reason} " +
                        $"IssuedTick={issuedTick}");
                }

                return;
            }
        }

        var admission = ManualCommandExecutor.Apply(world, command);

        if (!admission.Accepted)
        {
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "LlmControlRejected",
                    $"Reason=ManualCommandRejected Command={decision.CommandKind} " +
                    $"AdmissionReason={admission.Reason} IssuedTick={issuedTick}");
            }

            return;
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "LlmControlAccepted",
                $"Command={decision.CommandKind} ManualPrimed={(primedManualMode ? 1 : 0)} " +
                $"IssuedTick={issuedTick}");
        }
    }

    private void ExpireTimedOutRequests(WorldState world)
    {
        for (var i = 0; i < _eligibleNpcIds.Count; i++)
        {
            var npcId = _eligibleNpcIds[i];
            if (!_inFlightByNpc.TryGetValue(npcId, out var inFlight) ||
                !IsExpired(world.Tick, inFlight.Request.IssuedTick))
            {
                continue;
            }

            CompleteInFlight(npcId, inFlight, cancel: true);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npcId, "LlmControlTimedOut",
                    $"Request={inFlight.Request.RequestId} " +
                    $"IssuedTick={inFlight.Request.IssuedTick} CurrentTick={world.Tick}");
            }
        }
    }

    private void RequestEligibleDecisions(WorldState world)
    {
        var examined = 0;
        while (_inFlightByNpc.Count < _maxInFlightRequests &&
               examined < _eligibleNpcIds.Count)
        {
            var npcId = _eligibleNpcIds[_roundRobinCursor];
            _roundRobinCursor = (_roundRobinCursor + 1) % _eligibleNpcIds.Count;
            examined += 1;

            if (_inFlightByNpc.ContainsKey(npcId) ||
                IsCoolingDown(world.Tick, npcId) ||
                !world.Entities.Npcs.TryGetValue(npcId, out var npc) ||
                !CanRequestDecision(world, npc))
            {
                continue;
            }

            StartRequest(world, npc);
        }
    }

    private void StartRequest(WorldState world, NPCState npc)
    {
        var cancellation = new CancellationTokenSource();
        var request = new LlmControlRequest(
            _nextRequestId++,
            LlmDecisionContextBuilder.Build(world, npc),
            cancellation.Token);
        var inFlight = new InFlightRequest(request, cancellation);

        // Arm both guards before calling provider code. Rejection or immediate
        // provider failure is still throttled instead of retried every pass.
        _inFlightByNpc.Add(npc.Id, inFlight);
        _nextDecisionTicks[npc.Id] = world.Tick + _decisionCooldownTicks;

        var accepted = false;
        var failureReported = false;
        try
        {
            accepted = _provider.TryRequest(request);
        }
        catch (Exception exception)
        {
            TraceProviderFailure(world, npc.Id,
                exception.GetType().Name, exception.Message);
            failureReported = true;
        }

        if (accepted)
        {
            return;
        }

        CompleteInFlight(npc.Id, inFlight, cancel: true);
        if (!failureReported && SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "LlmControlProviderFailed",
                $"Provider={_provider.GetType().Name} Error=RequestRejected");
        }
    }

    private bool IsCoolingDown(int tick, EntityId npcId) =>
        _nextDecisionTicks.TryGetValue(npcId, out var nextTick) && tick < nextTick;

    private bool IsExpired(int currentTick, int issuedTick) =>
        currentTick < issuedTick || currentTick - issuedTick >= _requestTimeoutTicks;

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

    private void CompleteInFlight(
        EntityId npcId, InFlightRequest inFlight, bool cancel)
    {
        if (_inFlightByNpc.TryGetValue(npcId, out var current) &&
            ReferenceEquals(current, inFlight))
        {
            _inFlightByNpc.Remove(npcId);
        }

        if (cancel)
        {
            CancelWithoutThrowing(inFlight.Cancellation);
        }

        inFlight.Cancellation.Dispose();
    }

    private void CancelAllInFlight()
    {
        for (var i = 0; i < _eligibleNpcIds.Count; i++)
        {
            var npcId = _eligibleNpcIds[i];
            if (_inFlightByNpc.TryGetValue(npcId, out var inFlight))
            {
                CompleteInFlight(npcId, inFlight, cancel: true);
            }
        }
    }

    private static void CancelWithoutThrowing(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (AggregateException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void TraceProviderFailure(
        WorldState world, EntityId npcId, string errorType, string errorMessage)
    {
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npcId, "LlmControlProviderFailed",
                $"Provider={_provider.GetType().Name} Error={errorType}: {errorMessage}");
        }
    }

    private static void TraceDropped(
        WorldState world, EntityId npcId, string reason, int issuedTick)
    {
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npcId, "LlmControlDropped",
                $"Reason={reason} IssuedTick={issuedTick} CurrentTick={world.Tick}");
        }
    }

}

}
