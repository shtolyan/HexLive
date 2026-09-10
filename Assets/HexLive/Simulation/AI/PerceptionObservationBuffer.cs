using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.AI
{

/// <summary>§160.6a: attachment-local, bounded sightings between model turns.
/// Only copies the existing sensor; never retains a body, object or world.</summary>
public sealed class PerceptionObservationBuffer : IDisposable
{
    public const int ObjectCapacity = 128;
    public const int NpcCapacity = 48;
    public const int MobCapacity = 16;
    public const int SignificantNpcCapacity = 16;
    private readonly object _gate = new();
    private Bucket _objects = new(ObjectCapacity);
    private Bucket _npcs = new(NpcCapacity);
    private Bucket _mobs = new(MobCapacity);
    private Bucket _significantNpcs = new(SignificantNpcCapacity, protectUrgent: true);
    private long _sequence;
    private long _lostThrough;
    private int _lastCaptureTick = int.MinValue;
    public string Epoch { get; private set; } = Guid.NewGuid().ToString("N");

    public bool Capture(WorldState world, NPCState observer)
    {
        lock (_gate)
        {
            if (_objects == null) return false;
            var tick = observer.Perception.LastUpdatedTick;
            if (tick == _lastCaptureTick) return true;
            // A rollback cannot mix observations from two timelines.
            if (tick < _lastCaptureTick) Clear();
            _lastCaptureTick = tick;
            foreach (var seen in observer.Perception.Objects)
            {
                if (seen.FromMemory) continue;
                _objects.Put(new PerceptionObservation
                {
                    Kind = "object", Id = seen.Id.Value, DefinitionId = seen.DefinitionId,
                    TileQ = seen.Tile.Q, TileR = seen.Tile.R, Reachable = seen.IsReachable,
                    Occupied = seen.IsOccupied,
                }, observer.Tile, tick, ref _sequence, ref _lostThrough);
            }
            CapturePeople(world, observer, observer.Perception.Agents, false, tick);
            CapturePeople(world, observer, observer.Perception.Hostiles, true, tick);
            foreach (var seen in observer.Perception.Mobs)
                _mobs.Put(new PerceptionObservation
                {
                    Kind = "mob", Id = seen.Id, DefinitionId = seen.MobId,
                    TileQ = seen.Tile.Q, TileR = seen.Tile.R, Health = seen.Health,
                    MobStatus = seen.Status, TargetsMe = seen.TargetsMe,
                }, observer.Tile, tick, ref _sequence, ref _lostThrough);
            return true;
        }
    }

    private void CapturePeople(WorldState world, NPCState observer,
        List<PerceivedAgent> people, bool hostile, int tick)
    {
        foreach (var seen in people)
        {
            if (!seen.CanSee || seen.Id == observer.Id ||
                !world.Entities.Npcs.TryGetValue(seen.Id, out var body)) continue;
            var observation = new PerceptionObservation
            {
                Kind = "npc", Id = seen.Id.Value, NameId = body.DisplayName,
                TileQ = seen.Tile.Q, TileR = seen.Tile.R, Reachable = seen.IsReachable,
                Hostile = hostile, Unconscious = seen.IsUnconscious, Dying = seen.IsDying,
                Suffering = seen.Suffering, AidKind = seen.AidKind,
            };
            // A transient injury may have healed before the next model turn.
            // Preserve that observation separately from the most recent state.
            if ((seen.IsDying || seen.IsUnconscious || seen.Suffering > 0f) &&
                (!_npcs.TryGet(seen.Id.Value, out var previous) ||
                 seen.IsDying && !previous.Dying || seen.IsUnconscious && !previous.Unconscious ||
                 seen.Suffering > previous.Suffering || seen.AidKind != previous.AidKind))
            {
                observation.Significant = true;
                _significantNpcs.Put(observation, observer.Tile, tick, ref _sequence, ref _lostThrough);
                observation.Significant = false;
            }
            _npcs.Put(observation, observer.Tile, tick, ref _sequence, ref _lostThrough);
        }
    }

    public PerceptionObservationBatch Read(string epoch, long sinceSequence, int currentTick)
    {
        lock (_gate)
        {
            var reset = epoch != Epoch || sinceSequence < 0 || sinceSequence > _sequence;
            if (reset) sinceSequence = 0;
            // An explicit cursor acknowledges the previous durable model turn.
            // Entries refreshed while that request was running have larger
            // sequences and survive. Never clear the buffer merely on reading.
            _objects?.Acknowledge(sinceSequence);
            _npcs?.Acknowledge(sinceSequence);
            _mobs?.Acknowledge(sinceSequence);
            _significantNpcs?.Acknowledge(sinceSequence);
            var rows = new List<PerceptionObservation>();
            _objects?.Append(rows, sinceSequence, currentTick, _lastCaptureTick);
            _npcs?.Append(rows, sinceSequence, currentTick, _lastCaptureTick);
            _mobs?.Append(rows, sinceSequence, currentTick, _lastCaptureTick);
            _significantNpcs?.Append(rows, sinceSequence, currentTick, _lastCaptureTick);
            rows.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
            return new PerceptionObservationBatch(Epoch, _sequence,
                reset, _lostThrough > sinceSequence, rows.ToArray());
        }
    }

    private void Clear()
    {
        _objects.Clear();
        _npcs.Clear();
        _mobs.Clear();
        _significantNpcs.Clear();
        Epoch = Guid.NewGuid().ToString("N");
        _sequence = 0;
        _lostThrough = 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            // Detach/TTL/death/world replacement release all retained strings
            // immediately, even while the world is paused and never ticks again.
            _objects = null;
            _npcs = null;
            _mobs = null;
            _significantNpcs = null;
        }
    }

    private sealed class Bucket
    {
        private readonly PerceptionObservation[] _entries;
        private readonly Dictionary<int, int> _index;
        private int _count;
        private readonly bool _protectUrgent;
        public Bucket(int capacity, bool protectUrgent = false)
        {
            _entries = new PerceptionObservation[capacity];
            _index = new Dictionary<int, int>(capacity);
            _protectUrgent = protectUrgent;
        }

        public bool TryGet(int id, out PerceptionObservation value)
        {
            if (_index.TryGetValue(id, out var index)) { value = _entries[index]; return true; }
            value = default;
            return false;
        }

        public void Put(PerceptionObservation value, TileCoord observerTile, int tick,
            ref long sequence, ref long lostThrough)
        {
            value.Sequence = ++sequence;
            value.LastSeenTick = tick;
            value.ObserverTileQ = observerTile.Q;
            value.ObserverTileR = observerTile.R;
            if (!_index.TryGetValue(value.Id, out var slot))
            {
                if (_count < _entries.Length) slot = _count++;
                else
                {
                    // Overflow is rare: bounded scan, protect injured people
                    // from displacement by a stream of ordinary passers-by.
                    slot = 0;
                    for (var i = 1; i < _count; i++)
                        if (Priority(_entries[i]) < Priority(_entries[slot]) ||
                            (Priority(_entries[i]) == Priority(_entries[slot]) &&
                             _entries[i].Sequence < _entries[slot].Sequence)) slot = i;
                    if (Priority(value) < Priority(_entries[slot]))
                    {
                        lostThrough = value.Sequence;
                        return;
                    }
                    lostThrough = Math.Max(lostThrough, _entries[slot].Sequence);
                    _index.Remove(_entries[slot].Id);
                }
                _index.Add(value.Id, slot);
                value.FirstSeenTick = tick;
            }
            else
            {
                // Keep the worst event in its own slot when a later, less
                // serious injury occurs before acknowledgment.
                if (_protectUrgent && (Priority(value) < Priority(_entries[slot]) ||
                    Priority(value) == Priority(_entries[slot]) && value.Suffering < _entries[slot].Suffering))
                {
                    // A read of the older event may still be in flight. Keep
                    // its frozen facts but prevent that read's acknowledgment
                    // from consuming the newly coalesced observation.
                    _entries[slot].Sequence = value.Sequence;
                    return;
                }
                value.FirstSeenTick = _entries[slot].FirstSeenTick;
            }
            _entries[slot] = value;
        }

        private int Priority(PerceptionObservation value) => !_protectUrgent ? 1 :
            value.Dying || value.Unconscious || value.TargetsMe ? 3 : value.Suffering > 0f ? 2 : 1;

        public void Acknowledge(long since)
        {
            for (var i = _count - 1; i >= 0; i--)
            {
                if (_entries[i].Sequence > since) continue;
                _index.Remove(_entries[i].Id);
                _count--;
                if (i != _count)
                {
                    _entries[i] = _entries[_count];
                    _index[_entries[i].Id] = i;
                }
                _entries[_count] = default;
            }
        }

        public void Append(List<PerceptionObservation> rows, long since, int tick, int captureTick)
        {
            for (var i = 0; i < _count; i++)
            {
                var value = _entries[i];
                if (value.Sequence <= since) continue;
                value.AgeTicks = Math.Max(0, tick - value.LastSeenTick);
                value.InLatestPerception = value.LastSeenTick == captureTick;
                rows.Add(value);
            }
        }

        public void Clear()
        {
            Array.Clear(_entries, 0, _count);
            _index.Clear();
            _count = 0;
        }
    }
}

