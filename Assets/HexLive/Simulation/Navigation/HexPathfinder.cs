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

    /// <summary>
    /// §40.18-C (баг #162): шаг «из воды на сушу». Выбраться на берег — не
    /// прыжок, а карабканье, и оно доступно всем, пока человек в сознании.
    /// <para>
    /// Без этого исключения вода была ловушкой в одну сторону: замер по живому
    /// сейву (seed −28147312) показал, что из ВСЕХ 1265 глубоких узлов и из 434
    /// из 474 мелководных не существует ни одного маршрута на сушу с
    /// <c>canJump=false</c> — берег везде на ступень выше. Раненая в ноги
    /// колонистка, оказавшись в воде, теряла весь мир: планы падали один за
    /// другим, и она умирала там, где стояла.
    /// </para>
    /// Правило направленное: ВОЙТИ в воду по этому исключению нельзя, только
    /// выйти. «Из воды» — узел, у которого есть водяной тайл; «на сушу» — узел
    /// без глубокой воды, у которого есть ходибельный сухой тайл (берег часто
    /// смешанный, и требовать полностью сухой узел значило бы оставить ловушку).
    /// </summary>
    public static bool IsWaterExit(WorldState world, JunctionId fromId, JunctionId toId)
    {
        if (!world.Junctions.Items.TryGetValue(fromId, out var from) ||
            !world.Junctions.Items.TryGetValue(toId, out var to))
        {
            return false;
        }

        var fromInWater = false;
        foreach (var coord in from.Tiles)
        {
            if (world.Tiles.Items.TryGetValue(coord, out var tile) &&
                tile.Flags.HasFlag(TileFlags.Water))
            {
                fromInWater = true;
                break;
            }
        }

        if (!fromInWater || world.SwimJunctions.Contains(toId) ||
            world.StraitJunctions.Contains(toId))
        {
            return false;
        }

        foreach (var coord in to.Tiles)
        {
            if (world.Tiles.Items.TryGetValue(coord, out var tile) &&
                tile.Flags.HasFlag(TileFlags.Walkable) &&
                !tile.Flags.HasFlag(TileFlags.Water))
            {
                return true;
            }
        }

        return false;
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
        HashSet<JunctionId> hardAvoid = null,
        int maxExpansions = 0)
    {
        if (start.Equals(goal))
        {
            return new List<JunctionId> { start };
        }

        // Spec 40.17: weighted shortest path. Frontier ordered by
        // (priority, seq): the seq term makes every priority unique, so the
        // queue is stable and the search is reproducible tick for tick.
        // ClimbCost applies real per-edge weights (climb up 4.5x, down 2.5x,
        // strait 2x, swim 4x — NOT uniform), so ordering is genuine
        // cheapest-first.
        // §40.17 v2: with RELAXATION. The old loop closed a neighbour the first
        // time it was reached (`cameFrom.ContainsKey`) and never improved it, so
        // with a real cost spread a node first found through an expensive edge
        // kept that price forever — the search could return a route more
        // expensive than one it had the data to find. `closed` keeps a popped
        // node from being reopened.
        //
        // §135.5 (perf): the frontier is a POOLED BINARY HEAP, not a
        // SortedDictionary. The old queue allocated a red-black node per push
        // and walked the tree with a fresh enumerator per pop (`foreach … break`
        // to read the minimum) — five collections' worth of garbage on every
        // single call, and calls are per chasing mob per medium tick. Pop order
        // is unchanged because the key still carries the unique `seq`; an
        // improved node is pushed again and its stale entry is skipped on pop
        // (it is already closed), which is exactly what the old explicit
        // Remove did.
        //
        // §135.5: and the priority is now g + h, i.e. A*, not plain Dijkstra.
        // The heuristic is `floor(distance / longest edge) × FlatCost` — the
        // cheapest conceivable remainder, so it is admissible AND consistent
        // (each edge is at most one "longest edge" long and costs at least
        // FlatCost), which is what makes closing a node on pop still optimal.
        // Routes cost the same as before; among EQUAL-cost routes a different
        // one can now win, and that shows up as a golden-trace diff.
        const long priorityScale = 100_000_000L;
        var scratch = SearchScratch.Rent();
        try
        {
            var closed = scratch.Closed;
            var cameFrom = scratch.CameFrom;
            var gScore = scratch.GScore;
            var seq = 0L;
            var expansions = 0;
            var budgetHit = false;
            var goalPosition = world.Junctions.Items.TryGetValue(goal, out var goalJunction)
                ? goalJunction.WorldPosition
                : default;
            var heuristicScale = goalJunction is null ? 0f : 1f / LongestEdge(world);

            scratch.Push(0L, start);
            cameFrom[start] = null;
            gScore[start] = 0L;

        while (scratch.TryPop(out var current))
        {
            if (closed.Contains(current))
            {
                continue; // устаревшая запись улучшенного узла — он уже закрыт
            }

            closed.Add(current);
            if (current.Equals(goal))
            {
                break;
            }

            if (maxExpansions > 0 && ++expansions > maxExpansions)
            {
                // §135.5: бюджет узлов. Недостижимая цель разворачивала ВЕСЬ
                // граф (~14 000 узлов) на каждом вызове — и звала вызывающего
                // повторить это следующим тиком. Упёрлись в потолок — честно
                // отвечаем «дороги нет»: ровно то, что делает §29C.3, только
                // за миллисекунды.
                budgetHit = true;
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
                // §40.18-C: и ровно одно исключение — ВЫХОД ИЗ ВОДЫ на сушу.
                if (!canJump && !IsWaterExit(world, current, neighborId) &&
                    (stepDelta > 0 ||
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
                // A* (§135.5): очередь ведёт ОЦЕНКА полного пути g + h. При
                // h = 0 это ровно прежняя равноценная Дейкстра — свойство,
                // на которое опирается запасной путь без цели-узла.
                var estimate = cost + Heuristic(neighbor.WorldPosition, goalPosition, heuristicScale);
                scratch.Push(estimate * priorityScale + seq++, neighborId);
            }
        }

        if (budgetHit || !cameFrom.ContainsKey(goal))
        {
            // Fully enclosed by standing housemates: take the direct path.
            // hardAvoid stays — terrain bans are walls, not courtesies.
            // Бюджет узлов повторной попытки НЕ получает: она стоила бы ровно
            // столько же и упёрлась бы в тот же потолок.
            return !budgetHit && avoid is not null
                ? FindPath(world, start, goal, null, weightClimb, canJump, danger, dangerCost,
                    hardAvoid, maxExpansions)
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
        finally
        {
            scratch.Return();
        }
    }

    // §135.5: нижняя оценка остатка пути в тех же единицах, что gScore.
    // `scale` = 1 / (длина самого длинного ребра графа), поэтому
    // floor(расстояние × scale) — это заведомо не больше, чем шагов осталось,
    // а каждый шаг стоит не меньше FlatCost. Отсюда и допустимость (оценка не
    // завышена → маршрут остаётся оптимальным), и согласованность (соседи
    // отличаются не больше чем на один шаг → закрывать узел при извлечении
    // по-прежнему безопасно).
    private static long Heuristic(Float2 from, Float2 goal, float scale)
    {
        if (scale <= 0f)
        {
            return 0L;
        }

        var steps = (long)(HexSpatialMath.Distance(from, goal) * scale);
        return steps * FlatCost;
    }

    // Самое длинное ребро графа. Позиции джанкшенов — вывод worldgen и после
    // него не меняются (Blocked двигает проходимость, не геометрию), поэтому
    // считается один раз на мир и живёт в кэшах.
    private static float LongestEdge(WorldState world)
    {
        if (world.Caches.LongestJunctionEdge > 0f)
        {
            return world.Caches.LongestJunctionEdge;
        }

        var longest = 0f;
        foreach (var junction in world.Junctions.Items.Values)
        {
            for (var i = 0; i < junction.Neighbors.Count; i++)
            {
                if (!world.Junctions.Items.TryGetValue(junction.Neighbors[i], out var neighbor))
                {
                    continue;
                }

                var length = HexSpatialMath.Distance(junction.WorldPosition, neighbor.WorldPosition);
                if (length > longest)
                {
                    longest = length;
                }
            }
        }

        // Пустой/вырожденный граф: эвристика выключается, поиск остаётся
        // прежней Дейкстрой. Лучше медленно, чем неверно.
        world.Caches.LongestJunctionEdge = longest > 0f ? longest : -1f;
        return world.Caches.LongestJunctionEdge;
    }

    /// <summary>
    /// §135.5: фронтир поиска — двоичная куча на переиспользуемых массивах.
    /// <para>
    /// ⭐ Порядок извлечения ТОТ ЖЕ, что у прежнего <c>SortedDictionary</c>:
    /// ключ несёт уникальный <c>seq</c>, так что равных ключей не бывает, а
    /// улучшенный узел просто кладётся второй раз — устаревшая запись
    /// отбрасывается при извлечении, потому что узел уже закрыт. Это ровно то,
    /// что делал явный <c>Remove</c>, только без обхода дерева.
    /// </para>
    /// <para>
    /// Буферы висят на потоке (<c>[ThreadStatic]</c>): симуляция крутится на
    /// своём воркере, а редактор/сервер могут звать поиск из другого. Вложенный
    /// вызов (запасной проход без <c>avoid</c>) берёт собственный экземпляр —
    /// иначе он затёр бы данные внешнего поиска.
    /// </para>
    /// </summary>
    private sealed class SearchScratch
    {
        [System.ThreadStatic]
        private static SearchScratch _pooled;

        private long[] _keys = new long[256];
        private JunctionId[] _values = new JunctionId[256];
        private int _count;
        private bool _rented;

        public HashSet<JunctionId> Closed { get; } = new();

        public Dictionary<JunctionId, JunctionId?> CameFrom { get; } = new();

        public Dictionary<JunctionId, long> GScore { get; } = new();

        public static SearchScratch Rent()
        {
            var pooled = _pooled;
            if (pooled is null)
            {
                _pooled = pooled = new SearchScratch();
            }
            else if (pooled._rented)
            {
                pooled = new SearchScratch(); // вложенный поиск — свой буфер
            }

            pooled._rented = true;
            pooled._count = 0;
            pooled.Closed.Clear();
            pooled.CameFrom.Clear();
            pooled.GScore.Clear();
            return pooled;
        }

        public void Return()
        {
            _rented = false;
            // Содержимое не чистим здесь: Rent сделает это перед следующим
            // поиском, а держать ссылки до тех пор дешевле, чем чистить дважды.
        }

        public void Push(long key, JunctionId value)
        {
            if (_count == _keys.Length)
            {
                System.Array.Resize(ref _keys, _count * 2);
                System.Array.Resize(ref _values, _count * 2);
            }

            var child = _count++;
            while (child > 0)
            {
                var parent = (child - 1) / 2;
                if (_keys[parent] <= key)
                {
                    break;
                }

                _keys[child] = _keys[parent];
                _values[child] = _values[parent];
                child = parent;
            }

            _keys[child] = key;
            _values[child] = value;
        }

        public bool TryPop(out JunctionId value)
        {
            if (_count == 0)
            {
                value = default;
                return false;
            }

            value = _values[0];
            var lastKey = _keys[--_count];
            var lastValue = _values[_count];
            if (_count == 0)
            {
                return true;
            }

            var parent = 0;
            while (true)
            {
                var left = parent * 2 + 1;
                if (left >= _count)
                {
                    break;
                }

                var right = left + 1;
                var best = right < _count && _keys[right] < _keys[left] ? right : left;
                if (_keys[best] >= lastKey)
                {
                    break;
                }

                _keys[parent] = _keys[best];
                _values[parent] = _values[best];
                parent = best;
            }

            _keys[parent] = lastKey;
            _values[parent] = lastValue;
            return true;
        }
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
                // §40.18-C: из воды на сушу — всегда (см. IsWaterExit).
                if (!canJump && !IsWaterExit(world, current, neighborId) &&
                    (stepDelta > 0 ||
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
    // resource. The wider ring stays SwimCost (4x), a slow last resort.
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
