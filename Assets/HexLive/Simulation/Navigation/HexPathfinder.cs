using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Navigation
{

public static class HexPathfinder
{
    public static List<JunctionId> FindPath(WorldState world, JunctionId start, JunctionId goal)
    {
        if (start.Equals(goal))
        {
            return new List<JunctionId> { start };
        }

        var frontier = new Queue<JunctionId>();
        var cameFrom = new Dictionary<JunctionId, JunctionId?>();

        frontier.Enqueue(start);
        cameFrom[start] = null;

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            if (current.Equals(goal))
            {
                break;
            }

            if (!world.Junctions.Items.TryGetValue(current, out var junction))
            {
                continue;
            }

            foreach (var neighborId in junction.Neighbors)
            {
                if (cameFrom.ContainsKey(neighborId))
                {
                    continue;
                }

                if (!world.Junctions.Items.TryGetValue(neighborId, out var neighbor))
                {
                    continue;
                }

                if (neighbor.Blocked && !neighborId.Equals(goal))
                {
                    continue;
                }

                frontier.Enqueue(neighborId);
                cameFrom[neighborId] = current;
            }
        }

        if (!cameFrom.ContainsKey(goal))
        {
            return new List<JunctionId>();
        }

        var path = new List<JunctionId>();
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
