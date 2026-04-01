using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Spatial
{

public static class SpatialQueries
{
    public static bool IsTileWalkable(WorldState world, TileCoord coord)
    {
        return world.Tiles.Items.TryGetValue(coord, out var tile) &&
               tile.Flags.HasFlag(TileFlags.Walkable) &&
               !tile.Flags.HasFlag(TileFlags.Blocked);
    }

    public static bool IsJunctionFree(WorldState world, JunctionId junctionId)
    {
        if (!world.Occupancy.JunctionOwner.TryGetValue(junctionId, out var owner))
        {
            return true;
        }

        return owner is null;
    }

    public static bool IsJunctionPassable(WorldState world, JunctionId junctionId)
    {
        if (!world.Junctions.Items.TryGetValue(junctionId, out var junction))
        {
            return false;
        }

        return !junction.Blocked;
    }

    public static IReadOnlyList<TileCoord> GetNeighbors(WorldState world, TileCoord origin)
    {
        var neighbors = new List<TileCoord>(6);
        foreach (var direction in HexDirection.All)
        {
            var candidate = new TileCoord(origin.Q + direction.DQ, origin.R + direction.DR);
            if (world.Tiles.Items.ContainsKey(candidate))
            {
                neighbors.Add(candidate);
            }
        }

        return neighbors;
    }

    public static IReadOnlyList<JunctionId> GetPassableNeighbors(WorldState world, JunctionId junctionId)
    {
        if (!world.Junctions.Items.TryGetValue(junctionId, out var junction))
        {
            return System.Array.Empty<JunctionId>();
        }

        var result = new List<JunctionId>();
        foreach (var neighborId in junction.Neighbors)
        {
            if (world.Junctions.Items.TryGetValue(neighborId, out var neighbor) && !neighbor.Blocked)
            {
                result.Add(neighborId);
            }
        }

        return result;
    }

    public static JunctionId? FindNearestJunction(WorldState world, Float2 worldPosition)
    {
        JunctionId? nearest = null;
        var bestDist = float.MaxValue;

        foreach (var pair in world.Junctions.Items)
        {
            var dist = HexSpatialMath.Distance(worldPosition, pair.Value.WorldPosition);
            if (dist < bestDist)
            {
                bestDist = dist;
                nearest = pair.Key;
            }
        }

        return nearest;
    }
}

}
