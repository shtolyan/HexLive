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

    // §40.17 v2: the signed elevation change of a directed step, resolved from
    // the tiles the walker actually leaves and enters. This is THE definition of
    // "this step is a jump" — the hop arming in MovementSystem reads the same
    // resolver, and worldgen bakes it into Junction.NeighborStepDelta so the
    // pathfinder never has to run it in its inner loop.
    public static int ResolveStepDelta(WorldState world, JunctionId fromId, JunctionId toId)
    {
        if (!TryGetDirectedStepTile(world, toId, fromId, out var fromTile) ||
            !TryGetDirectedStepTile(world, fromId, toId, out var toTile))
        {
            return 0;
        }

        return toTile.Elevation - fromTile.Elevation;
    }

    // The baked value for the step from -> from.Neighbors[neighborIndex], with a
    // live fallback for graphs built by hand (unit fixtures never run worldgen).
    // Falling back matters more than speed here: silently reading 0 would make
    // every jump free again, which is precisely the bug being fixed.
    public static int StepDelta(WorldState world, Junction from, int neighborIndex, JunctionId toId)
    {
        var baked = from.NeighborStepDelta;
        return baked is not null && neighborIndex < baked.Length
            ? baked[neighborIndex]
            : ResolveStepDelta(world, from.Id, toId);
    }

    // Spec §50: does crossing from `fromId` to `toId` need a jump? A survivor
    // who can't jump (a lost leg) must not route across an elevation edge, so
    // that terrain is off-limits to her.
    public static bool RequiresJump(WorldState world, JunctionId fromId, JunctionId toId)
    {
        return ResolveStepDelta(world, fromId, toId) != 0;
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
        // PriorityQueue, it compiles under Unity's netstandard2.1. ClimbCost
        // applies real per-edge weights (climb up 4.5x, down 2.5x, strait 2x,
        // swim 4x — NOT uniform), so ordering is genuine cheapest-first.
        // §40.17 v2: with RELAXATION. The old loop closed a neighbour the first
        // time it was reached (`cameFrom.ContainsKey`) and never improved it, so
        // with a real cost spread a node first found through an expensive edge
        // kept that price forever — the search could return a route more
        // expensive than one it had the data to find. Frontier holds at most one
        // entry per node: an improvement removes the stale key before adding the
        // new one, and `closed` keeps a popped node from being reopened.
        const long priorityScale = 100_000_000L;
        var frontier = new SortedDictionary<long, JunctionId>();
        var frontierKey = new Dictionary<JunctionId, long>();
        var closed = new HashSet<JunctionId>();
        var cameFrom = new Dictionary<JunctionId, JunctionId?>();
        var gScore = new Dictionary<JunctionId, long>();
        var seq = 0L;

        frontier.Add(0L, start);
        frontierKey[start] = 0L;
        cameFrom[start] = null;
        gScore[start] = 0L;

        while (frontier.Count > 0)
        {
            var head = default(KeyValuePair<long, JunctionId>);
            foreach (var kv in frontier) { head = kv; break; } // lowest priority
            frontier.Remove(head.Key);
            var current = head.Value;
            frontierKey.Remove(current);
            closed.Add(current);
            if (current.Equals(goal))
            {
                break;
            }

            if (!world.Junctions.Items.TryGetValue(current, out var junction))
            {
                continue;
            }

            // Indexed, not foreach: the baked per-edge elevation delta lives in
            // a list parallel to Neighbors. Same order, same determinism.
            for (var n = 0; n < junction.Neighbors.Count; n++)
            {
                var neighborId = junction.Neighbors[n];
                if (closed.Contains(neighborId))
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

                var stepDelta = StepDelta(world, junction, n, neighborId);

                // Spec §50 + §57.11: a survivor who can't jump can't CLIMB an
                // elevation step — but she can lower herself DOWN one («вверх
                // нельзя, а спрыгнуть-то можно»). Water stays barred in both
                // directions: a controlled slide ends on land, never in a dive.
                if (!canJump && (stepDelta > 0 ||
                    (stepDelta < 0 && (world.SwimJunctions.Contains(neighborId) ||
                                       world.StraitJunctions.Contains(neighborId)))))
                {
                    continue;
                }

                var cost = gScore[current] +
                    ClimbCost(world, neighborId, stepDelta, weightClimb);
                if (danger is not null && danger.Contains(neighborId))
                {
                    cost += dangerCost;
                }

                if (gScore.TryGetValue(neighborId, out var known) && cost >= known)
                {
                    continue; // no improvement — keep the cheaper parent
                }

                gScore[neighborId] = cost;
                cameFrom[neighborId] = current;
                var priority = cost * priorityScale + seq++;
                if (frontierKey.TryGetValue(neighborId, out var stalePriority))
                {
                    frontier.Remove(stalePriority);
                }

                frontierKey[neighborId] = priority;
                frontier.Add(priority, neighborId);
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

    /// <summary>
    /// §123: one weighted search from an actor to every formation candidate.
    /// Costs use the same edge rules as FindPath; missing goals receive the
    /// same housemate-avoid fallback instead of one Dijkstra per slot.
    /// </summary>
    public static Dictionary<JunctionId, long> FindCosts(
        WorldState world, JunctionId start, IReadOnlyCollection<JunctionId> goals,
        HashSet<JunctionId> avoid, bool weightClimb = true, bool canJump = true,
        HashSet<JunctionId> danger = null, long dangerCost = 0L,
        HashSet<JunctionId> hardAvoid = null)
    {
        var result = FindCostsCore(
            world, start, goals, avoid, weightClimb, canJump, danger, dangerCost, hardAvoid);
        if (avoid is null || result.Count >= goals.Count) return result;

        var fallback = FindCostsCore(
            world, start, goals, null, weightClimb, canJump, danger, dangerCost, hardAvoid);
        foreach (var pair in fallback)
        {
            if (!result.ContainsKey(pair.Key)) result[pair.Key] = pair.Value;
        }
        return result;
    }

    private static Dictionary<JunctionId, long> FindCostsCore(
        WorldState world, JunctionId start, IReadOnlyCollection<JunctionId> goals,
        HashSet<JunctionId> avoid, bool weightClimb, bool canJump,
        HashSet<JunctionId> danger, long dangerCost, HashSet<JunctionId> hardAvoid)
    {
        var result = new Dictionary<JunctionId, long>();
        if (goals is null || goals.Count == 0) return result;
        var goalSet = goals as HashSet<JunctionId> ?? new HashSet<JunctionId>(goals);

        const long priorityScale = 100_000_000L;
        var frontier = new SortedDictionary<long, JunctionId>();
        var frontierKey = new Dictionary<JunctionId, long>();
        var closed = new HashSet<JunctionId>();
        var score = new Dictionary<JunctionId, long> { [start] = 0L };
        var seq = 0L;
        frontier.Add(0L, start);
        frontierKey[start] = 0L;

        while (frontier.Count > 0 && result.Count < goalSet.Count)
        {
            var head = default(KeyValuePair<long, JunctionId>);
            foreach (var pair in frontier) { head = pair; break; }
            frontier.Remove(head.Key);
            frontierKey.Remove(head.Value);
            var current = head.Value;
            if (!closed.Add(current)) continue;

            if (goalSet.Contains(current)) result[current] = score[current];
            if (!world.Junctions.Items.TryGetValue(current, out var junction)) continue;

            for (var n = 0; n < junction.Neighbors.Count; n++)
            {
                var neighborId = junction.Neighbors[n];
                if (closed.Contains(neighborId) ||
                    !world.Junctions.Items.TryGetValue(neighborId, out var neighbor)) continue;

                var isGoal = goalSet.Contains(neighborId);
                if (neighbor.Blocked && !isGoal) continue;
                if (IsClimbSeamWalk(world, current, neighborId)) continue;
                if (avoid is not null && avoid.Contains(neighborId) && !isGoal) continue;
                if (hardAvoid is not null && hardAvoid.Contains(neighborId) && !isGoal) continue;

                var stepDelta = StepDelta(world, junction, n, neighborId);
                // §57.11: то же правило, что в FindPath — вниз можно, вверх и
                // в воду нельзя. Обе выборки обязаны совпадать, иначе мультицель
                // и путь разойдутся в достижимости.
                if (!canJump && (stepDelta > 0 ||
                    (stepDelta < 0 && (world.SwimJunctions.Contains(neighborId) ||
                                       world.StraitJunctions.Contains(neighborId))))) continue;
                var next = score[current] + ClimbCost(
                    world, neighborId, stepDelta, weightClimb);
                if (danger is not null && danger.Contains(neighborId)) next += dangerCost;
                if (score.TryGetValue(neighborId, out var known) && next >= known) continue;

                score[neighborId] = next;
                if (frontierKey.TryGetValue(neighborId, out var stale)) frontier.Remove(stale);
                var priority = next * priorityScale + seq++;
                frontierKey[neighborId] = priority;
                frontier.Add(priority, neighborId);
            }
        }

        return result;
    }

    // Spec 40.17: a flat step costs FlatCost, a crossing costs more, so a
    // comfortable NPC prefers the flat way. Costs are ×10 so a fractional
    // multiplier stays integer. The weight is LIVE, and gated: hungry/thirsty
    // NPCs and anyone fleeing pass weightClimb=false and ignore it, so survival
    // routes stay short (that exemption is half of what made it shippable — see
    // §40.17; the other half was the dog margin).
    // Balance here is knife-edge by construction: every price change reshuffles
    // the deterministic dog dance, so re-soak ALL seeds before touching these,
    // and expect which-seed-loses-whom to move even when the totals hold.
    private const long FlatCost = 10L;

    // §40.17 v2: priced from the TIME a hop actually costs, not guessed.
    // §21.21B v23: одно окно на оба направления. По оттюненному ассету
    // HopSeconds 1.81 с ≈ 7.3 тика против ~2 тиков на плоское ребро решётки,
    // то есть КЛИМБ стоит ~3.6 плоских ребра:
    //     SeamUpCost = FlatCost * (7.3 / 2) ≈ 36
    // Do NOT push these higher "to be safe": walking around one tile is ~6.9
    // edges ≈ 14 ticks, so past ~4.5x she starts taking detours that are slower
    // in real time than the jump she avoided. Re-derive if HopSeconds is
    // retuned (§21.21B) — это одно и то же число в разных единицах.
    //
    // ⭐ СПУСК ДЕШЕВЛЕ ПОДЪЁМА, И ЭТО УЖЕ НЕ ПРО ВРЕМЯ. До v23 у спрыгивания
    // было своё, вдвое более короткое окно, и разница цен просто повторяла
    // разницу секунд. v23 сделал окно ОДНИМ — и цены на секунду сравнялись,
    // уронив гейт `DroppingIsCheaperThanClimbing`. Гейт прав: выбирая между
    // «вскарабкаться на ступень» и «спрыгнуть с неё», человек спрыгивает —
    // спуску не нужен ни разбег, ни подъём собственного веса. Это отдельное
    // предпочтение маршрутизатора, а не хронометраж, поэтому оно и живёт
    // теперь отдельным множителем (2/3), а не выводится из окна.
    private const long SeamUpCost = 36L;
    private const long SeamDownCost = 24L;

    // Spec 40.18: entering the swim ring costs 4x a land step — a slow, risky
    // last resort, so a route only takes to the water when there's no dry way.
    private const long SwimCost = 40L;

    // Spec 40.18 step 4: the strait to the second island is a cheap swim (2x),
    // so a foraging NPC will actually make the hop for an island-exclusive
    // resource. The wider ring stays SwimCost (4x), a shark-risked last resort.
    private const long StraitCost = 20L;

    // stepDelta is the signed elevation change of THIS edge (see StepDelta):
    // that is what makes the weight a property of the crossing rather than of
    // the seam junction, so hugging a wall no longer costs what jumping it does.
    // Water keeps priority over the climb weight, as before — entering the swim
    // ring is priced as swimming, not double-charged as a drop into it.
    private static long ClimbCost(WorldState world, JunctionId to, int stepDelta, bool weightClimb)
    {
        if (world.StraitJunctions.Contains(to))
        {
            return StraitCost;
        }

        if (world.SwimJunctions.Contains(to))
        {
            return SwimCost;
        }

        if (!weightClimb || stepDelta == 0)
        {
            return FlatCost;
        }

        return stepDelta > 0 ? SeamUpCost : SeamDownCost;
    }

    private static bool IsClimbSeamWalk(WorldState world, JunctionId from, JunctionId to)
    {
        return world.ClimbSeams.Contains(from) && world.ClimbSeams.Contains(to);
    }
}

}
