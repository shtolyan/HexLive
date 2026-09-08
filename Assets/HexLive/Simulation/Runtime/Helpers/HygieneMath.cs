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

    /// <summary>
    /// §40.6 r14: с этого узла можно стоять на берегу — у него есть сухой
    /// ходибельный тайл, соседствующий с водой. Раньше спрашивался ТОЛЬКО
    /// <c>Tiles[0]</c>, и это отсекало как раз пограничные узлы, у которых
    /// первым числится вода, — то есть ровно те, что стоят у самой кромки.
    /// </summary>
    private static bool StandsOnShore(WorldState world, Junction junction)
    {
        foreach (var tile in junction.Tiles)
        {
            if (IsShoreTile(world, tile))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>§40.6 r14: узел стоит на самой границе — ему принадлежит и вода.</summary>
    private static bool TouchesWater(WorldState world, Junction junction)
    {
        foreach (var tile in junction.Tiles)
        {
            if (world.Tiles.Items.TryGetValue(tile, out var state) &&
                state.Flags.HasFlag(TileFlags.Water))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// §40.6 r14 (#175): тайл, на котором у этого узла СТОЯТ, — первый сухой
    /// ходибельный. У пограничного узла первым может числиться вода, и план,
    /// бравший <c>Tiles[0]</c> как цель прибытия, делал целью ВОДУ — а явная
    /// водная цель у Bathe включает «прибытие = пересечь кромку» (§21.21B
    /// v25), и стирающая вставала в море. Стоять надо на песке; в воду ведёт
    /// только шаг SwimBathe со своим водным тайлом.
    /// </summary>
    public static TileCoord? DryStandTile(WorldState world, Junction junction)
    {
        // Первый проход — сухой тайл, который САМ берег (смежен с водой):
        // у узла бывает и внутренний сухой тайл, и, встав на него, она
        // стирала бы не у кромки (замер: seed 12345, тайл 2,10). У узла,
        // прошедшего StandsOnShore, такой тайл есть всегда.
        TileCoord? dry = null;
        foreach (var tile in junction.Tiles)
        {
            if (!world.Tiles.Items.TryGetValue(tile, out var state) ||
                !state.Flags.HasFlag(TileFlags.Walkable) ||
                state.Flags.HasFlag(TileFlags.Water))
            {
                continue;
            }

            if (IsShoreTile(world, tile))
            {
                return tile;
            }

            dry ??= tile;
        }

        if (dry is { } inland)
        {
            return inland;
        }

        return junction.Tiles.Count > 0 ? junction.Tiles[0] : null;
    }

    /// <summary>
    /// #175: стирка числится в гексе, где стоит стирающая, а прибытие
    /// (§21.21B v24, «ближняя сторона») могло записать ей ВНУТРЕННИЙ тайл
    /// берегового узла: физически точка та же, но такт шёл «не у воды»
    /// (замер: seed 12345, t=60325, тайл 2,10 узла 17253). Перед тактом
    /// тайл приводится к береговому тайлу её узла тем же щадящим правилом,
    /// что и <c>PlaceAtEdge</c>: только соседний гекс и не больше одной
    /// ступени высоты. Возвращает, стоит ли она теперь у воды.
    /// </summary>
    public static bool TryAnchorShoreStand(WorldState world, NPCState npc)
    {
        if (npc.CurrentJunction is { } at &&
            world.Junctions.Items.TryGetValue(at, out var junction) &&
            DryStandTile(world, junction) is { } stand &&
            IsShoreTile(world, stand) &&
            npc.Tile != stand &&
            HexSpatialMath.HexDistance(npc.Tile, stand) <= 1 &&
            world.Tiles.Items.TryGetValue(npc.Tile, out var from) &&
            world.Tiles.Items.TryGetValue(stand, out var to) &&
            System.Math.Abs(from.Elevation - to.Elevation) <= 1)
        {
            var previous = npc.Tile;
            npc.Tile = stand;
            SpatialMutations.MoveEntityToTile(world, npc.Id, previous, stand);
        }

        return IsBathingTile(world, npc.Tile);
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
        // §158.4: окрестность вместо всего графа — фильтры и порядок те же.
        var nearby = world.Caches.LocalSearchScratch;
        LocalSearch.CollectWithinTiles(world, npc.Tile, LocalSearch.TileRadiusCovering(maxDistance), nearby);
        foreach (var junction in nearby)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                occupied.Contains(junction.Id) ||
                !SpatialQueries.IsJunctionFree(world, junction.Id) ||
                !StandsOnShore(world, junction))
            {
                continue;
            }

            var distance = HexSpatialMath.Distance(
                junction.WorldPosition, npc.Position);
            if (distance >= maxDistance)
            {
                continue;
            }

            // ⭐ §40.6 r14 (#147): У САМОЙ ВОДЫ, а не «где-то в песке». Тайл
            // берега — это целый гекс шириной 2.6 wu, и его узлы разбросаны по
            // всей ширине: раньше годился любой, и стирка запросто игралась в
            // метре от кромки. Узел, которому принадлежит и водяной тайл, стоит
            // ровно на границе — его и предпочитаем, доплачивая за него не
            // больше двух радиусов лишнего хода.
            var effective = TouchesWater(world, junction)
                ? distance
                : distance + HexSpatialMath.HexRadius * 2f;
            InsertNearest(candidates, junction.Id, effective);
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
        // §158.4: вода ищется вокруг берегового узла, не по всему графу.
        var nearby = world.Caches.LocalSearchScratch;
        LocalSearch.CollectWithinTiles(world, shoreJunction.Tiles[0],
            LocalSearch.TileRadiusCovering(maxDistance), nearby);
        foreach (var junction in nearby)
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
