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

    /// <summary>
    /// PERF (Aug-2026, BigIsland): the spatial index this routing runs on.
    /// The unindexed Build swept ALL junctions per call — the blocked-band test
    /// alone was a distance-to-segment against every blocked junction on the
    /// island (~194k on BigIsland), per WALKING NPC per tick, and the routed
    /// branch additionally built a 194k-entry dictionary per call. That was
    /// ~59 of the 63 ms of a big-island tick frame. The owner (the renderer)
    /// rebuilds the index only when the snapshot's junction set or its
    /// Blocked stamp changes — positions are worldgen output and never move.
    /// </summary>
    public sealed class RouteIndex
    {
        internal const float CellSize = 3f;

        internal readonly Dictionary<JunctionId, JunctionSnapshot> ById = new();

        internal readonly Dictionary<long, List<JunctionSnapshot>> Cells = new();

        internal int MinCellX, MaxCellX, MinCellY, MaxCellY;

        public int BuiltStamp { get; private set; } = int.MinValue;

        public int BuiltCount { get; private set; } = -1;

        // BFS / path scratches — one routing runs at a time (render thread).
        internal readonly Queue<JunctionId> Frontier = new();
        internal readonly Dictionary<JunctionId, JunctionId> Parent = new();
        internal readonly Dictionary<JunctionId, int> Depth = new();
        internal readonly List<Float2> Reversed = new();

        public bool IsCurrent(IReadOnlyList<JunctionSnapshot> junctions, int stamp) =>
            BuiltStamp == stamp && junctions != null && BuiltCount == junctions.Count;

        public void Rebuild(IReadOnlyList<JunctionSnapshot> junctions, int stamp)
        {
            ById.Clear();
            Cells.Clear();
            MinCellX = MinCellY = int.MaxValue;
            MaxCellX = MaxCellY = int.MinValue;
            for (var i = 0; i < junctions.Count; i++)
            {
                var junction = junctions[i];
                ById[junction.Id] = junction;
                var cx = CellOf(junction.WorldPosition.X);
                var cy = CellOf(junction.WorldPosition.Y);
                MinCellX = Math.Min(MinCellX, cx);
                MaxCellX = Math.Max(MaxCellX, cx);
                MinCellY = Math.Min(MinCellY, cy);
                MaxCellY = Math.Max(MaxCellY, cy);
                var key = Key(cx, cy);
                if (!Cells.TryGetValue(key, out var list))
                {
                    list = new List<JunctionSnapshot>();
                    Cells[key] = list;
                }

                list.Add(junction);
            }

            BuiltStamp = stamp;
            BuiltCount = junctions.Count;
        }

        internal static int CellOf(float v) => (int)MathF.Floor(v / CellSize);

        internal static long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;
    }

    /// <summary>Compatibility entry (tests, ad-hoc callers): builds a throwaway
    /// index every call. Production goes through the indexed overload.</summary>
    public static RouteKind Build(
        IReadOnlyList<JunctionSnapshot> junctions,
        Float2 from,
        Float2 to,
        List<Float2> route)
    {
        if (junctions == null) throw new ArgumentNullException(nameof(junctions));
        var index = new RouteIndex();
        index.Rebuild(junctions, int.MinValue + 1);
        return Build(index, from, to, route);
    }

    public static RouteKind Build(
        RouteIndex index,
        Float2 from,
        Float2 to,
        List<Float2> route)
    {
        if (index == null) throw new ArgumentNullException(nameof(index));
        if (route == null) throw new ArgumentNullException(nameof(route));
        route.Clear();

        if (!CrossesBlockedBand(index, from, to))
        {
            return RouteKind.Direct;
        }

        var start = FindNearestUnblocked(index, from);
        var finish = FindNearestUnblocked(index, to);
        if (start == null || finish == null)
        {
            return RouteKind.Disconnected;
        }

        // Positions may sit between two junctions, but their short attachment
        // segments still have to live in free space. A furniture snap or stale
        // remote pose inside solid geometry must never be dressed up as travel.
        if (CrossesBlockedBand(index, from, start.WorldPosition) ||
            CrossesBlockedBand(index, finish.WorldPosition, to))
        {
            return RouteKind.Disconnected;
        }

        var frontier = index.Frontier;
        var parent = index.Parent;
        var depth = index.Depth;
        frontier.Clear();
        parent.Clear();
        depth.Clear();
        frontier.Enqueue(start.Id);
        parent[start.Id] = start.Id;
        depth[start.Id] = 0;

        while (frontier.Count > 0 && !parent.ContainsKey(finish.Id))
        {
            var currentId = frontier.Dequeue();
            var current = index.ById[currentId];
            var nextDepth = depth[currentId] + 1;
            if (nextDepth > MaximumJunctionHops) continue;
            for (var i = 0; i < current.Neighbors.Count; i++)
            {
                var neighborId = current.Neighbors[i];
                if (parent.ContainsKey(neighborId) ||
                    !index.ById.TryGetValue(neighborId, out var neighbor) ||
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

        var reversed = index.Reversed;
        reversed.Clear();
        var cursor = finish.Id;
        while (cursor != start.Id)
        {
            reversed.Add(index.ById[cursor].WorldPosition);
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

    // The same boolean the full sweep produced: any BLOCKED junction whose
    // distance to the segment is within the band. Only cells overlapping the
    // segment's AABB inflated by the band radius can contain such a junction.
    private static bool CrossesBlockedBand(RouteIndex index, Float2 from, Float2 to)
    {
        var radius = BarrierRadius;
        var radiusSq = radius * radius + 0.000001f;
        var pad = radius + 0.01f;
        var minCx = RouteIndex.CellOf(MathF.Min(from.X, to.X) - pad);
        var maxCx = RouteIndex.CellOf(MathF.Max(from.X, to.X) + pad);
        var minCy = RouteIndex.CellOf(MathF.Min(from.Y, to.Y) - pad);
        var maxCy = RouteIndex.CellOf(MathF.Max(from.Y, to.Y) + pad);
        for (var cx = minCx; cx <= maxCx; cx++)
        {
            for (var cy = minCy; cy <= maxCy; cy++)
            {
                if (!index.Cells.TryGetValue(RouteIndex.Key(cx, cy), out var cell))
                {
                    continue;
                }

                for (var i = 0; i < cell.Count; i++)
                {
                    var junction = cell[i];
                    if (junction.Blocked &&
                        DistanceToSegmentSquared(junction.WorldPosition, from, to) <= radiusSq)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    // Expanding cell rings; exact same answer as the global arg-min the sweep
    // produced, tie-break included: smallest distance, then lowest id (the old
    // list was sorted by id and kept the first strict minimum).
    private static JunctionSnapshot FindNearestUnblocked(RouteIndex index, Float2 point)
    {
        var cs = RouteIndex.CellSize;
        var cx0 = RouteIndex.CellOf(point.X);
        var cy0 = RouteIndex.CellOf(point.Y);
        var maxRing = Math.Max(
            Math.Max(Math.Abs(cx0 - index.MinCellX), Math.Abs(index.MaxCellX - cx0)),
            Math.Max(Math.Abs(cy0 - index.MinCellY), Math.Abs(index.MaxCellY - cy0)));
        JunctionSnapshot best = null;
        var bestSq = float.MaxValue;
        for (var ring = 0; ring <= maxRing; ring++)
        {
            // A cell on Chebyshev ring r cannot hold a point closer than (r-1)
            // cells away from anywhere inside the centre cell.
            var lowerBound = (ring - 1) * cs;
            if (best != null && lowerBound > 0f && lowerBound * lowerBound > bestSq)
            {
                break;
            }

            for (var cx = cx0 - ring; cx <= cx0 + ring; cx++)
            {
                for (var cy = cy0 - ring; cy <= cy0 + ring; cy++)
                {
                    if (ring > 0 &&
                        Math.Abs(cx - cx0) != ring && Math.Abs(cy - cy0) != ring)
                    {
                        continue; // interior — already visited on a smaller ring
                    }

                    if (!index.Cells.TryGetValue(RouteIndex.Key(cx, cy), out var cell))
                    {
                        continue;
                    }

                    for (var i = 0; i < cell.Count; i++)
                    {
                        var junction = cell[i];
                        if (junction.Blocked)
                        {
                            continue;
                        }

                        var d = DistanceSquared(point, junction.WorldPosition);
                        if (d < bestSq ||
                            (d == bestSq && best != null &&
                             junction.Id.Value < best.Id.Value))
                        {
                            bestSq = d;
                            best = junction;
                        }
                    }
                }
            }
        }

        return best;
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
