using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

internal static class HygieneMath
{
    private const int BathCandidateBudget = 12;
    private const int BathRouteMaxJunctions = 16;
    private const int BathApproachMaxJunctions = 64;
    private const int BathRouteExpansionBudget = 1500;

    public static bool IsShoreTile(WorldState world, TileCoord tile)
    {
        if (!world.Tiles.Items.TryGetValue(tile, out var here) ||
            here.Flags.HasFlag(TileFlags.Water) || !here.Flags.HasFlag(TileFlags.Walkable))
        {
            return false;
        }

        foreach (var direction in HexDirection.All)
        {
            var neighbour = new TileCoord(tile.Q + direction.DQ, tile.R + direction.DR);
            if (world.Tiles.Items.TryGetValue(neighbour, out var adjacent) &&
                adjacent.Flags.HasFlag(TileFlags.Water))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsBathingTile(WorldState world, TileCoord tile)
    {
        if (world.Tiles.Items.TryGetValue(tile, out var here) &&
            here.Flags.HasFlag(TileFlags.Water))
        {
            return true;
        }

        foreach (var direction in HexDirection.All)
        {
            var neighbour = new TileCoord(tile.Q + direction.DQ, tile.R + direction.DR);
            if (world.Tiles.Items.TryGetValue(neighbour, out var adjacent) &&
                adjacent.Flags.HasFlag(TileFlags.Water))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// §40.6 r8: choose the shore through the same physical router movement
    /// uses. Connectivity is deliberately coarser than execution: it ignores
    /// door state and actors, so using it as the auction promise could award a
    /// Bathe plan which failed four path retries, cooled down briefly and won
    /// again. Only the nearest bounded candidate set is searched, and every
    /// accepted shore must also own a short reversible dip.
    /// </summary>
    public static Junction? FindReachableBathShore(WorldState world, NPCState npc)
    {
        if (npc.CurrentJunction is not { } from ||
            !world.Junctions.Items.TryGetValue(from, out _))
        {
            return null;
        }

        var occupied = PathfindingSystem.OtherActorJunctions(world, npc);
        var candidates = world.Caches.BathShoreCandidatesScratch;
        candidates.Clear();
        var maxDistance = HexSpatialMath.HexRadius * 12f;
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                occupied.Contains(junction.Id) ||
                !SpatialQueries.IsJunctionFree(world, junction.Id) ||
                !IsShoreTile(world, junction.Tiles[0]))
            {
                continue;
            }

            var distance = HexSpatialMath.Distance(
                junction.WorldPosition, npc.Position);
            if (distance >= maxDistance)
            {
                continue;
            }

            InsertNearest(candidates, junction.Id, distance);
        }

        foreach (var candidate in candidates)
        {
            if (!HasBathApproachRoute(world, npc, from, candidate.Junction) ||
                FindRoundTripBathWater(world, npc, candidate.Junction) is null)
            {
                continue;
            }

            return world.Junctions.Items[candidate.Junction];
        }

        return null;
    }

    /// <summary>Exact, bounded land approach used by both bidding and planning.</summary>
    internal static bool HasBathApproachRoute(
        WorldState world, NPCState npc, JunctionId from, JunctionId to)
    {
        if (!world.Junctions.Items.TryGetValue(from, out _) ||
            !world.Junctions.Items.TryGetValue(to, out var target) ||
            target.Blocked)
        {
            return false;
        }

        var occupied = PathfindingSystem.OtherActorJunctions(world, npc);
        if ((npc.CurrentJunction is { } current && !from.Equals(current) &&
             occupied.Contains(from)) ||
            occupied.Contains(to))
        {
            return false;
        }

        var route = HexPathfinder.FindPath(
            world, from, to, occupied,
            weightClimb: true, canJump: PlanningSystem.CanUseRoutineTraversal(npc),
            danger: null, dangerCost: 0L,
            hardAvoid: DoorTopology.ForbiddenFor(world, npc.Faction),
            maxExpansions: BathRouteExpansionBudget);
        return route.Count is > 0 and <= BathApproachMaxJunctions;
    }

    private static void InsertNearest(
        System.Collections.Generic.List<(JunctionId Junction, float Distance)> candidates,
        JunctionId junction, float distance)
    {
        var insert = 0;
        while (insert < candidates.Count && candidates[insert].Distance <= distance)
        {
            insert++;
        }

        if (insert >= BathCandidateBudget)
        {
            return;
        }

        candidates.Insert(insert, (junction, distance));
        if (candidates.Count > BathCandidateBudget)
        {
            candidates.RemoveAt(candidates.Count - 1);
        }
    }

    /// <summary>
    /// §40.6 r7: choose nearby water by an exact, reversible route, not by
    /// straight-line distance.  A water junction can be two world units from
    /// shore and forty-eight graph edges away through a hut; if that door
    /// closes during the bath, the return becomes impossible.  Voluntary
    /// grooming therefore never routes through ANY door portal and rejects a
    /// trip longer than a local dip.  Twelve nearest candidates and a bounded
    /// A* keep this a small, deterministic planning operation.
    /// </summary>
    public static Junction? FindRoundTripBathWater(
        WorldState world, NPCState npc, JunctionId shore)
    {
        if (!world.Junctions.Items.TryGetValue(shore, out var shoreJunction))
        {
            return null;
        }

        // Ensure DoorByPortal is current, then copy it into a purpose-specific
        // hard-avoid set: even an open foreign door may close during 25 seconds
        // of bathing, so current closed-state bans are not sufficient.
        DoorTopology.ForbiddenFor(world, npc.Faction);
        var hardAvoid = world.Caches.VoluntaryWaterDoorAvoidScratch;
        hardAvoid.Clear();
        foreach (var portal in world.Caches.DoorByPortal.Keys)
        {
            hardAvoid.Add(portal);
        }

        var candidates = world.Caches.BathWaterCandidatesScratch;
        candidates.Clear();
        var maxDistance = HexSpatialMath.HexRadius * 3f;
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Id.Equals(shore) || junction.Blocked ||
                junction.Tiles.Count == 0 ||
                !world.Tiles.Items.TryGetValue(junction.Tiles[0], out var tile) ||
                !tile.Flags.HasFlag(TileFlags.Water) ||
                hardAvoid.Contains(junction.Id))
            {
                continue;
            }

            var distance = HexSpatialMath.Distance(
                shoreJunction.WorldPosition, junction.WorldPosition);
            if (distance > maxDistance)
            {
                continue;
            }

            // Tiny insertion-sorted nearest-N list: no world-sized sort and no
            // path search for every water junction on the island.
            InsertNearest(candidates, junction.Id, distance);
        }

        foreach (var candidate in candidates)
        {
            var outward = HexPathfinder.FindPath(
                world, shore, candidate.Junction, avoid: null,
                weightClimb: true, canJump: PlanningSystem.CanUseRoutineTraversal(npc),
                danger: null, dangerCost: 0L, hardAvoid: hardAvoid,
                maxExpansions: BathRouteExpansionBudget);
            if (outward.Count is 0 or > BathRouteMaxJunctions)
            {
                continue;
            }

            var home = HexPathfinder.FindPath(
                world, candidate.Junction, shore, avoid: null,
                weightClimb: true, canJump: PlanningSystem.CanUseRoutineTraversal(npc),
                danger: null, dangerCost: 0L, hardAvoid: hardAvoid,
                maxExpansions: BathRouteExpansionBudget);
            if (home.Count is 0 or > BathRouteMaxJunctions)
            {
                continue;
            }

            return world.Junctions.Items[candidate.Junction];
        }

        return null;
    }
}

}
