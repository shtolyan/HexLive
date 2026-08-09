using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Debug
{

/// <summary>
/// Reconstructs the functional limb value carried by a typed body snapshot.
/// This mirrors <see cref="BodyState.LimbFunction"/> without adding a derived
/// handedness field to the save or wire format.
/// </summary>
public static class BodyPartFunctionSnapshotMath
{
    public static float LimbFunction(
        IReadOnlyList<BodyPartConditionSnapshot> conditions, BodyPart part)
    {
        if (conditions == null)
        {
            return 1f; // legacy/dev callers with no typed conditions stay right-handed
        }

        for (var i = 0; i < conditions.Count; i++)
        {
            var condition = conditions[i];
            if (condition == null || condition.Part != part)
            {
                continue;
            }

            if (!condition.Severed)
            {
                return System.Math.Max(condition.Health, condition.SplintSupport);
            }

            var prosthetic = condition.Prosthetic;
            if (prosthetic == null || prosthetic.MaxCondition <= 0f)
            {
                return 0f;
            }

            var condition01 = System.Math.Max(0f,
                System.Math.Min(1f, prosthetic.Condition / prosthetic.MaxCondition));
            return prosthetic.Function * condition01;
        }

        // Typed snapshots normally contain all seven zones. Preserve the old
        // right-handed presentation for partial snapshots made by dev scenes.
        return 1f;
    }

    /// <summary>
    /// Right is the default acting hand. Left takes over only when right is
    /// unusable; false means neither arm reaches the functional threshold.
    /// </summary>
    public static bool TryGetActingHand(
        IReadOnlyList<BodyPartConditionSnapshot> conditions, out BodyPart hand)
    {
        if (LimbFunction(conditions, BodyPart.ArmR) >= BodyState.UsableHandFunctionThreshold)
        {
            hand = BodyPart.ArmR;
            return true;
        }

        if (LimbFunction(conditions, BodyPart.ArmL) >= BodyState.UsableHandFunctionThreshold)
        {
            hand = BodyPart.ArmL;
            return true;
        }

        hand = BodyPart.ArmR;
        return false;
    }
}

}
