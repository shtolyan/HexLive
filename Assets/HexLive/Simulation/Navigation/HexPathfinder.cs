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

    // Spec §50: the tile reached by a directed step. Shared boundary junctions
    // own two or three tiles, and Tiles[0] is generation order, not movement
    // direction. Look just beyond the target junction along the travel vector:
    // crossing a border picks the tile on the far side, while walking along the
    // border has no strong forward tile and falls back to the shared current
    // side. MovementSystem uses the same resolver for hop arming and tile
    // bookkeeping, so pathability and execution agree.
    public static bool TryGetDirectedStepTile(
        WorldState world, JunctionId fromId, JunctionId toId, out Tile tile)
    {
        tile = default;
        if (!world.Junctions.Items.TryGetValue(fromId, out var from) ||
            !world.Junctions.Items.TryGetValue(toId, out var to) ||
            to.Tiles.Count == 0)
        {
            return false;
        }

        if (to.Tiles.Count == 1)
        {
            return world.Tiles.Items.TryGetValue(to.Tiles[0], out tile);
        }

        var direction = HexSpatialMath.Normalize(to.WorldPosition - from.WorldPosition);
        var bestForward = float.NegativeInfinity;
        Tile bestTile = default;
        var hasBest = false;
        foreach (var coord in to.Tiles)
        {
            if (!world.Tiles.Items.TryGetValue(coord, out var candidate))
            {
                continue;
            }

            var fromTargetToCenter = HexSpatialMath.TileToWorld(coord) - to.WorldPosition;
            var forward = fromTargetToCenter.X * direction.X + fromTargetToCenter.Y * direction.Y;
            if (!hasBest || forward > bestForward)
            {
                bestForward = forward;
                bestTile = candidate;
                hasBest = true;
            }
        }

        const float ForwardTileThreshold = 0.1f;
        if (hasBest && bestForward > ForwardTileThreshold)
        {
            tile = bestTile;
            return true;
        }

        foreach (var coord in from.Tiles)
        {
            if (to.Tiles.Contains(coord) &&
                world.Tiles.Items.TryGetValue(coord, out tile))
            {
                return true;
            }
        }

        if (hasBest)
        {
            tile = bestTile;
            return true;
        }

        return false;
    }

    // Spec §50: does crossing from `fromId` to `toId` need a jump? A survivor
    // who can't jump (a lost leg) must not route across an elevation edge, so
    // that terrain is off-limits to her.
    public static bool RequiresJump(WorldState world, JunctionId fromId, JunctionId toId)
    {
        if (!TryGetDirectedStepTile(world, toId, fromId, out var fromTile) ||
            !TryGetDirectedStepTile(world, fromId, toId, out var toTile))
        {
            return false;
        }

        return fromTile.Elevation != toTile.Elevation;
    }

    // Spec 24.3 (iteration 24): housemates are soft obstacles — avoid the
    // junctions they stand on; when that seals every route, fall back to
    // the direct path (never hard-stuck).
    // Spec 40.17: weightClimb applies the climb-seam detour preference (false
    // for hungry/thirsty NPCs so food/water routes stay short — fixes 12345).
    // Spec §62: `danger` junctions (the ring around a live mob) add
    // `dangerCost` per step — a soft weight, not a wall, so an unfit girl
    // detours around the wolf yet can still cross if the map leaves no choice.
    // Spec 29C.3: `hardAvoid` is TERRAIN the walker may never step on (a
    // mob's indoor/door/water ban) — unlike `avoid` (standing actors, a
    // courtesy), it survives the enclosed-fallback retry below: a dog boxed
    // out by housemates may push through THEM, never through the hut wall.
    public static List<JunctionId> FindPath(
        WorldState world, JunctionId start, JunctionId goal,
        HashSet<JunctionId> avoid, bool weightClimb = true, bool canJump = true,
        HashSet<JunctionId> danger = null, long dangerCost = 0L,
        HashSet<JunctionId> hardAvoid = null)
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

                // Climb-seam junctions are jump thresholds, not footpaths.
                // Traversing seam->seam lets an NPC walk along the vertical lip
                // and then wedge into the wall; legal routes must approach the
                // seam from one side and leave on the other.
                if (IsClimbSeamWalk(world, current, neighborId))
                {
                    continue;
                }

                if (avoid is not null && avoid.Contains(neighborId) && !neighborId.Equals(goal))
                {
                    continue;
                }

                if (hardAvoid is not null && hardAvoid.Contains(neighborId) &&
                    !neighborId.Equals(goal))
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
                if (danger is not null && danger.Contains(neighborId))
                {
                    cost += dangerCost;
                }

                gScore[neighborId] = cost;
                cameFrom[neighborId] = current;
                frontier.Add(cost * priorityScale + seq++, neighborId);
            }
        }

        if (!cameFrom.ContainsKey(goal))
        {
            // Fully enclosed by standing housemates: take the direct path.
            // hardAvoid stays — terrain bans are walls, not courtesies.
            return avoid is not null
                ? FindPath(world, start, goal, null, weightClimb, canJump, danger, dangerCost, hardAvoid)
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
    // 3x, applied when weightClimb. A 1.2x nudge was too weak for short
    // sawtooth paths: an NPC could still step down-up-down along a ledge while
    // fetching non-emergency materials because the hop barely cost more than a
    // flat move.
    private const long SeamCost = 30L;

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

    private static bool IsClimbSeamWalk(WorldState world, JunctionId from, JunctionId to)
    {
        return world.ClimbSeams.Contains(from) && world.ClimbSeams.Contains(to);
    }
}

}
