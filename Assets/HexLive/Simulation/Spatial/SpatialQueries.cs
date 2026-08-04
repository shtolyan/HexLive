using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
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

    // The largest gap between two adjacent sub-grid junctions (~0.75 wu on a
    // boundary row). "One point away" from the object means a stand no farther
    // than the object's footprint plus this single step — see BesideReach.
    public const float StandStepAllowance = 0.80f;

    // The farthest a legitimate "beside" stand may sit from an object's anchor:
    // its solid footprint (ObstacleRadius) plus one sub-grid step. Anything past
    // this is reaching ACROSS a wall/water/cliff — the object is not adjacently
    // reachable and the plan must retarget, not interact from afar (user: crafts,
    // harvesting and building must all happen at the smallest hop, never a whole
    // hex out). Point items (radius 0) → one step; the campfire (0.55R) keeps its
    // ~1.1 wu warming rim; a bed (1.39) its footprint edge.
    public static float BesideReach(float obstacleRadius) =>
        System.Math.Max(0f, obstacleRadius) + StandStepAllowance;

    // ⭐ §26.6A r5: WHY the rim is being walked, because the answer differs and
    // conflating the two cost a colony. The §26.6A table already separates
    // "am I close enough" from "does a route exist"; this is that same line,
    // drawn one level down where the BFS can see it.
    public enum RimPurpose
    {
        /// <summary>«Дотянусь ли отсюда» — чужое тело стена (r5).</summary>
        Reach,

        /// <summary>«Есть ли где встать рядом ВООБЩЕ» — тело обходят, стеной
        /// остаётся только терраин (§31C.7 / r4). Восприятие спрашивает это.</summary>
        Route,
    }

    // Spec §26.6A r4: a junction closed by TERRAIN — a cliff face (owning tiles
    // more than one level apart), the open sea, a hut wall, an authored block.
    // The ROUTE question stops here and no further: a body is walked around.
    public static bool IsTerrainBlocked(WorldState world, JunctionId junctionId)
    {
        if (!world.Junctions.Items.TryGetValue(junctionId, out var junction) || !junction.Blocked)
        {
            return false;
        }

        if (world.ObjectBlockBuiltVersion != world.TopologyVersion)
        {
            RebuildObjectBlocked(world);
        }

        return !world.ObjectBlockedJunctions.Contains(junctionId) &&
               !IsAllWaterJunction(world, junctionId);
    }

    private static void RebuildObjectBlocked(WorldState world)
    {
        world.ObjectBlockedJunctions.Clear();
        foreach (var worldObject in world.Entities.Objects.Values)
        {
            foreach (var junctionId in worldObject.BlockedJunctions)
            {
                world.ObjectBlockedJunctions.Add(junctionId);
            }
        }

        world.ObjectBlockBuiltVersion = world.TopologyVersion;
    }

    // ⭐ Spec §26.6A r5: is this junction a WALL for a pair of hands reaching
    // toward `target`? Blocked junctions come in three kinds, and only now are
    // all three told apart:
    //
    //   - the TARGET's OWN footprint — the trunk when she fells the palm, the
    //     ember ring when she tends the fire, the frame when she makes the bed.
    //     CROSSABLE: you work a thing from its rim, and that is what makes
    //     fireside work and furniture interactions possible at all.
    //   - SOMEONE ELSE's footprint — a wall. r4 let the rim BFS walk through
    //     ANY object, and the geometry makes that exactly wide enough to matter:
    //     the sub-grid step is 0.375 wu and BesideReach(0) is 0.80 wu, i.e. two
    //     steps, i.e. exactly one blocked junction fits between the hand and the
    //     prize. A palm trunk IS exactly one blocked junction (tree.palm carries
    //     no ObstacleRadius, so only its anchor closes) and a coconut drops on a
    //     free junction of the palm's own tile — so "pierce the nut THROUGH the
    //     trunk" was not a near miss, it was the arithmetic working as written.
    //   - TERRAIN — a cliff face (§20.16), a hut wall, an authored block, the
    //     open sea. A wall for everyone, `target` or not.
    //
    // Water is deliberately never a barrier: leaning over the bank to drink or
    // to take flotsam has always been legitimate, and shore work stays allowed.
    //
    // `target` null = the strictest reading (nothing may be crossed) — that is
    // what a caller with no object in hand (furniture placement) actually wants.
    //
    // ⚠️ MEASURED, and the reason `purpose` exists at all: applying the strict
    // reading to the ROUTE question too (perception's ReachableBeside) cost the
    // colony 86/120 → 72/120 alive over 30 seeds × 10 days, with two total
    // wipes where there had been none — objects quietly stopped being
    // "reachable" and dropped out of candidate lists with no PlanFailed to show
    // for it. Reaching through a trunk is a lie; walking around one is not.
    public static bool IsBarrierFor(
        WorldState world, JunctionId junctionId, WorldObjectState target,
        RimPurpose purpose = RimPurpose.Reach)
    {
        if (purpose == RimPurpose.Route)
        {
            return IsTerrainBlocked(world, junctionId);
        }

        if (!world.Junctions.Items.TryGetValue(junctionId, out var junction) || !junction.Blocked)
        {
            return false;
        }

        if (IsAllWaterJunction(world, junctionId))
        {
            return false;
        }

        return target is null || !target.BlockedJunctions.Contains(junctionId);
    }

    // Spec §26.6A r4/r5: may the object anchored at `anchor` be TOUCHED from
    // `stand`? The gap between them may cross water and `target`'s own
    // footprint — never terrain, and never a THIRD object standing in the way.
    // The same borders that stop the pathfinder stop the hands. Without this an
    // NPC standing one sub-grid step BELOW a ledge pierced a coconut, felled a
    // palm or sat on a stump straight through the cliff face, because 0.75 wu of
    // straight-line distance said "adjacent" (r4) — and, with the border honest,
    // still reached the coconut through the palm beside it (r5).
    public static bool CanTouchAcross(
        WorldState world, JunctionId stand, JunctionId anchor, float maxDist,
        WorldObjectState target = null, RimPurpose purpose = RimPurpose.Reach)
    {
        if (stand.Equals(anchor))
        {
            return true;
        }

        CollectStandableAround(world, anchor, _touchScratch, 96, maxDist, target, purpose);
        return _touchScratch.Contains(stand);
    }

    private static readonly List<JunctionId> _touchScratch = new();

    // Spec 31C.7: walk the blocked/wet cluster outward from an anchor and
    // collect the passable dry junctions on its rim — "stand at the edge
    // of the furniture / on the river bank". BFS bounded by maxVisited.
    // maxAnchorDist caps how far the rim may sit from the anchor (BesideReach):
    // rim junctions past it are dropped and the BFS never walks beyond it, so a
    // boxed-in object yields an EMPTY result (unreachable) instead of a spot a
    // whole hex away.
    // §26.6A r4/r5: the walk crosses water and `owner`'s OWN footprint only —
    // any other blocked junction (a cliff face, a hut wall, the open sea, or
    // another object's body: the palm between her and the nut) ends that branch,
    // so the rim never appears on the far side of a border nobody can walk over.
    // `owner` is the object this rim is being built FOR; null = cross nothing.
    public static void CollectStandableAround(
        WorldState world, HexLive.Simulation.Common.JunctionId anchor,
        System.Collections.Generic.List<HexLive.Simulation.Common.JunctionId> results,
        int maxVisited = 96, float maxAnchorDist = float.MaxValue,
        WorldObjectState owner = null, RimPurpose purpose = RimPurpose.Reach)
    {
        results.Clear();
        var hasCap = maxAnchorDist < float.MaxValue;
        var anchorPos = hasCap && world.Junctions.Items.TryGetValue(anchor, out var anchorJ)
            ? anchorJ.WorldPosition : default;
        var capSq = maxAnchorDist * maxAnchorDist;
        var visited = new System.Collections.Generic.HashSet<HexLive.Simulation.Common.JunctionId> { anchor };
        var frontier = new System.Collections.Generic.Queue<HexLive.Simulation.Common.JunctionId>();
        frontier.Enqueue(anchor);
        while (frontier.Count > 0 && visited.Count < maxVisited)
        {
            var currentId = frontier.Dequeue();
            if (!world.Junctions.Items.TryGetValue(currentId, out var current))
            {
                continue;
            }

            foreach (var neighborId in current.Neighbors)
            {
                if (!visited.Add(neighborId) ||
                    !world.Junctions.Items.TryGetValue(neighborId, out var neighbor))
                {
                    continue;
                }

                if (hasCap)
                {
                    var dx = neighbor.WorldPosition.X - anchorPos.X;
                    var dy = neighbor.WorldPosition.Y - anchorPos.Y;
                    if (dx * dx + dy * dy > capSq)
                    {
                        continue; // beyond one hop of the footprint — prune, never reach here
                    }
                }

                var wet = IsAllWaterJunction(world, neighborId);
                if (!neighbor.Blocked && !wet)
                {
                    results.Add(neighborId); // rim found; do not expand past it
                }
                else if (IsBarrierFor(world, neighborId, owner, purpose))
                {
                    continue; // cliff / wall / open sea / another body — border ends here
                }
                else
                {
                    frontier.Enqueue(neighborId); // inside the cluster; keep walking
                }
            }
        }
    }

    // Spec 31C.7: a junction strictly inside water (every owning tile is
    // Water). Shore junctions (mixed land+water) do not count.
    public static bool IsAllWaterJunction(WorldState world, JunctionId junctionId)
    {
        if (!world.Junctions.Items.TryGetValue(junctionId, out var junction) ||
            junction.Tiles.Count == 0)
        {
            return false;
        }

        foreach (var coord in junction.Tiles)
        {
            if (!world.Tiles.Items.TryGetValue(coord, out var tile) ||
                !tile.Flags.HasFlag(TileFlags.Water))
            {
                return false;
            }
        }

        return true;
    }

    // §106/§40.18-B: deep water = swimming; walkable river shallows are waded,
    // not swum. THE definition of "in the water" — MovementSystem (entry pause,
    // stroke speed) and every combat gate read this one predicate, so the sim
    // cannot disagree with itself about who is swimming. Note TileFlags.Swimmable
    // is a dead flag (declared, never set) — this is deliberately not it.
    public static bool IsSwimTile(Tile tile) =>
        (tile.Flags & TileFlags.Water) != 0 && (tile.Flags & TileFlags.Walkable) == 0;

    // Measured per TILE, not per junction: shore junctions are mixed land+water,
    // and a girl standing on their dry tile is not swimming.
    public static bool IsSwimTile(WorldState world, TileCoord coord) =>
        world.Tiles.Items.TryGetValue(coord, out var tile) && IsSwimTile(tile);

    // §54.9A: true when every junction inside `radius` of the anchor is
    // passable dry ground — the piece's PHYSICAL footprint fits here without
    // crossing walls, obstacle-blocked junctions (boulders, palms, the fire's
    // ember ring, other furniture) or water. Radius 0 = point object, always fits.
    public static bool FootprintClear(WorldState world, Junction anchor, float radius)
    {
        if (radius <= 0f)
        {
            return true;
        }

        if (anchor.Tiles.Count == 0)
        {
            return false;
        }

        var center = anchor.WorldPosition;
        var radiusSq = radius * radius;
        var home = anchor.Tiles[0];
        for (var i = -1; i < HexDirection.All.Length; i++)
        {
            var coord = i < 0
                ? home
                : new TileCoord(home.Q + HexDirection.All[i].DQ, home.R + HexDirection.All[i].DR);
            if (!world.Tiles.Items.TryGetValue(coord, out var tile))
            {
                continue;
            }

            foreach (var junctionId in tile.Junctions)
            {
                if (!world.Junctions.Items.TryGetValue(junctionId, out var junction))
                {
                    continue;
                }

                var dx = junction.WorldPosition.X - center.X;
                var dy = junction.WorldPosition.Y - center.Y;
                if (dx * dx + dy * dy > radiusSq)
                {
                    continue;
                }

                if (junction.Blocked || IsAllWaterJunction(world, junctionId))
                {
                    return false;
                }
            }
        }

        return true;
    }

    // Ground sleep is rendered by a long lying animation, not by a point-sized
    // pawn. Keep the whole body envelope clear of blocked junctions so a
    // fireside nap cannot visually land inside the campfire ember ring.
    public static bool LyingBodyClear(WorldState world, Junction anchor) =>
        FootprintClear(world, anchor, HexSpatialMath.HexRadius);

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
            if (pair.Value.Blocked)
            {
                continue; // spec 35.3: never resolve onto a wall
            }

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
