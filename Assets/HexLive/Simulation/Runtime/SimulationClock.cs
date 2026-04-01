namespace HexLive.Simulation.Runtime
{
public sealed class SimulationClock
{
    public bool IsPaused { get; private set; } = true;

    public float SpeedMultiplier { get; private set; } = 1f;

    public void Pause() => IsPaused = true;

    public void Resume() => IsPaused = false;

    public void SetSpeed(float speedMultiplier) => SpeedMultiplier = speedMultiplier <= 0f ? 1f : speedMultiplier;
}

}
