using System.Collections.Generic;

namespace HexLive.Simulation.Runtime
{

public sealed class SimulationEvent
{
    /// <summary>
    /// Monotonic id, assigned by <see cref="SimulationEventBuffer.Add"/>. Consumers
    /// track a watermark and take everything above it.
    /// <para>
    /// Identity ("is this the same object I saw last time?") used to be the way to
    /// find new events, and it fails twice: once when the ring trims the anchor away
    /// — the scan then finds nothing and re-processes all 2048 entries — and once
    /// more the moment an event crosses a serialization boundary, where identity
    /// does not survive at all. A number survives both.
    /// </para>
    /// </summary>
    public long Seq { get; set; }

    public int Tick { get; set; }

    public string Type { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public int? EntityId { get; set; }
}

public sealed class SimulationEventBuffer
{
    private readonly List<SimulationEvent> _events = new();

    private long _nextSeq = 1;

    public int Capacity { get; set; } = 2048;

    public IReadOnlyList<SimulationEvent> Items => _events;

    /// <summary>Seq of the newest event ever added; 0 before the first one.</summary>
    public long HighestSeq { get; private set; }

    /// <summary>
    /// Seq of the oldest event still in the ring. A consumer whose watermark is
    /// below this has missed events to trimming and must SKIP the gap rather than
    /// replay what is left — replaying writes duplicates downstream.
    /// </summary>
    public long LowestSeq => _events.Count > 0 ? _events[0].Seq : _nextSeq;

    public void Add(SimulationEvent simulationEvent)
    {
        simulationEvent.Seq = _nextSeq++;
        HighestSeq = simulationEvent.Seq;

        _events.Add(simulationEvent);
        var overflow = _events.Count - Capacity;
        if (overflow <= 0)
        {
            return;
        }

        _events.RemoveRange(0, overflow);
    }

    // Deliberately does NOT reset the counter: a save restore clears the ring
    // mid-session, and seq must keep climbing so watermarks stay valid. Only a
    // brand-new WorldState (fresh process, or a server restart) starts back at 1 —
    // consumers treat an incoming seq below their watermark as a session reset.
    public void Clear() => _events.Clear();
}

}
