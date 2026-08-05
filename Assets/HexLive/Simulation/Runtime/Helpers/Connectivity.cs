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
        // Spec §50: a legless survivor reads the graph WITHOUT elevation-step
        // edges — a higher ledge or the water is a separate component to her.
        if (!canJump)
        {
            if (world.ComponentsFlatBuiltVersion != world.TopologyVersion)
            {
                RebuildFlat(world);
            }

            return world.JunctionComponentsFlat.TryGetValue(a, out var fa) && fa >= 0 &&
                   world.JunctionComponentsFlat.TryGetValue(b, out var fb) &&
                   fa == fb;
        }

        if (world.ComponentsBuiltVersion != world.TopologyVersion)
        {
            Rebuild(world);
        }

        return world.JunctionComponents.TryGetValue(a, out var ca) && ca >= 0 &&
               world.JunctionComponents.TryGetValue(b, out var cb) &&
               ca == cb;
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
        Trace.EmitSystem(world, "ConnectivityRebuilt",
            $"Components={component} Junctions={world.Junctions.Items.Count}");
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
                    if (world.JunctionComponentsFlat.TryGetValue(neighborId, out var mark) && mark == 0 &&
                        world.Junctions.Items.TryGetValue(neighborId, out var neighbor) && !neighbor.Blocked &&
                        !Navigation.HexPathfinder.RequiresJump(world, currentId, neighborId))
                    {
                        world.JunctionComponentsFlat[neighborId] = component;
                        _queue.Enqueue(neighborId);
                    }
                }
            }
        }

        world.ComponentsFlatBuiltVersion = world.TopologyVersion;
        Trace.EmitSystem(world, "ConnectivityFlatRebuilt",
            $"Components={component} Junctions={world.Junctions.Items.Count}");
    }
}

}
