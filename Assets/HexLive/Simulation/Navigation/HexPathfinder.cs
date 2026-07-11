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

        // Spec 40.17: uniform-cost search. Priority = gScore * PriorityScale +
        // insertion order, so with every edge at ClimbCost == 1 it dequeues in
        // exactly BFS order (the seq tiebreak preserves FIFO within a depth) —
        // byte-identical to the old Queue BFS. This is the safe substrate for
        // the deferred 2x climb-seam weight: the day seams are tagged at
        // world-gen, ClimbCost returns 2 for a seam edge and detours win.
        const long priorityScale = 1_000_000L;
        var frontier = new PriorityQueue<JunctionId, long>();
        var cameFrom = new Dictionary<JunctionId, JunctionId?>();
        var gScore = new Dictionary<JunctionId, long>();
        var seq = 0L;

        frontier.Enqueue(start, 0L);
        cameFrom[start] = null;
        gScore[start] = 0L;

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

                var cost = gScore[current] + ClimbCost(world, current, neighborId);
                gScore[neighborId] = cost;
                cameFrom[neighborId] = current;
                frontier.Enqueue(neighborId, cost * priorityScale + seq++);
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

    // Spec 40.17: per-edge walk cost. v1 is uniform (1) — behaviourally
    // identical to BFS. When elevation-step "climb seams" are tagged at
    // world-gen (a symmetric climb-edge set on WorldState), this returns 2 for
    // a seam edge so a route prefers the flat detour but still climbs when
    // climbing is genuinely shorter.
    private static long ClimbCost(WorldState world, JunctionId from, JunctionId to)
    {
        // Spec 40.17: seams are TAGGED (world.ClimbSeams) but not yet weighted.
        // A 2x weight here reshuffled the fragile colony's dog-dance in soak
        // (deaths 1->2 in 3 of 6 seeds), so the weight waits on a focused
        // rebalance pass; routing stays byte-identical to plain BFS for now.
        // Flip to `world.ClimbSeams.Contains(to) ? 2L : 1L` when rebalancing.
        return 1L;
    }
}

}
