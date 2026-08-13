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
        // Spec §50 + §57.11: a survivor who can't jump reads the graph without
        // CLIMB edges, but descents count (directed): a lower shelf is
        // reachable, the way back is not. Water stays its own world.
        if (!canJump)
        {
            if (world.ComponentsFlatBuiltVersion != world.TopologyVersion)
            {
                RebuildFlat(world);
            }

            return world.JunctionComponentsFlat.TryGetValue(a, out var fa) && fa >= 0 &&
                   world.JunctionComponentsFlat.TryGetValue(b, out var fb) && fb >= 0 &&
                   FlatReaches(world, fa, fb);
        }

        if (world.ComponentsBuiltVersion != world.TopologyVersion)
        {
            Rebuild(world);
        }

        return world.JunctionComponents.TryGetValue(a, out var ca) && ca >= 0 &&
               world.JunctionComponents.TryGetValue(b, out var cb) &&
               ca == cb;
    }

    // §22.7: идентификатор компоненты связности узла в нужном варианте графа.
    // Reachable(a,b) — это ровно сравнение этих идентификаторов, поэтому кэш,
    // ключёванный компонентой «откуда», меняет ответы только вместе с ними.
    // Лениво перестраивает словарь так же, как Reachable.
    public static int ComponentOf(WorldState world, JunctionId junction, bool canJump)
    {
        if (!canJump)
        {
            if (world.ComponentsFlatBuiltVersion != world.TopologyVersion)
            {
                RebuildFlat(world);
            }

            return world.JunctionComponentsFlat.TryGetValue(junction, out var flat)
                ? flat
                : int.MinValue + 1;
        }

        if (world.ComponentsBuiltVersion != world.TopologyVersion)
        {
            Rebuild(world);
        }

        return world.JunctionComponents.TryGetValue(junction, out var component)
            ? component
            : int.MinValue + 1;
    }

    /// <summary>Сколько узлов в ПЛОСКОЙ компоненте этого узла (0 — узел
    /// заблокирован/неизвестен). Ленивая перестройка как у Reachable.</summary>
    public static int FlatComponentSizeAt(WorldState world, JunctionId junction)
    {
        if (world.ComponentsFlatBuiltVersion != world.TopologyVersion)
        {
            RebuildFlat(world);
        }

        return world.JunctionComponentsFlat.TryGetValue(junction, out var comp) && comp > 0 &&
            world.JunctionComponentsFlatSizes.TryGetValue(comp, out var size)
                ? size
                : 0;
    }

    private static readonly System.Collections.Generic.Queue<JunctionId> _queue = new();

    private static void Rebuild(WorldState world)
    {
        world.JunctionComponents.Clear();
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
                        world.Junctions.Items.TryGetValue(neighborId, out var neighbor) && !neighbor.Blocked)
                    {
                        world.JunctionComponents[neighborId] = component;
                        _queue.Enqueue(neighborId);
                    }
                }
            }
        }

        world.ComponentsBuiltVersion = world.TopologyVersion;
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
        world.FlatDescendClosure.Clear();
        var descendEdges = new System.Collections.Generic.Dictionary<int,
            System.Collections.Generic.HashSet<int>>();
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
                    world.SwimJunctions.Contains(neighborId) ||
                    world.StraitJunctions.Contains(neighborId) ||
                    (world.ClimbSeams.Contains(junction.Id) &&
                     world.ClimbSeams.Contains(neighborId)) ||
                    Navigation.HexPathfinder.StepDelta(world, junction, n, neighborId) >= 0 ||
                    !world.JunctionComponentsFlat.TryGetValue(neighborId, out var toComp) ||
                    toComp <= 0 || toComp == fromComp)
                {
                    continue;
                }

                if (!descendEdges.TryGetValue(fromComp, out var outs))
                {
                    outs = new System.Collections.Generic.HashSet<int>();
                    descendEdges[fromComp] = outs;
                }

                outs.Add(toComp);
            }
        }

        var descendEdgeCount = 0;
        foreach (var pair in descendEdges)
        {
            descendEdgeCount += pair.Value.Count;
            var closure = new System.Collections.Generic.HashSet<int>();
            var frontier = new System.Collections.Generic.Queue<int>();
            foreach (var direct in pair.Value)
            {
                if (closure.Add(direct))
                {
                    frontier.Enqueue(direct);
                }
            }

            while (frontier.Count > 0)
            {
                var comp = frontier.Dequeue();
                if (!descendEdges.TryGetValue(comp, out var next))
                {
                    continue;
                }

                foreach (var further in next)
                {
                    if (further != pair.Key && closure.Add(further))
                    {
                        frontier.Enqueue(further);
                    }
                }
            }

            world.FlatDescendClosure[pair.Key] = closure;
        }

        world.ComponentsFlatBuiltVersion = world.TopologyVersion;
        if (SimTrace.Enabled)
        {
            Trace.DebugSystem(world, "ConnectivityFlatRebuilt",
                $"Components={component} Junctions={world.Junctions.Items.Count} " +
                $"Largest={world.LargestFlatComponentId}({largestSize}) " +
                $"DescendEdges={descendEdgeCount}");
        }
    }

    /// <summary>§57.11: достижима ли компонента b из компоненты a без прыжка,
    /// СЧИТАЯ спуски (направленно). Та же компонента — тривиально да.</summary>
    private static bool FlatReaches(WorldState world, int fa, int fb) =>
        fa == fb ||
        (world.FlatDescendClosure.TryGetValue(fa, out var closure) && closure.Contains(fb));

    /// <summary>§57.11: есть ли отсюда БЕЗ ПРЫЖКА дорога (считая спуски) в
    /// самую большую плоскую компоненту. Скоринг §50.9 обязан спросить это ДО
    /// назначения цели: иначе запертая на бессходной полке крутит вечный цикл
    /// PlanFailed NoRouteToMainland (замерено: 184 события на два сида).</summary>
    public static bool FlatReachesMainland(WorldState world, JunctionId from)
    {
        if (world.ComponentsFlatBuiltVersion != world.TopologyVersion)
        {
            RebuildFlat(world);
        }

        return world.LargestFlatComponentId > 0 &&
               world.JunctionComponentsFlat.TryGetValue(from, out var comp) && comp > 0 &&
               FlatReaches(world, comp, world.LargestFlatComponentId);
    }

    /// <summary>§57.11: размер мира, доступного отсюда без прыжка, — своя
    /// плоская компонента ПЛЮС всё, куда можно сползти. Именно этим числом
    /// §50.9 меряет «я на крошечном уступе» против «подо мной материк».</summary>
    public static int FlatWorldSizeAt(WorldState world, JunctionId junction)
    {
        if (world.ComponentsFlatBuiltVersion != world.TopologyVersion)
        {
            RebuildFlat(world);
        }

        if (!world.JunctionComponentsFlat.TryGetValue(junction, out var comp) || comp <= 0)
        {
            return 0;
        }

        world.JunctionComponentsFlatSizes.TryGetValue(comp, out var total);
        if (world.FlatDescendClosure.TryGetValue(comp, out var closure))
        {
            foreach (var reachable in closure)
            {
                world.JunctionComponentsFlatSizes.TryGetValue(reachable, out var size);
                total += size;
            }
        }

        return total;
    }
}

}
