namespace HexLive.UnityPresentation.Wearing
{

/// <summary>
/// Converts the simulation clock into a finite presentation clock.
/// The simulation deliberately uses +Infinity to mean "run as many ticks as
/// the CPU allows". Unity Animator and pose phase accumulators require finite
/// values: feeding that sentinel into them permanently poisons the pose with
/// Infinity/NaN even after the player returns to 1x.
/// </summary>
public static class PresentationSpeed
{
    // The fastest already-supported finite preset. At MAX the renderer only
    // shows the newest snapshot, so trying to mirror uncapped tick throughput
    // has no meaningful visual interpretation; 50x is finite and established.
    public const float MaxVisualMultiplier = 50f;
    public const float MinVisualMultiplier = 0.01f;

    public static float Normalize(float simulationMultiplier)
    {
        if (float.IsPositiveInfinity(simulationMultiplier))
        {
            return MaxVisualMultiplier;
        }

        if (float.IsNaN(simulationMultiplier) ||
            float.IsNegativeInfinity(simulationMultiplier))
        {
            return 1f;
        }

        if (simulationMultiplier < MinVisualMultiplier)
        {
            return MinVisualMultiplier;
        }

        return simulationMultiplier > MaxVisualMultiplier
            ? MaxVisualMultiplier
            : simulationMultiplier;
    }
}

}
