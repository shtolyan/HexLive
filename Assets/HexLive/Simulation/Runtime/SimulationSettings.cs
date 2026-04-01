namespace HexLive.Simulation.Runtime
{
public sealed class SimulationSettings
{
    public float TickDeltaTime { get; set; } = 0.25f;

    public int FastInterval { get; set; } = 1;

    public int MediumInterval { get; set; } = 4;

    public int SlowInterval { get; set; } = 16;

    public int MaxTicksPerFrame { get; set; } = 1;
}

}
