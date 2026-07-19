using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Memory
{

// Spec 27.18A: v1 spatial object memory — what the NPC knows exists and
// where. Sightings upsert; negative evidence and TTL remove.
public sealed class MemoryState
{
    public Dictionary<ObjectId, ObjectMemory> KnownObjects { get; } = new();

    // Spec 29C.4A: places where this NPC was attacked. TTL 2400 ticks, cap 8.
    public List<DangerMemory> Dangers { get; } = new();

    // Behavior audit (Jul 2026): objects that turned out occupied on arrival.
    // A short personal "don't chase that one again" note so the planner picks
    // a DIFFERENT source next time instead of oscillating against the same
    // contested coconut for half a day (the thirst-death class of seed 12345).
    // Transient by design — not persisted; an empty table after load is fine.
    public Dictionary<ObjectId, int> ShunnedUntil { get; } = new();

    public void Shun(ObjectId id, int untilTick) => ShunnedUntil[id] = untilTick;

    public bool IsShunned(ObjectId id, int tick) =>
        ShunnedUntil.TryGetValue(id, out var until) && until > tick;
}

public sealed class DangerMemory
{
    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public int Tick { get; set; }
}

public sealed class ObjectMemory
{
    public ObjectId Id { get; set; }

    public string DefinitionId { get; set; } = string.Empty;

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public JunctionId? Junction { get; set; }

    // Seeded home knowledge (bootstrap objects) never expires.
    public bool IsPermanent { get; set; }

    public int LastSeenTick { get; set; }
}

}
