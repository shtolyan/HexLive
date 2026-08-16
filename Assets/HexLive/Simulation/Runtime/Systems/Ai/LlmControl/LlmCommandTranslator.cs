using System;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// Converts an LLM decision into the same immutable command objects used by
/// player control. Translation only validates payload shape; it never applies
/// or enqueues the command.
/// </summary>
public static class LlmCommandTranslator
{
    /// <summary>
    /// Returns true when the decision is structurally valid. A valid
    /// <see cref="LlmCommandKind.None"/> decision produces a null command.
    /// </summary>
    public static bool TryTranslate(
        LlmDecision decision,
        EntityId npcId,
        out ISimulationCommand? command,
        out string errorReason)
    {
        command = null;
        errorReason = string.Empty;

        if (decision is null)
        {
            errorReason = "Decision is required.";
            return false;
        }

        switch (decision.CommandKind)
        {
            case LlmCommandKind.None:
                return true;

            case LlmCommandKind.Stop:
                command = new StopCommand(npcId);
                return true;

            case LlmCommandKind.MoveTo:
                if (decision.TargetPosition is not { } targetPosition)
                {
                    errorReason = "MoveTo requires TargetPosition.";
                    return false;
                }

                if (!IsFinite(targetPosition.X) || !IsFinite(targetPosition.Y))
                {
                    errorReason = "MoveTo requires a finite TargetPosition.";
                    return false;
                }

                command = new MoveToCommand(npcId, targetPosition);
                return true;

            case LlmCommandKind.Interact:
                if (decision.TargetObjectId is not { } targetObjectId)
                {
                    errorReason = "Interact requires TargetObjectId.";
                    return false;
                }

                if (targetObjectId.Value <= 0)
                {
                    errorReason = "Interact requires a positive TargetObjectId.";
                    return false;
                }

                if (decision.Interaction is not { } interaction)
                {
                    errorReason = "Interact requires Interaction.";
                    return false;
                }

                if (!Enum.IsDefined(typeof(Content.InteractionType), interaction))
                {
                    errorReason = $"Interact does not support Interaction: {interaction}.";
                    return false;
                }

                command = new InteractCommand(npcId, targetObjectId, interaction);
                return true;

            case LlmCommandKind.AttackNpc:
                if (decision.TargetNpcId is not { } targetNpcId)
                {
                    errorReason = "AttackNpc requires TargetNpcId.";
                    return false;
                }

                if (targetNpcId.Value <= 0)
                {
                    errorReason = "AttackNpc requires a positive TargetNpcId.";
                    return false;
                }

                if (targetNpcId == npcId)
                {
                    errorReason = "AttackNpc cannot target the acting NPC.";
                    return false;
                }

                command = new AttackNpcCommand(npcId, targetNpcId);
                return true;

            case LlmCommandKind.AttackMob:
                if (decision.TargetMobId is not { } targetMobId)
                {
                    errorReason = "AttackMob requires TargetMobId.";
                    return false;
                }

                if (targetMobId <= 0)
                {
                    errorReason = "AttackMob requires a positive TargetMobId.";
                    return false;
                }

                command = new AttackMobCommand(npcId, targetMobId);
                return true;

            case LlmCommandKind.SetManualControl:
                if (decision.ManualControlEnabled is not { } enabled)
                {
                    errorReason = "SetManualControl requires ManualControlEnabled.";
                    return false;
                }

                command = new SetManualControlCommand(npcId, enabled);
                return true;

            default:
                errorReason = $"Unsupported LLM command kind: {decision.CommandKind}.";
                return false;
        }
    }

    private static bool IsFinite(float value) =>
        !float.IsNaN(value) && !float.IsInfinity(value);
}

}