public struct PerceptionObservation
{
    public string Kind { get; set; }
    public int Id { get; set; }
    public string DefinitionId { get; set; }
    public string NameId { get; set; }
    public int TileQ { get; set; }
    public int TileR { get; set; }
    public int ObserverTileQ { get; set; }
    public int ObserverTileR { get; set; }
    public int FirstSeenTick { get; set; }
    public int LastSeenTick { get; set; }
    public int AgeTicks { get; set; }
    public long Sequence { get; set; }
    public bool InLatestPerception { get; set; }
    public bool Significant { get; set; }
    public bool Reachable { get; set; }
    public bool Occupied { get; set; }
    public bool Hostile { get; set; }
    public bool Unconscious { get; set; }
    public bool Dying { get; set; }
    public float Suffering { get; set; }
    public AidKind AidKind { get; set; }
    public float Health { get; set; }
    public Wildlife.MobStatus MobStatus { get; set; }
    public bool TargetsMe { get; set; }
}

public sealed class PerceptionObservationBatch
{
    public PerceptionObservationBatch(string epoch, long watermark, bool reset, bool gap,
        PerceptionObservation[] observations)
    { Epoch = epoch; Watermark = watermark; Reset = reset; Gap = gap; Observations = observations; }
    public string Epoch { get; }
    public long Watermark { get; }
    public bool Reset { get; }
    public bool Gap { get; }
    public PerceptionObservation[] Observations { get; }
}

}
