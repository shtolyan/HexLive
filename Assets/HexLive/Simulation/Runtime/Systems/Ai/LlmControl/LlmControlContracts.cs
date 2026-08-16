using System;
using System.Threading;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Runtime
{

/// <summary>Commands an LLM pilot may request. Applying them is outside this contract.</summary>
public enum LlmCommandKind
{
    None,
    Stop,
    MoveTo,
    Interact,
    AttackNpc,
    AttackMob,
    SetManualControl
}

/// <summary>
/// Non-blocking provider boundary. <see cref="TryRequest"/> may only enqueue
/// work; provider latency happens off the simulation thread. Completed work is
/// published through a provider-owned, thread-safe queue drained by
/// <see cref="TryDequeueResult"/>.
/// </summary>
public interface ILlmControlProvider : IDisposable
{
    bool TryRequest(LlmControlRequest request);

    bool TryDequeueResult(out LlmControlResult result);
}

/// <summary>Immutable request envelope used to correlate asynchronous results.</summary>
public sealed class LlmControlRequest
{
    public LlmControlRequest(
        long requestId,
        LlmDecisionContext context,
        CancellationToken cancellationToken)
    {
        if (requestId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestId));
        }

        RequestId = requestId;
        Context = context ?? throw new ArgumentNullException(nameof(context));
        CancellationToken = cancellationToken;
    }

    public long RequestId { get; }
    public LlmDecisionContext Context { get; }
    public CancellationToken CancellationToken { get; }
    public EntityId NpcId => Context.NpcId;
    public int IssuedTick => Context.Tick;
}

public enum LlmControlResultStatus
{
    Completed,
    Failed,
    Canceled
}

/// <summary>
/// Immutable result envelope. Request identity and issued tick are repeated so
/// the simulation can reject late, duplicated or otherwise stale provider work
/// without trusting provider completion order.
/// </summary>
public sealed class LlmControlResult
{
    public LlmControlResult(
        long requestId,
        EntityId npcId,
        int issuedTick,
        LlmControlResultStatus status,
        LlmDecision decision = null,
        string errorType = "",
        string errorMessage = "")
    {
        RequestId = requestId;
        NpcId = npcId;
        IssuedTick = issuedTick;
        Status = status;
        Decision = decision;
        ErrorType = errorType ?? string.Empty;
        ErrorMessage = errorMessage ?? string.Empty;
    }

    public long RequestId { get; }
    public EntityId NpcId { get; }
    public int IssuedTick { get; }
    public LlmControlResultStatus Status { get; }
    public LlmDecision Decision { get; }
    public string ErrorType { get; }
    public string ErrorMessage { get; }

    public static LlmControlResult Completed(
        LlmControlRequest request, LlmDecision decision) =>
        new(
            request.RequestId,
            request.NpcId,
            request.IssuedTick,
            LlmControlResultStatus.Completed,
            decision);

    public static LlmControlResult Failed(
        LlmControlRequest request, Exception exception) =>
        new(
            request.RequestId,
            request.NpcId,
            request.IssuedTick,
            LlmControlResultStatus.Failed,
            errorType: exception?.GetType().Name ?? "UnknownError",
            errorMessage: exception?.Message ?? string.Empty);

    public static LlmControlResult Canceled(LlmControlRequest request) =>
        new(
            request.RequestId,
            request.NpcId,
            request.IssuedTick,
            LlmControlResultStatus.Canceled);
}

/// <summary>
/// Compact immutable input for an LLM pilot. Summaries are prepared by
/// <see cref="LlmDecisionContextBuilder"/> so providers never receive or mutate
/// <c>WorldState</c> directly.
/// </summary>
public sealed class LlmDecisionContext
{
    public LlmDecisionContext(
        EntityId npcId,
        int tick,
        Float2 position,
        string stateSummary = "",
        string perceptionSummary = "",
        string memorySummary = "")
    {
        NpcId = npcId;
        Tick = tick;
        Position = position;
        StateSummary = stateSummary ?? string.Empty;
        PerceptionSummary = perceptionSummary ?? string.Empty;
        MemorySummary = memorySummary ?? string.Empty;
    }

    public EntityId NpcId { get; }
    public int Tick { get; }
    public Float2 Position { get; }
    public string StateSummary { get; }
    public string PerceptionSummary { get; }
    public string MemorySummary { get; }
}

/// <summary>
/// Immutable provider result. Target fields are optional because each command
/// kind needs a different subset; <see cref="LlmCommandTranslator"/> validates
/// that subset without executing it.
/// </summary>
public sealed class LlmDecision
{
    public LlmDecision(
        LlmCommandKind commandKind,
        EntityId? targetNpcId = null,
        ObjectId? targetObjectId = null,
        int? targetMobId = null,
        Float2? targetPosition = null,
        InteractionType? interaction = null,
        bool? manualControlEnabled = null,
        string reason = "")
    {
        CommandKind = commandKind;
        TargetNpcId = targetNpcId;
        TargetObjectId = targetObjectId;
        TargetMobId = targetMobId;
        TargetPosition = targetPosition;
        Interaction = interaction;
        ManualControlEnabled = manualControlEnabled;
        Reason = reason ?? string.Empty;
    }

    public LlmCommandKind CommandKind { get; }
    public EntityId? TargetNpcId { get; }
    public ObjectId? TargetObjectId { get; }
    public int? TargetMobId { get; }
    public Float2? TargetPosition { get; }
    public InteractionType? Interaction { get; }
    public bool? ManualControlEnabled { get; }
    public string Reason { get; }
}

}
