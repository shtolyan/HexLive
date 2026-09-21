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

internal static class Connectivity
{
    // Spec 31C.7: "can I stand next to it" — blocked/water anchors are
    // reachable through any passable dry neighbor (solid furniture and
    // river-water drink spots must stay visible to planning).
    //
    // ⭐ §26.6A r5: this is the ROUTE question, and it must STAY the route
    // question. The §26.6A table keeps "does a way exist" apart from "am I
    // close enough" on purpose, and the first attempt at r5 quietly merged them
    // here — a third body between the anchor and the world started reading as
    // unreachable, objects fell out of perception with no PlanFailed to show
    // for it, and 30 seeds × 10 days went 86/120 → 72/120 alive with two wipes.
    // A body is walked AROUND. Refusing to reach THROUGH one is the start
    // gate's job, and only its job.
    public static bool ReachableBeside(
        WorldState world, JunctionId from, JunctionId anchor, bool canJump = true,
        WorldObjectState owner = null)
    {
        if (CraftProjectMath.TryGetSupport(world, owner, out var station))
            return Reachable(world, from, station.CraftJunction.Value, canJump);
        var anchorBlocked = !world.Junctions.Items.TryGetValue(anchor, out var junction) ||
            junction.Blocked || SpatialQueries.IsAllWaterJunction(world, anchor);
        if (!anchorBlocked)
        {
            return Reachable(world, from, anchor, canJump);
        }

        SpatialQueries.CollectStandableAround(world, anchor, _besideScratch, 96, float.MaxValue, owner,
            SpatialQueries.RimPurpose.Route);
        foreach (var rim in _besideScratch)
        {
            if (Reachable(world, from, rim, canJump))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly System.Collections.Generic.List<JunctionId> _besideScratch = new();

    public static bool Reachable(WorldState world, JunctionId a, JunctionId b, bool canJump = true)
    {
        // Bug #338 r2: старт на «замурованном» шве — скольжение вдоль цепочки:
        // цель в той же цепочке достижима сразу, иначе старт считается от
        // выхода цепочки. Графы при этом остаются строгими.
        if (Navigation.HexPathfinder.IsStrandedSeam(world, a))
        {
            var resolved = ResolveStrandedStartOrTarget(world, a, b, out var hitTarget);
            if (hitTarget)
            {
                return true;
            }

            a = resolved;
        }

        // Spec §50 + §57.11: a survivor who can't jump reads the graph without
        // CLIMB edges, but descents count (directed): a lower shelf is
        // reachable, the way back is not. Water stays its own world.
        if (!canJump)
        {
            EnsureFlat(world);

            return world.JunctionComponentsFlat.TryGetValue(a, out var fa) && fa >= 0 &&
                   world.JunctionComponentsFlat.TryGetValue(b, out var fb) && fb >= 0 &&
                   FlatReaches(world, fa, fb);
        }

        EnsureJump(world);

        return world.JunctionComponents.TryGetValue(a, out var ca) && ca >= 0 &&
               world.JunctionComponents.TryGetValue(b, out var cb) &&
               ca == cb;
    }

    // §22.7: идентификатор компоненты связности узла в нужном варианте графа.
    // Reachable(a,b) — это ровно сравнение этих идентификаторов, поэтому кэш,
    // ключёванный компонентой «откуда», меняет ответы только вместе с ними.
    // Лениво перестраивает словарь так же, как Reachable.
    /// <summary>Bug #338 r2: для «замурованного» шва — первый узел его шовной
    /// цепочки, имеющий свободный не-шовный выход; иначе сам узел. Кэш
    /// замурованных швов держит патфайндер (по TopologyVersion).</summary>
    public static JunctionId ResolveStrandedStart(WorldState world, JunctionId start)
        => ResolveStrandedStartOrTarget(world, start, null, out _);

    private static JunctionId ResolveStrandedStartOrTarget(
        WorldState world, JunctionId start, JunctionId? target, out bool hitTarget)
    {
        hitTarget = false;
        if (!Navigation.HexPathfinder.IsStrandedSeam(world, start))
        {
            return start;
        }

        _strandedScratch.Clear();
        _strandedQueue.Clear();
        _strandedScratch.Add(start);
        _strandedQueue.Enqueue(start);
        while (_strandedQueue.Count > 0 && _strandedScratch.Count <= 64)
        {
            var current = _strandedQueue.Dequeue();
            if (!world.Junctions.Items.TryGetValue(current, out var junction))
            {
                continue;
            }

            foreach (var neighborId in junction.Neighbors)
            {
                if (target.HasValue && neighborId.Equals(target.Value))
                {
                    hitTarget = true; // цель в самой шовной цепочке
                    return start;
                }

                if (!world.ClimbSeams.Contains(neighborId) ||
                    !_strandedScratch.Add(neighborId))
                {
                    continue;
                }

                if (!Navigation.HexPathfinder.IsStrandedSeam(world, neighborId))
                {
                    return neighborId; // здоровый шов с боковым выходом
                }

                _strandedQueue.Enqueue(neighborId);
            }
        }

        return start;
    }

    private static readonly System.Collections.Generic.HashSet<JunctionId> _strandedScratch = new();
    private static readonly System.Collections.Generic.Queue<JunctionId> _strandedQueue = new();

    public static int ComponentOf(WorldState world, JunctionId junction, bool canJump)
    {
        // Bug #334/#338 r2: «замурованный» шов — синглтон в графах, но живой
        // роутер даёт стоящей НА нём скользить вдоль кромки до выхода. Чтобы
        // планирование не считало её мир пустым, компонент такого узла — это
        // компонент первого выхода его шовной цепочки. Старт-относительно и
        // без новых рёбер в графах.
        var resolved = ResolveStrandedStart(world, junction);
        if (!resolved.Equals(junction))
        {
            junction = resolved;
        }

        if (!canJump)
        {
            EnsureFlat(world);

            return world.JunctionComponentsFlat.TryGetValue(junction, out var flat)
                ? flat
                : int.MinValue + 1;
        }

        EnsureJump(world);

        return world.JunctionComponents.TryGetValue(junction, out var component)
            ? component
            : int.MinValue + 1;
    }

    /// <summary>Сколько узлов в ПЛОСКОЙ компоненте этого узла (0 — узел
    /// заблокирован/неизвестен). Ленивая перестройка как у Reachable.</summary>
    public static int FlatComponentSizeAt(WorldState world, JunctionId junction)
    {
        EnsureFlat(world);

        return world.JunctionComponentsFlat.TryGetValue(junction, out var comp) && comp > 0 &&
            world.JunctionComponentsFlatSizes.TryGetValue(comp, out var size)
                ? size
                : 0;
    }

    // ── §158.3: инкрементальная связность ─────────────────────────────────
    //
    // Раньше любая смена TopologyVersion (брошенное бревно, срубленная пальма,
    // колышек стройки) роняла обе карты компонент целиком, и первый, кто
    // спрашивал достижимость, платил BFS по ВСЕМУ графу: 0.5 с прыжковый +
    // 1.8 с плоский на 2.5 млн узлов «Островов» — по 2–3 с тишины сервера на
    // каждое действие игрока (§158.1). Теперь потребитель догоняет журнал
    // (§158.2) точечно: заблокированный узел проверяется на раскол ЛОКАЛЬНЫМ
    // BFS с бюджетом, разблокированный — сливает компоненты соседей
    // перекраской меньшей. Полная перестройка осталась запасным ходом ровно
    // для одного случая: узел рассёк компоненту на две части, каждая больше
    // бюджета (стена поперёк острова), — и каждый такой случай считается
    // (RuntimeCaches.ConnectivityFullRebuilds) и пишется в самописец.
    //
    // Идентификаторы компонент при этом НЕ равны тем, что дала бы свежая
    // перестройка (та нумерует по порядку обхода), но они нигде не сравниваются
    // ни с чем, кроме друг друга: ответы Reachable/ComponentOf/размеры совпадают
    // с ответами перестроенной карты бит-в-бит, что и проверяет гейт
    // ConnectivityIncrementalParityTests на случайных последовательностях.

    /// <summary>Бюджет локального BFS при блокировке узла: столько узлов старой
    /// компоненты просматривается, прежде чем область признаётся «большой».
    /// Интерьеры хижин (десятки узлов) и любые загоны меньше бюджета
    /// раскалываются точно и локально; две «большие» половины — полная
    /// перестройка.</summary>
    internal const int SplitProbeBudget = 4096;

    private static readonly System.Collections.Generic.Queue<JunctionId> _queue = new();
    private static readonly System.Collections.Generic.List<JunctionId> _changedScratch = new();
    private static readonly System.Collections.Generic.List<JunctionId> _portsScratch = new();
    private static readonly System.Collections.Generic.HashSet<JunctionId> _pendingScratch = new();
    private static readonly System.Collections.Generic.List<int> _labelsScratch = new();

    private static System.Collections.Generic.Dictionary<JunctionId, int> Labels(WorldState world, bool flat) =>
        flat ? world.JunctionComponentsFlat : world.JunctionComponents;

    private static System.Collections.Generic.Dictionary<int, int> Sizes(WorldState world, bool flat) =>
        flat ? world.JunctionComponentsFlatSizes : world.JunctionComponentSizes;

    /// <summary>Ребро графа компонент между двумя ПРОХОДИМЫМИ узлами: ровно то
    /// правило, по которому Rebuild/RebuildFlat кладут соседа в очередь.
    /// Симметрично (перепад высот и швы не зависят от направления).</summary>
    private static bool EdgeAllowed(WorldState world, JunctionId from, JunctionId to, bool flat)
    {
        if (world.ClimbSeams.Contains(from) && world.ClimbSeams.Contains(to))
        {
            return false;
        }

        return !flat || !Navigation.HexPathfinder.RequiresJump(world, from, to);
    }

    /// <summary>Догон прыжкового графа до текущей версии топологии.</summary>
    internal static void EnsureJump(WorldState world)
    {
        if (world.ComponentsBuiltVersion == world.TopologyVersion)
        {
            return;
        }

        var version = world.ComponentsBuiltVersion;
        var cursor = world.ComponentsJournalCursor;
        var full = WorldTopology.CatchUp(world, ref version, ref cursor, _changedScratch);
        world.ComponentsBuiltVersion = version;
        world.ComponentsJournalCursor = cursor;
        if (full)
        {
            Rebuild(world);
            return;
        }

        ApplyChanges(world, flat: false);
    }

    /// <summary>Догон плоского графа до текущей версии топологии.</summary>
    internal static void EnsureFlat(WorldState world)
    {
        if (world.ComponentsFlatBuiltVersion == world.TopologyVersion)
        {
            return;
        }

        var version = world.ComponentsFlatBuiltVersion;
        var cursor = world.ComponentsFlatJournalCursor;
        var full = WorldTopology.CatchUp(world, ref version, ref cursor, _changedScratch);
        world.ComponentsFlatBuiltVersion = version;
        world.ComponentsFlatJournalCursor = cursor;
        if (full)
        {
            RebuildFlat(world);
            return;
        }

        ApplyChanges(world, flat: true);
    }

    private static void ApplyChanges(WorldState world, bool flat)
    {
        var labels = Labels(world, flat);
        var touched = false;
        for (var i = 0; i < _changedScratch.Count; i++)
        {
            var id = _changedScratch[i];
            if (!world.Junctions.Items.TryGetValue(id, out var junction))
            {
                continue;
            }

            if (!labels.TryGetValue(id, out var label))
            {
                // Узел, которого карта не знает, — граф менялся мимо worldgen.
                FullRebuild(world, flat, "UnknownJunction");
                return;
            }

            var wasBlocked = label < 0;
            if (wasBlocked == junction.Blocked)
            {
                continue; // менялась дверь либо блок успел вернуться — карта верна
            }

            touched = true;
            if (junction.Blocked)
            {
                if (!Block(world, junction, label, flat))
                {
                    FullRebuild(world, flat, "AmbiguousSplit");
                    return;
                }
            }
            else
            {
                Unblock(world, junction, flat);
            }
        }

        if (touched && flat && _edgeSetChanged)
        {
            // Замыкания спусков зависят только от МНОЖЕСТВА рёбер между
            // компонентами; счётчик, изменившийся с 3 на 2, их не трогает.
            // Бревно посреди поляны поэтому не заставляет пересчитывать
            // замыкание материка (сотни мс на 97 тысячах компонент «Островов»).
            world.FlatDescendClosure.Clear();
        }

        _edgeSetChanged = false;
    }

    /// <summary>Появилось или исчезло ребро графа компонент за текущий догон.</summary>
    private static bool _edgeSetChanged;

    private static void FullRebuild(WorldState world, bool flat, string reason)
    {
        world.Caches.ConnectivityFullRebuilds++;
        if (SimTrace.Enabled)
        {
            Trace.DebugSystem(world, "ConnectivityFullRebuild",
                $"Graph={(flat ? "Flat" : "Jump")} Reason={reason}");
        }

        if (flat)
        {
            RebuildFlat(world);
        }
        else
        {
            Rebuild(world);
        }
    }

    // ── блокировка узла: раскол ──────────────────────────────────────────

    /// <summary>Узел стал непроходимым. Возвращает false, если раскол не
    /// удалось разрешить локально (две области больше бюджета).</summary>
    private static bool Block(WorldState world, Junction junction, int label, bool flat)
    {
        var labels = Labels(world, flat);
        if (flat)
        {
            NodeDescendEdges(world, junction, label, add: false);
        }

        labels[junction.Id] = -1;
        AddSize(world, flat, label, -1);

        // Порты: проходимые соседи той же компоненты, связанные с узлом ребром
        // графа. Все прочие узлы компоненты достижимы из какого-то порта, так
        // что компоненты «C минус узел» — это ровно области портов.
        var ports = _portsScratch;
        ports.Clear();
        for (var i = 0; i < junction.Neighbors.Count; i++)
        {
            var neighborId = junction.Neighbors[i];
            if (labels.TryGetValue(neighborId, out var neighborLabel) && neighborLabel == label &&
                EdgeAllowed(world, junction.Id, neighborId, flat))
            {
                ports.Add(neighborId);
            }
        }

        if (ports.Count <= 1)
        {
            return true;
        }

        var pending = _pendingScratch;
        pending.Clear();
        foreach (var port in ports)
        {
            pending.Add(port);
        }

        System.Collections.Generic.HashSet<JunctionId> big = null;
        System.Collections.Generic.HashSet<JunctionId> keeper = null; // закрытая область, оставшаяся под старым id
        for (var p = 0; p < ports.Count; p++)
        {
            var port = ports[p];
            if (!pending.Contains(port))
            {
                continue;
            }

            var visited = new System.Collections.Generic.HashSet<JunctionId>();
            var outcome = LocalBfs(world, port, label, flat, visited, pending, big,
                stopWhenPendingEmpty: big is null && keeper is null && p == 0);
            switch (outcome)
            {
                case BfsOutcome.FoundAllPorts:
                    return true; // компонента не раскололась

                case BfsOutcome.TouchedBig:
                    break; // часть большой области, метки верны

                case BfsOutcome.Budget:
                    if (big is not null)
                    {
                        return false; // две большие области — локально не решить
                    }

                    big = visited;
                    if (keeper is not null)
                    {
                        // Старый id обязан остаться у области, чьи члены мы не
                        // знаем; закрытая область, что держала его, получает новый.
                        RelabelRegion(world, keeper, label, NextId(world, flat), flat);
                        keeper = null;
                    }

                    break;

                case BfsOutcome.Exhausted:
                    if (big is null && keeper is null)
                    {
                        keeper = visited; // первая закрытая область держит старый id
                    }
                    else
                    {
                        RelabelRegion(world, visited, label, NextId(world, flat), flat);
                    }

                    break;
            }
        }

        return true;
    }

    private enum BfsOutcome
    {
        FoundAllPorts,
        Exhausted,
        Budget,
        TouchedBig,
    }

    /// <summary>BFS по узлам с меткой <paramref name="label"/> (узел-виновник
    /// уже помечен -1 и в обход не попадает). Порты, встреченные по дороге,
    /// вычёркиваются из <paramref name="pending"/>.</summary>
    private static BfsOutcome LocalBfs(
        WorldState world, JunctionId start, int label, bool flat,
        System.Collections.Generic.HashSet<JunctionId> visited,
        System.Collections.Generic.HashSet<JunctionId> pending,
        System.Collections.Generic.HashSet<JunctionId> big,
        bool stopWhenPendingEmpty)
    {
        var labels = Labels(world, flat);
        _queue.Clear();
        visited.Add(start);
        pending.Remove(start);
        _queue.Enqueue(start);
        if (stopWhenPendingEmpty && pending.Count == 0)
        {
            return BfsOutcome.FoundAllPorts;
        }

        while (_queue.Count > 0)
        {
            var currentId = _queue.Dequeue();
            if (!world.Junctions.Items.TryGetValue(currentId, out var current))
            {
                continue;
            }

            for (var i = 0; i < current.Neighbors.Count; i++)
            {
                var neighborId = current.Neighbors[i];
                if (visited.Contains(neighborId) ||
                    !labels.TryGetValue(neighborId, out var neighborLabel) || neighborLabel != label ||
                    !EdgeAllowed(world, currentId, neighborId, flat))
                {
                    continue;
                }

                if (big is not null && big.Contains(neighborId))
                {
                    foreach (var seen in visited)
                    {
                        big.Add(seen);
                    }

                    return BfsOutcome.TouchedBig;
                }

                visited.Add(neighborId);
                pending.Remove(neighborId);
                if (stopWhenPendingEmpty && pending.Count == 0)
                {
                    return BfsOutcome.FoundAllPorts;
                }

                if (visited.Count >= SplitProbeBudget)
                {
                    return BfsOutcome.Budget;
                }

                _queue.Enqueue(neighborId);
            }
        }

        return BfsOutcome.Exhausted;
    }

    // ── разблокировка узла: слияние ───────────────────────────────────────

    private static void Unblock(WorldState world, Junction junction, bool flat)
    {
        var labels = Labels(world, flat);
        var sizes = Sizes(world, flat);
        var comps = _labelsScratch;
        comps.Clear();
        for (var i = 0; i < junction.Neighbors.Count; i++)
        {
            var neighborId = junction.Neighbors[i];
            if (labels.TryGetValue(neighborId, out var neighborLabel) && neighborLabel >= 0 &&
                EdgeAllowed(world, junction.Id, neighborId, flat) &&
                !comps.Contains(neighborLabel))
            {
                comps.Add(neighborLabel);
            }
        }

        int target;
        if (comps.Count == 0)
        {
            target = NextId(world, flat);
        }
        else
        {
            target = comps[0];
            sizes.TryGetValue(target, out var targetSize);
            for (var c = 1; c < comps.Count; c++)
            {
                sizes.TryGetValue(comps[c], out var size);
                if (size > targetSize)
                {
                    target = comps[c];
                    targetSize = size;
                }
            }

            for (var c = 0; c < comps.Count; c++)
            {
                var other = comps[c];
                if (other == target)
                {
                    continue;
                }

                // Члены сливаемой компоненты — BFS от любого её соседа узла.
                JunctionId seed = default;
                var found = false;
                for (var i = 0; i < junction.Neighbors.Count && !found; i++)
                {
                    if (labels.TryGetValue(junction.Neighbors[i], out var l) && l == other)
                    {
                        seed = junction.Neighbors[i];
                        found = true;
                    }
                }

                var members = new System.Collections.Generic.HashSet<JunctionId>();
                CollectComponent(world, seed, other, flat, members);
                RelabelRegion(world, members, other, target, flat);
            }
        }

        labels[junction.Id] = target;
        AddSize(world, flat, target, +1);
        if (flat)
        {
            NodeDescendEdges(world, junction, target, add: true);
        }
    }

    /// <summary>Все узлы компоненты <paramref name="label"/>, достижимые из
    /// <paramref name="seed"/> — то есть вся компонента целиком.</summary>
    private static void CollectComponent(
        WorldState world, JunctionId seed, int label, bool flat,
        System.Collections.Generic.HashSet<JunctionId> members)
    {
        var labels = Labels(world, flat);
        _queue.Clear();
        members.Add(seed);
        _queue.Enqueue(seed);
        while (_queue.Count > 0)
        {
            var currentId = _queue.Dequeue();
            if (!world.Junctions.Items.TryGetValue(currentId, out var current))
            {
                continue;
            }

            for (var i = 0; i < current.Neighbors.Count; i++)
            {
                var neighborId = current.Neighbors[i];
                if (!members.Contains(neighborId) &&
                    labels.TryGetValue(neighborId, out var neighborLabel) && neighborLabel == label &&
                    EdgeAllowed(world, currentId, neighborId, flat))
                {
                    members.Add(neighborId);
                    _queue.Enqueue(neighborId);
                }
            }
        }
    }

    // ── перекраска области и учёт ─────────────────────────────────────────

    private static int NextId(WorldState world, bool flat)
    {
        if (flat)
        {
            return world.NextFlatComponentId++;
        }

        return world.NextJumpComponentId++;
    }

    /// <summary>Переводит область из компоненты <paramref name="from"/> в
    /// <paramref name="to"/>: метки, размеры и (в плоском графе) рёбра спусков
    /// на границе области.</summary>
    private static void RelabelRegion(
        WorldState world, System.Collections.Generic.HashSet<JunctionId> region,
        int from, int to, bool flat)
    {
        var labels = Labels(world, flat);
        if (flat)
        {
            _edgeSetChanged = true; // компонента сменила имя — замыкания по старым id лгут
        }

        foreach (var id in region)
        {
            labels[id] = to;
        }

        AddSize(world, flat, from, -region.Count);
        AddSize(world, flat, to, region.Count);
        if (!flat)
        {
            return;
        }

        foreach (var id in region)
        {
            if (!world.Junctions.Items.TryGetValue(id, out var junction))
            {
                continue;
            }

            for (var i = 0; i < junction.Neighbors.Count; i++)
            {
                var neighborId = junction.Neighbors[i];
                if (region.Contains(neighborId) ||
                    !labels.TryGetValue(neighborId, out var neighborLabel) || neighborLabel < 0 ||
                    !world.Junctions.Items.TryGetValue(neighborId, out var neighbor))
                {
                    continue;
                }

                if (Descends(world, junction, i, neighbor))
                {
                    if (neighborLabel != from) CountEdge(world, from, neighborLabel, -1);
                    if (neighborLabel != to) CountEdge(world, to, neighborLabel, +1);
                }

                var back = neighbor.Neighbors.IndexOf(id);
                if (back >= 0 && Descends(world, neighbor, back, junction))
                {
                    if (neighborLabel != from) CountEdge(world, neighborLabel, from, -1);
                    if (neighborLabel != to) CountEdge(world, neighborLabel, to, +1);
                }
            }
        }
    }

    private static void AddSize(WorldState world, bool flat, int component, int delta)
    {
        var sizes = Sizes(world, flat);
        sizes.TryGetValue(component, out var size);
        size += delta;
        if (size <= 0)
        {
            sizes.Remove(component);
        }
        else
        {
            sizes[component] = size;
        }

        if (!flat)
        {
            return;
        }

        // Самая большая плоская компонента: подросшая сравнивается с текущей,
        // усохшая — если это была она — пересчитывается по словарю размеров
        // (~100 тысяч записей, миллисекунда; словарь компонент, не узлов).
        if (component == world.LargestFlatComponentId)
        {
            if (delta < 0)
            {
                RescanLargestFlat(world);
            }

            return;
        }

        world.JunctionComponentsFlatSizes.TryGetValue(world.LargestFlatComponentId, out var largest);
        if (size > largest)
        {
            world.LargestFlatComponentId = component;
        }
    }

    private static void RescanLargestFlat(WorldState world)
    {
        var bestId = -1;
        var bestSize = 0;
        foreach (var pair in world.JunctionComponentsFlatSizes)
        {
            if (pair.Value > bestSize || (pair.Value == bestSize && pair.Key < bestId))
            {
                bestSize = pair.Value;
                bestId = pair.Key;
            }
        }

        world.LargestFlatComponentId = bestId;
    }

    // ── рёбра спусков плоского графа ──────────────────────────────────────

    /// <summary>§57.11 / §40.18-C: направленное ребро «спуск» либо «выход из
    /// воды» между двумя ПРОХОДИМЫМИ узлами разных плоских компонент — ровно
    /// правило RebuildFlat, вынесенное в одно место.</summary>
    private static bool Descends(WorldState world, Junction from, int neighborIndex, Junction to)
    {
        if (world.ClimbSeams.Contains(from.Id) && world.ClimbSeams.Contains(to.Id))
        {
            return false;
        }

        var waterExit = Navigation.HexPathfinder.IsWaterExit(world, from.Id, to.Id);
        return waterExit ||
               (!world.SwimJunctions.Contains(to.Id) &&
                !world.StraitJunctions.Contains(to.Id) &&
                Navigation.HexPathfinder.StepDelta(world, from, neighborIndex, to.Id) < 0);
    }

    /// <summary>Рёбра спусков самого узла (в обе стороны) — прибавить при
    /// разблокировке, вычесть перед блокировкой.</summary>
    private static void NodeDescendEdges(WorldState world, Junction junction, int label, bool add)
    {
        var labels = world.JunctionComponentsFlat;
        var delta = add ? +1 : -1;
        for (var i = 0; i < junction.Neighbors.Count; i++)
        {
            var neighborId = junction.Neighbors[i];
            if (!labels.TryGetValue(neighborId, out var neighborLabel) || neighborLabel < 0 ||
                neighborLabel == label ||
                !world.Junctions.Items.TryGetValue(neighborId, out var neighbor))
            {
                continue;
            }

            if (Descends(world, junction, i, neighbor))
            {
                CountEdge(world, label, neighborLabel, delta);
            }

            var back = neighbor.Neighbors.IndexOf(junction.Id);
            if (back >= 0 && Descends(world, neighbor, back, junction))
            {
                CountEdge(world, neighborLabel, label, delta);
            }
        }
    }

    private static void CountEdge(WorldState world, int from, int to, int delta)
    {
        Count(world.FlatDescendEdges, from, to, delta);
        Count(world.FlatDescendEdgesIn, to, from, delta);
    }

    private static void Count(
        System.Collections.Generic.Dictionary<int, System.Collections.Generic.Dictionary<int, int>> index,
        int key, int other, int delta)
    {
        if (!index.TryGetValue(key, out var counts))
        {
            if (delta <= 0)
            {
                return;
            }

            counts = new System.Collections.Generic.Dictionary<int, int>();
            index[key] = counts;
        }

        counts.TryGetValue(other, out var count);
        var had = count > 0;
        count += delta;
        if (count <= 0)
        {
            counts.Remove(other);
            if (counts.Count == 0)
            {
                index.Remove(key);
            }
        }
        else
        {
            counts[other] = count;
        }

        if (had != count > 0)
        {
            _edgeSetChanged = true;
        }
    }

    // ── полные перестройки (запасной ход и первое построение) ────────────

    private static void Rebuild(WorldState world)
    {
        world.JunctionComponents.Clear();
        world.JunctionComponentSizes.Clear();
        foreach (var junction in world.Junctions.Items.Values)
        {
            world.JunctionComponents[junction.Id] = junction.Blocked ? -1 : 0;
        }

        var component = 0;
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || world.JunctionComponents[junction.Id] != 0)
            {
                continue;
            }

            component++;
            var size = 1;
            world.JunctionComponents[junction.Id] = component;
            _queue.Clear();
            _queue.Enqueue(junction.Id);
            while (_queue.Count > 0)
            {
                var currentId = _queue.Dequeue();
                var current = world.Junctions.Items[currentId];
                foreach (var neighborId in current.Neighbors)
                {
                    if (world.JunctionComponents.TryGetValue(neighborId, out var mark) && mark == 0 &&
                        world.Junctions.Items.TryGetValue(neighborId, out var neighbor) && !neighbor.Blocked &&
                        !(world.ClimbSeams.Contains(currentId) &&
                          world.ClimbSeams.Contains(neighborId)))
                    {
                        world.JunctionComponents[neighborId] = component;
                        size++;
                        _queue.Enqueue(neighborId);
                    }
                }
            }

            world.JunctionComponentSizes[component] = size;
        }

        world.NextJumpComponentId = component + 1;
        world.ComponentsBuiltVersion = world.TopologyVersion;
        world.ComponentsJournalCursor = world.Topology.EndIndex;
        if (SimTrace.Enabled)
        {
            Trace.DebugSystem(world, "ConnectivityRebuilt",
                $"Components={component} Junctions={world.Junctions.Items.Count}");
        }
    }

