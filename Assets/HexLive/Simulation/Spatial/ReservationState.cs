using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Spatial
{

public sealed class ReservationState
{
    public Dictionary<JunctionId, ReservationRecord> Junctions { get; } = new();
}

public sealed class ReservationRecord
{
    public EntityId Owner { get; set; }

    public int StartTick { get; set; }

    public int EndTick { get; set; }
}

public sealed class OccupancyState
{
    public Dictionary<JunctionId, EntityId?> JunctionOwner { get; } = new();

    public Dictionary<TileCoord, List<EntityId>> EntitiesInTile { get; } = new();
}

}
