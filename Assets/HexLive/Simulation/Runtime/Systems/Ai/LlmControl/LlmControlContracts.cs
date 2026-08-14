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

public interface ILlmControlProvider
{
    LlmDecision Decide(LlmDecisionContext context);
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
