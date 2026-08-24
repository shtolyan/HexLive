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

            // §121.9: паритет с приказами игрока — те же объекты команд.

            case LlmCommandKind.TalkTo:
                if (!RequirePeer(decision, npcId, "TalkTo", out var talkTarget, out errorReason))
                {
                    return false;
                }

                command = new TalkToCommand(npcId, talkTarget);
                return true;

            case LlmCommandKind.Aid:
                if (!RequirePeer(decision, npcId, "Aid", out var aidTarget, out errorReason))
                {
                    return false;
                }

                if (decision.AidKind is not { } aidKind)
                {
                    errorReason = "Aid requires AidKind.";
                    return false;
                }

                if (!Enum.IsDefined(typeof(AI.AidKind), aidKind))
                {
                    errorReason = $"Aid does not support AidKind: {aidKind}.";
                    return false;
                }

                command = new AidPersonCommand(npcId, aidTarget, aidKind);
                return true;

            case LlmCommandKind.TreatLimbs:
                if (!RequirePeer(decision, npcId, "TreatLimbs", out var patient, out errorReason))
                {
                    return false;
                }

                command = new TreatLimbsCommand(npcId, patient);
                return true;

            case LlmCommandKind.MedicalAid:
                if (!RequirePeer(decision, npcId, "MedicalAid", out var medicalPatient,
                        out errorReason))
                {
                    return false;
                }

                command = new MedicalAidCommand(npcId, medicalPatient);
                return true;

            case LlmCommandKind.SelfAction:
                if (decision.SelfAction is not { } selfAction)
                {
                    errorReason = "SelfAction requires SelfAction kind.";
                    return false;
                }

                if (!Enum.IsDefined(typeof(SelfActionKind), selfAction))
                {
                    errorReason = $"SelfAction does not support kind: {selfAction}.";
                    return false;
                }

                command = new SelfActionCommand(npcId, selfAction);
                return true;

            case LlmCommandKind.CarryPerson:
                if (!RequirePeer(decision, npcId, "CarryPerson", out var carried, out errorReason))
                {
                    return false;
                }

                command = new CarryPersonCommand(npcId, carried);
                return true;

            case LlmCommandKind.PutDownPerson:
                command = new PutDownPersonCommand(npcId);
                return true;

            case LlmCommandKind.PutPersonInBed:
                if (decision.TargetObjectId is not { } bedId)
                {
                    errorReason = "PutPersonInBed requires TargetObjectId (the bed).";
                    return false;
                }

                if (bedId.Value <= 0)
                {
                    errorReason = "PutPersonInBed requires a positive TargetObjectId.";
                    return false;
                }

                command = new PutPersonInBedCommand(npcId, bedId);
                return true;

            case LlmCommandKind.Craft:
                if (decision.RecipeGoal is not { } recipeGoal)
                {
                    errorReason = "Craft requires RecipeGoal.";
                    return false;
                }

                if (!Content.RecipeCatalog.ByGoal.ContainsKey(recipeGoal))
                {
                    errorReason = $"Craft does not know recipe goal: {recipeGoal}.";
                    return false;
                }

                command = new CraftItemCommand(npcId, recipeGoal);
                return true;

            default:
                errorReason = $"Unsupported LLM command kind: {decision.CommandKind}.";
                return false;
        }
    }

    private static bool IsFinite(float value) =>
        !float.IsNaN(value) && !float.IsInfinity(value);

    // §121.9: общая проверка цели-человека — есть, положительна, не сама.
    private static bool RequirePeer(
        LlmDecision decision, EntityId npcId, string verb,
        out EntityId target, out string errorReason)
    {
        target = default;
        errorReason = string.Empty;
        if (decision.TargetNpcId is not { } candidate)
        {
            errorReason = $"{verb} requires TargetNpcId.";
            return false;
        }

        if (candidate.Value <= 0)
        {
            errorReason = $"{verb} requires a positive TargetNpcId.";
            return false;
        }

        if (candidate == npcId)
        {
            errorReason = $"{verb} cannot target the acting NPC.";
            return false;
        }

        target = candidate;
        return true;
    }
}

}
