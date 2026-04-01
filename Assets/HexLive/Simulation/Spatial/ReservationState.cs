using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Spatial
{

public sealed class ReservationState
{
    public Dictionary<PointId, ReservationRecord> Points { get; } = new();
}

public sealed class ReservationRecord
{
    public EntityId Owner { get; set; }

    public int StartTick { get; set; }

    public int EndTick { get; set; }
}

public sealed class OccupancyState
{
    public Dictionary<PointId, EntityId?> PointOwner { get; } = new();

    public Dictionary<TileCoord, List<EntityId>> EntitiesInTile { get; } = new();
}

}
