using System.Collections.Generic;

namespace HexLive.Simulation.Runtime
{

public sealed class SimulationEvent
{
    public int Tick { get; set; }

    public string Type { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public int? EntityId { get; set; }
}

public sealed class SimulationEventBuffer
{
    private readonly List<SimulationEvent> _events = new();

    public int Capacity { get; set; } = 2048;

    public IReadOnlyList<SimulationEvent> Items => _events;

    public void Add(SimulationEvent simulationEvent)
    {
        _events.Add(simulationEvent);
        var overflow = _events.Count - Capacity;
        if (overflow <= 0)
        {
            return;
        }

        _events.RemoveRange(0, overflow);
    }

    public void Clear() => _events.Clear();
}

}
