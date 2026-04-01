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

    public static bool TryReserveJunction(WorldState world, JunctionId junctionId, EntityId owner, int currentTick, int durationTicks)
    {
        if (world.Reservations.Junctions.TryGetValue(junctionId, out var existing))
        {
            if (existing.Owner != owner && existing.EndTick >= currentTick)
            {
                return false;
            }
        }

        world.Reservations.Junctions[junctionId] = new ReservationRecord
        {
            Owner = owner,
            StartTick = currentTick,
            EndTick = currentTick + durationTicks
        };

        return true;
    }

    public static void ReleaseJunctionReservation(WorldState world, JunctionId junctionId, EntityId owner)
    {
        if (world.Reservations.Junctions.TryGetValue(junctionId, out var existing) && existing.Owner == owner)
        {
            world.Reservations.Junctions.Remove(junctionId);
        }
    }

    public static void OccupyJunction(WorldState world, JunctionId junctionId, EntityId owner)
    {
        world.Occupancy.JunctionOwner[junctionId] = owner;
    }

    public static void FreeJunction(WorldState world, JunctionId junctionId, EntityId owner)
    {
        if (world.Occupancy.JunctionOwner.TryGetValue(junctionId, out var existing) && existing == owner)
        {
            world.Occupancy.JunctionOwner[junctionId] = null;
        }
    }
}

}
