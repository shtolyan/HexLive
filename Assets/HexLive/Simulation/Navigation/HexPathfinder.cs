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
        return FindPath(world, start, goal, null);
    }

    // Spec 24.3 (iteration 24): housemates are soft obstacles — avoid the
    // junctions they stand on; when that seals every route, fall back to
    // the direct path (never hard-stuck).
    public static List<JunctionId> FindPath(
        WorldState world, JunctionId start, JunctionId goal,
        HashSet<JunctionId> avoid)
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

                if (avoid is not null && avoid.Contains(neighborId) && !neighborId.Equals(goal))
                {
                    continue;
                }

                frontier.Enqueue(neighborId);
                cameFrom[neighborId] = current;
            }
        }

        if (!cameFrom.ContainsKey(goal))
        {
            // Fully enclosed by standing housemates: take the direct path.
            return avoid is not null
                ? FindPath(world, start, goal, null)
                : new List<JunctionId>();
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
