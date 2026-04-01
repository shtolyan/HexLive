using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Spatial
{

public static class SpatialMutations
{
    public static void MoveEntityToTile(WorldState world, EntityId entityId, TileCoord fromTile, TileCoord toTile)
    {
        if (world.Occupancy.EntitiesInTile.TryGetValue(fromTile, out var fromEntities))
        {
            fromEntities.Remove(entityId);
        }

        if (!world.Occupancy.EntitiesInTile.TryGetValue(toTile, out var toEntities))
        {
            toEntities = new List<EntityId>();
            world.Occupancy.EntitiesInTile[toTile] = toEntities;
        }

        if (!toEntities.Contains(entityId))
        {
            toEntities.Add(entityId);
        }

        if (world.Caches.EntitiesByTile.TryGetValue(fromTile, out var fromCached))
        {
            fromCached.Remove(entityId);
        }

        if (!world.Caches.EntitiesByTile.TryGetValue(toTile, out var toCached))
        {
            toCached = new List<EntityId>();
            world.Caches.EntitiesByTile[toTile] = toCached;
        }

        if (!toCached.Contains(entityId))
        {
            toCached.Add(entityId);
        }
    }

    public static bool TryReservePoint(WorldState world, PointId pointId, EntityId owner, int currentTick, int durationTicks)
    {
        foreach (var linkedPointId in SpatialQueries.GetLinkedPointIds(world, pointId))
        {
            if (world.Reservations.Points.TryGetValue(linkedPointId, out var existing))
            {
                if (existing.Owner != owner && existing.EndTick >= currentTick)
                {
                    return false;
                }
            }
        }

        foreach (var linkedPointId in SpatialQueries.GetLinkedPointIds(world, pointId))
        {
            world.Reservations.Points[linkedPointId] = new ReservationRecord
            {
                Owner = owner,
                StartTick = currentTick,
                EndTick = currentTick + durationTicks
            };
        }

        return true;
    }

    public static void ReleasePointReservation(WorldState world, PointId pointId, EntityId owner)
    {
        foreach (var linkedPointId in SpatialQueries.GetLinkedPointIds(world, pointId))
        {
            if (world.Reservations.Points.TryGetValue(linkedPointId, out var existing) && existing.Owner == owner)
            {
                world.Reservations.Points.Remove(linkedPointId);
            }
        }
    }

    public static void OccupyPoint(WorldState world, PointId pointId, EntityId owner)
    {
        foreach (var linkedPointId in SpatialQueries.GetLinkedPointIds(world, pointId))
        {
            world.Occupancy.PointOwner[linkedPointId] = owner;
        }
    }

    public static void FreePoint(WorldState world, PointId pointId, EntityId owner)
    {
        foreach (var linkedPointId in SpatialQueries.GetLinkedPointIds(world, pointId))
        {
            if (world.Occupancy.PointOwner.TryGetValue(linkedPointId, out var existing) && existing == owner)
            {
                world.Occupancy.PointOwner[linkedPointId] = null;
            }
        }
    }
}

}
