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

    public static bool IsPointFree(WorldState world, PointId pointId)
    {
        foreach (var linkedPointId in GetLinkedPointIds(world, pointId))
        {
            if (!world.Occupancy.PointOwner.TryGetValue(linkedPointId, out var owner) || owner is not null)
            {
                return false;
            }
        }

        return true;
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

    public static IEnumerable<Point> GetPointsForTile(WorldState world, TileCoord coord)
    {
        if (!world.Tiles.Items.TryGetValue(coord, out var tile))
        {
            yield break;
        }

        foreach (var pointId in tile.Points)
        {
            if (world.Points.Items.TryGetValue(pointId, out var point))
            {
                yield return point;
            }
        }
    }

    public static IEnumerable<PointId> GetLinkedPointIds(WorldState world, PointId pointId)
    {
        if (!world.Points.Items.TryGetValue(pointId, out var point))
        {
            yield break;
        }

        if (point.ConnectionGroupId is null || !world.ConnectionGroups.Items.TryGetValue(point.ConnectionGroupId.Value, out var junction))
        {
            yield return pointId;
            yield break;
        }

        foreach (var memberPointId in junction.Points)
        {
            yield return memberPointId;
        }
    }
}

}
