using System;
using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Debug
{

/// <summary>
/// §71.10: restores the passable junction polyline between two sparse snapshot poses.
/// The simulation already walked this graph; presentation must not replace a legal bend
/// through a doorway with a straight chord through the wall between the two samples.
/// </summary>
public static class SnapshotMovementRoute
{
    private const int MaximumJunctionHops = 64;

    public enum RouteKind
    {
        Direct,
        Routed,
        Disconnected
    }

    // Adjacent sub-grid junctions are HexRadius / BoundaryRadius apart. Half of
    // that step joins the discrete blocked wall nodes into one continuous band.
    public static float BarrierRadius =>
        HexSpatialMath.HexRadius / HexPointLayout.BoundaryRadius * 0.5f;

    public static RouteKind Build(
        IReadOnlyList<JunctionSnapshot> junctions,
        Float2 from,
        Float2 to,
        List<Float2> route)
    {
        if (junctions == null) throw new ArgumentNullException(nameof(junctions));
        if (route == null) throw new ArgumentNullException(nameof(route));
        route.Clear();

        if (!CrossesBlockedBand(junctions, from, to))
        {
            return RouteKind.Direct;
        }

        var byId = new Dictionary<JunctionId, JunctionSnapshot>(junctions.Count);
        JunctionSnapshot start = null;
        JunctionSnapshot finish = null;
        var startDistanceSq = float.MaxValue;
        var finishDistanceSq = float.MaxValue;
        for (var i = 0; i < junctions.Count; i++)
        {
            var junction = junctions[i];
            byId[junction.Id] = junction;
            if (junction.Blocked) continue;

            var fromDistanceSq = DistanceSquared(from, junction.WorldPosition);
            if (fromDistanceSq < startDistanceSq)
            {
                startDistanceSq = fromDistanceSq;
                start = junction;
            }

            var toDistanceSq = DistanceSquared(to, junction.WorldPosition);
            if (toDistanceSq < finishDistanceSq)
            {
                finishDistanceSq = toDistanceSq;
                finish = junction;
            }
        }

        if (start == null || finish == null)
        {
            return RouteKind.Disconnected;
        }

        // Positions may sit between two junctions, but their short attachment
        // segments still have to live in free space. A furniture snap or stale
        // remote pose inside solid geometry must never be dressed up as travel.
        if (CrossesBlockedBand(junctions, from, start.WorldPosition) ||
            CrossesBlockedBand(junctions, finish.WorldPosition, to))
        {
            return RouteKind.Disconnected;
        }

        var frontier = new Queue<JunctionId>();
        var parent = new Dictionary<JunctionId, JunctionId>();
        var depth = new Dictionary<JunctionId, int>();
        frontier.Enqueue(start.Id);
        parent[start.Id] = start.Id;
        depth[start.Id] = 0;

        while (frontier.Count > 0 && !parent.ContainsKey(finish.Id))
        {
            var currentId = frontier.Dequeue();
            var current = byId[currentId];
            var nextDepth = depth[currentId] + 1;
            if (nextDepth > MaximumJunctionHops) continue;
            for (var i = 0; i < current.Neighbors.Count; i++)
            {
                var neighborId = current.Neighbors[i];
                if (parent.ContainsKey(neighborId) ||
                    !byId.TryGetValue(neighborId, out var neighbor) ||
                    neighbor.Blocked)
                {
                    continue;
                }

                parent[neighborId] = currentId;
                depth[neighborId] = nextDepth;
                frontier.Enqueue(neighborId);
            }
        }

        if (!parent.ContainsKey(finish.Id))
        {
            return RouteKind.Disconnected;
        }

        var reversed = new List<Float2>();
        var cursor = finish.Id;
        while (cursor != start.Id)
        {
            reversed.Add(byId[cursor].WorldPosition);
            cursor = parent[cursor];
        }
        reversed.Add(start.WorldPosition);

        AddDistinct(route, from);
        for (var i = reversed.Count - 1; i >= 0; i--)
        {
            AddDistinct(route, reversed[i]);
        }
        AddDistinct(route, to);
        return RouteKind.Routed;
    }

    public static Float2 Evaluate(IReadOnlyList<Float2> route, float alpha)
    {
        if (route == null || route.Count == 0) return Float2.Zero;
        if (route.Count == 1 || alpha <= 0f) return route[0];
        if (alpha >= 1f) return route[route.Count - 1];

        var total = 0f;
        for (var i = 1; i < route.Count; i++)
        {
            total += Distance(route[i - 1], route[i]);
        }

        if (total <= 0.00001f) return route[route.Count - 1];
        var remaining = total * alpha;
        for (var i = 1; i < route.Count; i++)
        {
            var segment = Distance(route[i - 1], route[i]);
            if (remaining <= segment && segment > 0.00001f)
            {
                var t = remaining / segment;
                var a = route[i - 1];
                var b = route[i];
                return new Float2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
            }
            remaining -= segment;
        }

        return route[route.Count - 1];
    }

    private static bool CrossesBlockedBand(
        IReadOnlyList<JunctionSnapshot> junctions, Float2 from, Float2 to)
    {
        var radiusSq = BarrierRadius * BarrierRadius + 0.000001f;
        for (var i = 0; i < junctions.Count; i++)
        {
            if (junctions[i].Blocked &&
                DistanceToSegmentSquared(junctions[i].WorldPosition, from, to) <= radiusSq)
            {
                return true;
            }
        }
        return false;
    }

    private static float DistanceToSegmentSquared(Float2 point, Float2 start, Float2 end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSq = dx * dx + dy * dy;
        if (lengthSq <= 0.000001f) return DistanceSquared(point, start);
        var t = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSq;
        t = Math.Max(0f, Math.Min(1f, t));
        var closest = new Float2(start.X + dx * t, start.Y + dy * t);
        return DistanceSquared(point, closest);
    }

    private static float Distance(Float2 a, Float2 b) => MathF.Sqrt(DistanceSquared(a, b));

    private static float DistanceSquared(Float2 a, Float2 b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return dx * dx + dy * dy;
    }

    private static void AddDistinct(List<Float2> route, Float2 point)
    {
        if (route.Count == 0 || DistanceSquared(route[route.Count - 1], point) > 0.000001f)
        {
            route.Add(point);
        }
    }
}

}
