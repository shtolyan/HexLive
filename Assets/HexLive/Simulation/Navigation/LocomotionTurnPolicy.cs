using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;

namespace HexLive.Simulation.Navigation
{

/// <summary>
/// One policy shared by the simulation and the locomotion laboratory. The
/// decision uses the angle at the START of the tick: checking only the angle
/// left after a 54-degree turn made a requested 120-degree pivot look like a
/// harmless 66-degree bend and allowed translation while the body was sideways.
/// </summary>
public static class LocomotionTurnPolicy
{
    public static bool RequiresPlantedPivot(float initialFacingError) =>
        MathUtil.Abs(initialFacingError) > SimBalance.TurnFreezeAngle;

    /// <summary>
    /// Small hex bends remain free. Mid-sized turns use the mean angle during
    /// this tick, so a 90-degree command slows before the body has finished
    /// rotating instead of evaluating only its already-small residual angle.
    /// Planted pivots never call this method because they do not translate.
    /// </summary>
    public static float AlignmentFactor(float initialFacingError, float residualFacingError)
    {
        var representativeError =
            (MathUtil.Abs(initialFacingError) + MathUtil.Abs(residualFacingError)) * 0.5f;
        if (representativeError <= SimBalance.TurnFreeAngle)
        {
            return 1f;
        }

        var over = (representativeError - SimBalance.TurnFreeAngle) /
            System.MathF.Max(1f, SimBalance.TurnFreezeAngle - SimBalance.TurnFreeAngle);
        return 1f - MathUtil.Clamp01(over) * (1f - SimBalance.TurnMinSpeedFactor);
    }
}

}
