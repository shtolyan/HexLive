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

        // Spec 40.17: uniform-cost search. Frontier ordered by (gScore, seq):
        // the seq term makes every priority unique, so a SortedDictionary is a
        // stable min-priority queue — and, unlike .NET 6's PriorityQueue, it
        // compiles under Unity's netstandard2.1. While ClimbCost is uniform the
        // ordering is exactly BFS (byte-identical, md5-verified against the
        // pre-change soak); a per-edge seam weight drops in via ClimbCost.
        const long priorityScale = 100_000_000L;
        var frontier = new SortedDictionary<long, JunctionId>();
        var cameFrom = new Dictionary<JunctionId, JunctionId?>();
        var gScore = new Dictionary<JunctionId, long>();
        var seq = 0L;

        frontier.Add(0L, start);
        cameFrom[start] = null;
        gScore[start] = 0L;

        while (frontier.Count > 0)
        {
            var head = default(KeyValuePair<long, JunctionId>);
            foreach (var kv in frontier) { head = kv; break; } // lowest priority
            frontier.Remove(head.Key);
            var current = head.Value;
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
                frontier.Add(cost * priorityScale + seq++, neighborId);
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

    // Spec 40.17: a flat step costs FlatCost; a climb seam costs SeamCost, so
    // routes could prefer the flat way. Costs are scaled by 10 so a fractional
    // multiplier stays integer. EMPIRICAL FINDING: no simple weight holds the
    // fragile colony green — 2x worsened 3 of 6 seeds (deaths 1->2), and 1.5x
    // COLLAPSED 3 seeds outright. The reshuffle of the deterministic dog-dance
    // is chaotic per multiplier, so the weight needs a dedicated rebalance
    // iteration (dog spawns / home layout), not a tweak. SeamCost stays ==
    // FlatCost (uniform, byte-identical to BFS) until that pass; flip SeamCost
    // to enable the weight and rebalance.
    private const long FlatCost = 10L;
    private const long SeamCost = 10L; // 1.0x = uniform; see finding above

    // Spec 40.18: entering the swim ring costs 4x a land step — a slow, risky
    // last resort, so a route only takes to the water when there's no dry way.
    private const long SwimCost = 40L;

    private static long ClimbCost(WorldState world, JunctionId from, JunctionId to)
    {
        if (world.SwimJunctions.Contains(to))
        {
            return SwimCost;
        }

        return world.ClimbSeams.Contains(to) ? SeamCost : FlatCost;
    }
}

}
