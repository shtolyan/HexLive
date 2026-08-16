using System;
using System.Diagnostics;

namespace HexLive.Simulation.Runtime
{
public sealed class SimulationClock
{
    private readonly Func<double> _realtimeSeconds;

    public SimulationClock()
        : this(() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency)
    {
    }

    internal SimulationClock(Func<double> realtimeSeconds)
    {
        _realtimeSeconds = realtimeSeconds ?? throw new ArgumentNullException(nameof(realtimeSeconds));
    }

    public bool IsPaused { get; private set; } = true;

    public float SpeedMultiplier { get; private set; } = 1f;

    /// <summary>Monotonic wall time, unaffected by pause or simulation speed.</summary>
    internal double RealtimeSeconds => _realtimeSeconds();

    public void Pause() => IsPaused = true;

    public void Resume() => IsPaused = false;

    public void SetSpeed(float speedMultiplier) => SpeedMultiplier = speedMultiplier <= 0f ? 1f : speedMultiplier;
}

}