    // Spec §50: the no-jump connectivity graph — identical to Rebuild but an
    // edge is only followed when it stays on one elevation (RequiresJump false),
    // so each elevation shelf (and the water) is its own component. A survivor
    // who lost a leg reads reachability through this map.
    private static void RebuildFlat(WorldState world)
    {
        world.JunctionComponentsFlat.Clear();
        foreach (var junction in world.Junctions.Items.Values)
        {
            world.JunctionComponentsFlat[junction.Id] = junction.Blocked ? -1 : 0;
        }

        var component = 0;
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || world.JunctionComponentsFlat[junction.Id] != 0)
            {
                continue;
            }

            component++;
            world.JunctionComponentsFlat[junction.Id] = component;
            _queue.Clear();
            _queue.Enqueue(junction.Id);
            while (_queue.Count > 0)
            {
                var currentId = _queue.Dequeue();
                var current = world.Junctions.Items[currentId];
                foreach (var neighborId in current.Neighbors)
                {
                    // Ребро «шов→шов» пафйндер запрещает ВСЕМ (ходьба вдоль
                    // кромки обрыва), а этот граф его считал — и ползущая
                    // получала «достижимо» там, куда путь не строится. Замер
                    // (seed 987654, узлы 934→12294): одна плоская компонента,
                    // FindPath(canJump=false)=NULL — и 37 циклов PlanFailed
                    // Goal=GetFood к одному кокосу, пока она голодала. Граф
                    // обязан быть проекцией правил пафйндера, не оптимизмом.
                    if (world.JunctionComponentsFlat.TryGetValue(neighborId, out var mark) && mark == 0 &&
                        world.Junctions.Items.TryGetValue(neighborId, out var neighbor) && !neighbor.Blocked &&
                        !Navigation.HexPathfinder.RequiresJump(world, currentId, neighborId) &&
                        !(world.ClimbSeams.Contains(currentId) && world.ClimbSeams.Contains(neighborId)))
                    {
                        world.JunctionComponentsFlat[neighborId] = component;
                        _queue.Enqueue(neighborId);
                    }
                }
            }
        }

        world.NextFlatComponentId = component + 1;

        // Размеры компонент — для инстинкта «спуститься на большую землю»
        // (крошечный уступ против материка) и любых будущих вопросов «а велик
        // ли мой мир без прыжка». Считаются здесь же, за один проход.
        world.JunctionComponentsFlatSizes.Clear();
        world.LargestFlatComponentId = -1;
        var largestSize = 0;
        foreach (var pair in world.JunctionComponentsFlat)
        {
            if (pair.Value <= 0)
            {
                continue;
            }

            world.JunctionComponentsFlatSizes.TryGetValue(pair.Value, out var size);
            size++;
            world.JunctionComponentsFlatSizes[pair.Value] = size;
            if (size > largestSize)
            {
                largestSize = size;
                world.LargestFlatComponentId = pair.Value;
            }
        }

        // §57.11: замыкание спусков. Ребро вниз (не в воду, не «шов→шов» —
        // ровно правила пафйндера для canJump=false) соединяет компоненты
        // НАПРАВЛЕННО: сползти на нижнюю полку можно, вернуться — нет. По
        // высотам граф компонент ацикличен, но замыкание считается BFS-ом и
        // само по себе устойчиво к любой форме.
        //
        // §40.18-C (#162): сюда же — ВЫХОД ИЗ ВОДЫ. Он тоже направленный (выйти
        // можно, войти по нему нельзя) и тоже обязан быть проекцией правил
        // пафйндера, иначе повторится ровно та беда, о которой предупреждает
        // комментарий выше: граф обещает то, чего FindPath не строит, либо
        // молчит о том, что FindPath умеет. Замыкание BFS-ом уже устойчиво к
        // тому, что этот граф перестал быть ацикличным по высотам.
        //
        // §158.3: рёбра считаются СО СЧЁТОМ (и в обратном индексе), чтобы
        // точечное удаление узла вычитало свои рёбра, а ребро компонент жило,
        // пока его держит хоть одна пара узлов.
        world.FlatDescendClosure.Clear();
        world.FlatDescendEdges.Clear();
        world.FlatDescendEdgesIn.Clear();
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked ||
                !world.JunctionComponentsFlat.TryGetValue(junction.Id, out var fromComp) ||
                fromComp <= 0)
            {
                continue;
            }

            for (var n = 0; n < junction.Neighbors.Count; n++)
            {
                var neighborId = junction.Neighbors[n];
                if (!world.Junctions.Items.TryGetValue(neighborId, out var neighbor) ||
                    neighbor.Blocked ||
                    !world.JunctionComponentsFlat.TryGetValue(neighborId, out var toComp) ||
                    toComp <= 0 || toComp == fromComp ||
                    !Descends(world, junction, n, neighbor))
                {
                    continue;
                }

                CountEdge(world, fromComp, toComp, +1);
            }
        }

        // PERF (Aug-2026): замыкание больше НЕ материализуется здесь целиком —
        // на 194k джанкшенов полный проход по всем компонентам был квадратичным
        // (~930 МБ и сотни мс за одну перестройку, а перестройку дёргает каждая
        // стройка). Ответы считает ClosureOf по требованию и кэширует в
        // FlatDescendClosure; сами ответы бит-в-бит те же — BFS по тем же
        // рёбрам с тем же исключением самой стартовой компоненты.
        world.ComponentsFlatBuiltVersion = world.TopologyVersion;
        world.ComponentsFlatJournalCursor = world.Topology.EndIndex;
        if (SimTrace.Enabled)
        {
            Trace.DebugSystem(world, "ConnectivityFlatRebuilt",
                $"Components={component} Junctions={world.Junctions.Items.Count} " +
                $"Largest={world.LargestFlatComponentId}({largestSize}) " +
                $"DescendEdgeSources={world.FlatDescendEdges.Count}");
        }
    }

    /// <summary>§57.11: ленивое замыкание спусков компоненты. Первая просьба
    /// считает BFS по FlatDescendEdges и кэширует; RebuildFlat чистит кэш.</summary>
    private static System.Collections.Generic.HashSet<int> ClosureOf(WorldState world, int component)
    {
        if (world.FlatDescendClosure.TryGetValue(component, out var cached))
        {
            return cached;
        }

        var closure = new System.Collections.Generic.HashSet<int>();
        if (world.FlatDescendEdges.TryGetValue(component, out var direct))
        {
            var frontier = _closureFrontierScratch;
            frontier.Clear();
            foreach (var d in direct.Keys)
            {
                if (closure.Add(d))
                {
                    frontier.Enqueue(d);
                }
            }

            while (frontier.Count > 0)
            {
                var comp = frontier.Dequeue();
                if (!world.FlatDescendEdges.TryGetValue(comp, out var next))
                {
                    continue;
                }

                foreach (var further in next.Keys)
                {
                    if (further != component && closure.Add(further))
                    {
                        frontier.Enqueue(further);
                    }
                }
            }
        }

        world.FlatDescendClosure[component] = closure;
        return closure;
    }

    private static readonly System.Collections.Generic.Queue<int> _closureFrontierScratch = new();

    /// <summary>§57.11: достижима ли компонента b из компоненты a без прыжка,
    /// СЧИТАЯ спуски (направленно). Та же компонента — тривиально да.</summary>
    private static bool FlatReaches(WorldState world, int fa, int fb) =>
        fa == fb || ClosureOf(world, fa).Contains(fb);

    /// <summary>§57.11: есть ли отсюда БЕЗ ПРЫЖКА дорога (считая спуски) в
    /// самую большую плоскую компоненту. Скоринг §50.9 обязан спросить это ДО
    /// назначения цели: иначе запертая на бессходной полке крутит вечный цикл
    /// PlanFailed NoRouteToMainland (замерено: 184 события на два сида).</summary>
    public static bool FlatReachesMainland(WorldState world, JunctionId from)
    {
        EnsureFlat(world);

        return world.LargestFlatComponentId > 0 &&
               world.JunctionComponentsFlat.TryGetValue(from, out var comp) && comp > 0 &&
               FlatReaches(world, comp, world.LargestFlatComponentId);
    }

    /// <summary>§57.11: размер мира, доступного отсюда без прыжка, — своя
    /// плоская компонента ПЛЮС всё, куда можно сползти. Именно этим числом
    /// §50.9 меряет «я на крошечном уступе» против «подо мной материк».</summary>
    public static int FlatWorldSizeAt(WorldState world, JunctionId junction)
    {
        EnsureFlat(world);

        if (!world.JunctionComponentsFlat.TryGetValue(junction, out var comp) || comp <= 0)
        {
            return 0;
        }

        world.JunctionComponentsFlatSizes.TryGetValue(comp, out var total);
        foreach (var reachable in ClosureOf(world, comp))
        {
            world.JunctionComponentsFlatSizes.TryGetValue(reachable, out var size);
            total += size;
        }

        return total;
    }
}

}
