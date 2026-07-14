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
        return FindPath(world, start, goal, null, true);
    }

    // Spec §50: does crossing from `fromId` to `toId` need a jump? A hop is
    // armed (MovementSystem) whenever the tile stepped onto (a junction's
    // Tiles[0], the same rule normal walking uses) changes elevation — an
    // up/down step or a drop into water. A survivor who can't jump (a lost leg)
    // must not route across such an edge, so that terrain is off-limits to her.
    public static bool RequiresJump(WorldState world, JunctionId fromId, JunctionId toId)
    {
        if (!world.Junctions.Items.TryGetValue(fromId, out var from) || from.Tiles.Count == 0 ||
            !world.Junctions.Items.TryGetValue(toId, out var to) || to.Tiles.Count == 0)
        {
            return false;
        }

        if (!world.Tiles.Items.TryGetValue(from.Tiles[0], out var ft) ||
            !world.Tiles.Items.TryGetValue(to.Tiles[0], out var tt))
        {
            return false;
        }

        return ft.Elevation != tt.Elevation;
    }

    // Spec 24.3 (iteration 24): housemates are soft obstacles — avoid the
    // junctions they stand on; when that seals every route, fall back to
    // the direct path (never hard-stuck).
    // Spec 40.17: weightClimb applies the climb-seam detour preference (false
    // for hungry/thirsty NPCs so food/water routes stay short — fixes 12345).
    public static List<JunctionId> FindPath(
        WorldState world, JunctionId start, JunctionId goal,
        HashSet<JunctionId> avoid, bool weightClimb = true, bool canJump = true)
    {
        if (start.Equals(goal))
        {
            return new List<JunctionId> { start };
        }

        // Spec 40.17: uniform-cost (Dijkstra) search. Frontier ordered by
        // (gScore, seq): the seq term makes every priority unique, so a
        // SortedDictionary is a stable min-priority queue — and, unlike .NET 6's
        // PriorityQueue, it compiles under Unity's netstandard2.1. ClimbCost now
        // applies real per-edge weights (seam 1.2x, strait 2x, swim 4x — NOT
        // uniform), so ordering is genuine cheapest-first, not plain BFS.
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

                // Spec §50: a survivor who can't jump can't take an elevation
                // step (or dive water) — skip the edge entirely, even to the
                // goal (that spot is genuinely unreachable to her, not a detour).
                if (!canJump && RequiresJump(world, current, neighborId))
                {
                    continue;
                }

                var cost = gScore[current] + ClimbCost(world, current, neighborId, weightClimb);
                gScore[neighborId] = cost;
                cameFrom[neighborId] = current;
                frontier.Add(cost * priorityScale + seq++, neighborId);
            }
        }

        if (!cameFrom.ContainsKey(goal))
        {
            // Fully enclosed by standing housemates: take the direct path.
            return avoid is not null
                ? FindPath(world, start, goal, null, weightClimb, canJump)
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

    // Spec 40.17: a flat step costs FlatCost; a climb seam costs SeamCost (1.2x),
    // so a comfortable NPC prefers the flat way. Costs are ×10 so a fractional
    // multiplier stays integer. The seam weight is LIVE — SHIPPED in commit
    // "climb weight SHIPPED — food-exempt detour + dog margin" (spec 40.17):
    // hungry/thirsty NPCs pass weightClimb=false and ignore the seam so food /
    // water routes stay short; only comfortable NPCs pay the detour.
    // HISTORICAL (pre-rebalance): an earlier pass found ANY seam weight reshuffled
    // the deterministic dog-dance — 2x/1.5x collapsed several seeds and even 1.2x
    // killed seed 777. That predates the food-exemption + dog-margin rebalance and
    // NO LONGER holds (777 wins d25 in the current soak). Balance is still
    // knife-edge — re-soak ALL seeds before touching these constants.
    private const long FlatCost = 10L;
    // 1.2x, applied only when weightClimb (comfortable NPCs). A harness sweep
    // 2026-07-13 (1.0x-4.0x, 12 seeds x 40d) confirmed raising it is NOT worth
    // it: total hops stay ~8-10k at EVERY weight (the hops are FORCED survival
    // crossings by hungry NPCs, who are weight-exempt — a detour can't route
    // around the only path to food/water), while survival just reshuffles as
    // noise (WIN bounced 6/6/5/4/6/7/6/6). 1.2x is the mild nudge that lets a
    // COMFORTABLE NPC prefer a flat route WHEN one exists, with no survival hit.
    private const long SeamCost = 12L;

    // Spec 40.18: entering the swim ring costs 4x a land step — a slow, risky
    // last resort, so a route only takes to the water when there's no dry way.
    private const long SwimCost = 40L;

    // Spec 40.18 step 4: the strait to the second island is a cheap swim (2x),
    // so a foraging NPC will actually make the hop for an island-exclusive
    // resource. The wider ring stays SwimCost (4x), a shark-risked last resort.
    private const long StraitCost = 20L;

    private static long ClimbCost(WorldState world, JunctionId from, JunctionId to, bool weightClimb)
    {
        if (world.StraitJunctions.Contains(to))
        {
            return StraitCost;
        }

        if (world.SwimJunctions.Contains(to))
        {
            return SwimCost;
        }

        if (!weightClimb)
        {
            return FlatCost;
        }

        return world.ClimbSeams.Contains(to) ? SeamCost : FlatCost;
    }
}

}
