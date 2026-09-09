using System;
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
    private List<SimulationEvent> _deferredEvents;

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
        if (_deferredEvents != null)
        {
            _deferredEvents.Add(simulationEvent);
            return;
        }

        Publish(simulationEvent);
    }

    /// <summary>
    /// Defers publication for one synchronous, atomic world mutation. The
    /// ordinary Add path stays allocation-free; only the active bulk command
    /// owns a temporary list. Nested scopes are forbidden because their commit
    /// order would make a rolled-back outer mutation observable.
    /// </summary>
    internal DeferredPublicationScope DeferPublication()
    {
        if (_deferredEvents != null)
            throw new InvalidOperationException("Simulation event publication is already deferred.");

        var pending = new List<SimulationEvent>();
        _deferredEvents = pending;
        return new DeferredPublicationScope(this, pending);
    }

    private void Publish(SimulationEvent simulationEvent)
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

    private void CommitDeferred(List<SimulationEvent> pending)
    {
        RequireActiveScope(pending);
        _deferredEvents = null;
        foreach (var simulationEvent in pending)
            Publish(simulationEvent);
    }

    private void DiscardDeferred(List<SimulationEvent> pending)
    {
        RequireActiveScope(pending);
        _deferredEvents = null;
    }

    private void RequireActiveScope(List<SimulationEvent> pending)
    {
        if (!ReferenceEquals(_deferredEvents, pending))
            throw new InvalidOperationException("The simulation event scope is no longer active.");
    }

    internal sealed class DeferredPublicationScope : IDisposable
    {
        private readonly SimulationEventBuffer _owner;
        private readonly List<SimulationEvent> _pending;
        private bool _active = true;

        internal DeferredPublicationScope(
            SimulationEventBuffer owner, List<SimulationEvent> pending)
        {
            _owner = owner;
            _pending = pending;
        }

        internal void Commit()
        {
            if (!_active)
                throw new InvalidOperationException("The simulation event scope is no longer active.");
            try
            {
                _owner.CommitDeferred(_pending);
            }
            finally
            {
                _active = false;
            }
        }

        public void Dispose()
        {
            if (!_active) return;
            _owner.DiscardDeferred(_pending);
            _active = false;
        }
    }

    // Deliberately does NOT reset the counter: a save restore clears the ring
    // mid-session, and seq must keep climbing so watermarks stay valid. Only a
    // brand-new WorldState (fresh process, or a server restart) starts back at 1 —
    // consumers treat an incoming seq below their watermark as a session reset.
    public void Clear() => _events.Clear();
}

}
