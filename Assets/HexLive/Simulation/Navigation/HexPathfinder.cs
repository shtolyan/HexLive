using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Navigation
{

public static class HexPathfinder
{
    public static List<TileCoord> FindPath(WorldState world, TileCoord start, TileCoord goal)
    {
        var frontier = new Queue<TileCoord>();
        var cameFrom = new Dictionary<TileCoord, TileCoord?>();

        frontier.Enqueue(start);
        cameFrom[start] = null;

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            if (current == goal)
            {
                break;
            }

            foreach (var neighbor in SpatialQueries.GetNeighbors(world, current))
            {
                if (!SpatialQueries.IsTileWalkable(world, neighbor) && neighbor != goal)
                {
                    continue;
                }

                if (cameFrom.ContainsKey(neighbor))
                {
                    continue;
                }

                frontier.Enqueue(neighbor);
                cameFrom[neighbor] = current;
            }
        }

        if (!cameFrom.ContainsKey(goal))
        {
            return new List<TileCoord>();
        }

        var path = new List<TileCoord>();
        var step = goal;
        while (true)
        {
            path.Add(step);
            var previous = cameFrom[step];
            if (previous is null)
            {
                break;
            }

            step = previous.Value;
        }

        path.Reverse();
        return path;
    }
}

}
