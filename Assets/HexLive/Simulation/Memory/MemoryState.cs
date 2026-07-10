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
