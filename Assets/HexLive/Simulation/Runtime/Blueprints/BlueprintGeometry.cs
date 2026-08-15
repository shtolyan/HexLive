using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime.Blueprints
{
    /// <summary>
    /// Integer-only architecture lattice. One neighbor step is 0.5 wu and the
    /// pointy-top R=1.5 hex has three steps per edge. See §120.
    /// </summary>
    public static class BlueprintGeometry
    {
        public const float BuildStep = 0.5f;
        public const int SectionsPerHexEdge = 3;
        public const int PerimeterSectionCount = 18;
        public const int RequiredRoofSupportCount = 2;

        public static readonly HexBuildNodeKey[] NeighborDirections =
        {
            new HexBuildNodeKey(0, 1),
            new HexBuildNodeKey(1, 0),
            new HexBuildNodeKey(1, -1),
            new HexBuildNodeKey(0, -1),
            new HexBuildNodeKey(-1, 0),
            new HexBuildNodeKey(-1, 1)
        };

        private static readonly HexBuildNodeKey[] CornerOffsets =
        {
            new HexBuildNodeKey(0, 3),
            new HexBuildNodeKey(3, 0),
            new HexBuildNodeKey(3, -3),
            new HexBuildNodeKey(0, -3),
            new HexBuildNodeKey(-3, 0),
            new HexBuildNodeKey(-3, 3)
        };

        public static int NormalizeSector(int value)
        {
            value %= 6;
            return value < 0 ? value + 6 : value;
        }

        public static HexBuildNodeKey HexCenter(TileCoord tile) =>
            new HexBuildNodeKey(6 * tile.Q + 3 * tile.R, -3 * tile.Q + 3 * tile.R);

        public static HexBuildNodeKey HexCorner(TileCoord tile, int corner) =>
            HexCenter(tile) + CornerOffsets[NormalizeSector(corner)];

        public static Float2 ToWorld(HexBuildNodeKey node) => new Float2(
            BuildStep * HexSpatialMath.Sqrt3 * 0.5f * node.Q,
            BuildStep * (node.R + node.Q * 0.5f));

        public static Float2 JunctionToWorld(JunctionKey key) => new Float2(
            HexSpatialMath.Sqrt3 * 0.1875f * key.XKey,
            0.1875f * key.YKey);

        public static IReadOnlyList<BuildSegmentKey> HexPerimeter(TileCoord tile)
        {
            var result = new List<BuildSegmentKey>(PerimeterSectionCount);
            for (var edge = 0; edge < 6; edge++)
                result.AddRange(SplitLine(HexCorner(tile, edge), HexCorner(tile, edge + 1)));
            return result;
        }

        /// <summary>
        /// The posts a roof sector actually stands on: the two corners of its
        /// OWN outer hex edge. The panel spans from that edge up to the hex
        /// centre, where it meets its neighbours, so those two posts carry it.
        /// Counting three of the parent hex's six corners instead made a sector
        /// unbuildable whenever a room was stretched: a hex gained by stretching
        /// shares only two corners with the hex the room started on, and the
        /// player was left staring at the posts holding the very edge he was
        /// trying to roof.
        /// </summary>
        public static IReadOnlyList<HexBuildNodeKey> RoofSupports(RoofSectorKey sector) =>
            new[]
            {
                HexCorner(sector.Hex, sector.Sector),
                HexCorner(sector.Hex, sector.Sector + 1)
            };

        /// <summary>
        /// Which seam node carries each bay's single post pair.
        ///
        /// A wall/window/door section authors ONE pair of posts, and the export
        /// puts it on the model's local -Z end. Reading that end off the segment
        /// key does not work: <see cref="BuildSegmentKey"/> SORTS A and B, so
        /// around a closed boundary the orientation turns over wherever the key
        /// ordering does. A node that is the A of both its bays then grows two
        /// posts inside each other, and a node that is the A of neither gets no
        /// post at all. Measured on the player's own three-hex draft: 6 doubled
        /// joints and 5 bare ones out of 24 seams.
        ///
        /// So the NODE owns the post, not the bay. Every boundary component is
        /// walked once and each bay is handed the node it arrives at: a closed
        /// ring comes out with exactly one post per node, and an open chain
        /// leaves only its very first node bare, which is the true minimum when
        /// each section ships one pair. A bay that is handed nothing draws no
        /// post — its neighbours already cover both its ends.
        ///
        /// The walk starts at the ends and forks first (degree != 2) so an open
        /// chain is covered from its open end rather than from its middle.
        /// </summary>
        public static IReadOnlyDictionary<string, HexBuildNodeKey> AssignSeamPosts(
            IEnumerable<BlueprintElementData> elements)
        {
            var owner = new Dictionary<string, HexBuildNodeKey>();
            if (elements == null) return owner;

            // Deterministic order in, deterministic assignment out: the walk
            // below always takes the first unwalked bay at a node.
            var bays = elements
                .Where(element => element != null &&
                    element.Kind is BlueprintElementKind.Wall or BlueprintElementKind.Window
                        or BlueprintElementKind.Door)
                .OrderBy(element => element.Segment)
                .ThenBy(element => element.Id, StringComparer.Ordinal)
                .ToArray();
            if (bays.Length == 0) return owner;

            var incident = new Dictionary<HexBuildNodeKey, List<BlueprintElementData>>();
            foreach (var bay in bays)
            {
                Touch(incident, bay.Segment.A).Add(bay);
                Touch(incident, bay.Segment.B).Add(bay);
            }

            var starts = incident.Keys.OrderBy(node => node).ToArray();
            var claimed = new HashSet<HexBuildNodeKey>();
            var walked = new HashSet<string>(StringComparer.Ordinal);
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var start in starts)
                {
                    if (pass == 0 && incident[start].Count == 2) continue;
                    WalkSeam(incident, start, claimed, walked, owner);
                }
            }
            return owner;
        }

        private static List<BlueprintElementData> Touch(
            Dictionary<HexBuildNodeKey, List<BlueprintElementData>> incident, HexBuildNodeKey node)
        {
            if (!incident.TryGetValue(node, out var list))
            {
                list = new List<BlueprintElementData>();
                incident[node] = list;
            }
            return list;
        }

        private static void WalkSeam(
            Dictionary<HexBuildNodeKey, List<BlueprintElementData>> incident,
            HexBuildNodeKey start,
            HashSet<HexBuildNodeKey> claimed,
            HashSet<string> walked,
            Dictionary<string, HexBuildNodeKey> owner)
        {
            var current = start;
            while (true)
            {
                BlueprintElementData next = null;
                foreach (var candidate in incident[current])
                {
                    if (walked.Contains(candidate.Id)) continue;
                    next = candidate;
                    break;
                }
                if (next == null) return;
                walked.Add(next.Id);
                var far = next.Segment.A == current ? next.Segment.B : next.Segment.A;
                // Arriving at a free node claims it. Walking a ring, the last
                // bay arrives back at the node the walk started from, which is
                // still free — that is why a closed room needs no extra pass.
                if (claimed.Add(far)) owner[next.Id] = far;
                else if (claimed.Add(current)) owner[next.Id] = current;
                current = far;
            }
        }

        public static IReadOnlyList<BuildSegmentKey> SectorBoundary(FloorSectorKey sector)
        {
            var center = HexCenter(sector.Hex);
            var first = HexCorner(sector.Hex, sector.Sector);
            var second = HexCorner(sector.Hex, sector.Sector + 1);
            return SplitLine(center, first)
                .Concat(SplitLine(first, second))
                .Concat(SplitLine(second, center))
                .Distinct()
                .ToArray();
        }

        public static IReadOnlyList<BuildSegmentKey> BoundaryOf(IEnumerable<FloorSectorKey> sectors)
        {
            var counts = new Dictionary<BuildSegmentKey, int>();
            foreach (var sector in sectors.Distinct())
            {
                foreach (var segment in SectorBoundary(sector))
                    counts[segment] = counts.TryGetValue(segment, out var count) ? count + 1 : 1;
            }

            return counts.Where(pair => pair.Value == 1)
                .Select(pair => pair.Key)
                .OrderBy(segment => segment)
                .ToArray();
        }

        public static IReadOnlyList<BuildSegmentKey> SplitLine(HexBuildNodeKey start, HexBuildNodeKey end)
        {
            if (!TryLine(start, end, out var direction, out var length))
                throw new ArgumentException($"Build nodes {start} and {end} are not on one lattice axis.");

            var result = new List<BuildSegmentKey>(length);
            var current = start;
            for (var i = 0; i < length; i++)
            {
                var next = current + direction;
                result.Add(new BuildSegmentKey(current, next));
                current = next;
            }
            return result;
        }

        public static bool TryLine(
            HexBuildNodeKey start,
            HexBuildNodeKey end,
            out HexBuildNodeKey direction,
            out int length)
        {
            var dq = end.Q - start.Q;
            var dr = end.R - start.R;
            length = Math.Max(Math.Abs(dq), Math.Abs(dr));
            direction = default;
            if (length == 0) return false;
            if (dq != 0 && dr != 0 && dq != -dr) return false;

            direction = new HexBuildNodeKey(Math.Sign(dq), Math.Sign(dr));
            if (dq == 0) direction = new HexBuildNodeKey(0, Math.Sign(dr));
            else if (dr == 0) direction = new HexBuildNodeKey(Math.Sign(dq), 0);
            else direction = new HexBuildNodeKey(Math.Sign(dq), -Math.Sign(dq));
            return true;
        }

        public static bool IsUnitSegment(BuildSegmentKey segment)
        {
            var dq = segment.B.Q - segment.A.Q;
            var dr = segment.B.R - segment.A.R;
            return (Math.Abs(dq) == 1 && dr == 0) ||
                   (Math.Abs(dr) == 1 && dq == 0) ||
                   (Math.Abs(dq) == 1 && dq == -dr);
        }

        public static bool TryDoorPortal(BuildSegmentKey segment, out JunctionKey portal)
        {
            portal = default;
            if (!IsUnitSegment(segment)) return false;
            var qSum = segment.A.Q + segment.B.Q;
            var rSum = segment.A.R + segment.B.R;
            var xNumerator = 2 * qSum;
            var yNumerator = 2 * qSum + 4 * rSum;
            if (xNumerator % 3 != 0 || yNumerator % 3 != 0) return false;
            portal = new JunctionKey(xNumerator / 3, yNumerator / 3);
            return true;
        }

        /// <summary>
        /// Rotates a build node by <paramref name="steps"/> hex symmetries about
        /// the lattice origin. The build basis is axial (one neighbour step is
        /// 0.5 wu at 30°/90°/150°), so +60° is the SAME (q,r) -> (-r, q+r) step
        /// the tile grid uses — which is why a plan's footprint, its modules and
        /// its walls can all be rotated by one formula instead of three.
        /// </summary>
        public static HexBuildNodeKey RotateNode(HexBuildNodeKey node, int steps)
        {
            var q = node.Q;
            var r = node.R;
            for (var i = 0; i < NormalizeSector(steps); i++)
            {
                var nextQ = -r;
                r = q + r;
                q = nextQ;
            }
            return new HexBuildNodeKey(q, r);
        }

        /// <summary>
        /// The navigation junctions that lie ON one unit build segment — the
        /// §120 obstacle rule for a raised wall/window/door section.
        ///
        /// Both lattices share one world frame and one axial basis: a build node
        /// (q,r) is the junction key (4q/3, (4q+8r)/3), so the three build
        /// directions ARE the three junction directions and a junction point
        /// falls every 3/4 of a build step along the line. A unit segment
        /// therefore carries one or two junctions and the only candidate
        /// parameters are the quarters t = 0, 1/4, 1/2, 3/4, 1 — which is why
        /// this walks node coordinates scaled by four and stays exact integer
        /// arithmetic. Blocking every junction on the line seals it: in a
        /// triangular lattice no edge crosses a lattice line without landing on
        /// a lattice point of that line.
        ///
        /// The endpoints are inclusive on purpose. Adjacent sections then share
        /// their seam junction, so three sections along one hex edge cover all
        /// five of its junctions with no gap — and a HALF-built edge keeps its
        /// real holes instead of pretending to be solid.
        /// </summary>
        public static IReadOnlyList<JunctionKey> SegmentJunctions(BuildSegmentKey segment)
        {
            var result = new List<JunctionKey>(2);
            if (!IsUnitSegment(segment)) return result;
            var dq = segment.B.Q - segment.A.Q;
            var dr = segment.B.R - segment.A.R;
            for (var quarter = 0; quarter <= 4; quarter++)
            {
                var scaledQ = 4 * segment.A.Q + quarter * dq;
                var scaledR = 4 * segment.A.R + quarter * dr;
                if (Mod3(scaledQ) != 0) continue;
                var scaledY = scaledQ + 2 * scaledR;
                if (Mod3(scaledY) != 0) continue;
                var key = new JunctionKey(scaledQ / 3, scaledY / 3);
                if (!result.Contains(key)) result.Add(key);
            }
            return result;
        }

        private static int Mod3(int value)
        {
            var remainder = value % 3;
            return remainder < 0 ? remainder + 3 : remainder;
        }

        public static bool AreSectorsAdjacent(FloorSectorKey left, FloorSectorKey right)
        {
            if (left == right) return false;
            var shared = new HashSet<BuildSegmentKey>(SectorBoundary(left));
            shared.IntersectWith(SectorBoundary(right));
            return shared.Count == SectionsPerHexEdge;
        }

        public static bool IsConnected(IEnumerable<FloorSectorKey> sectors)
        {
            var remaining = new HashSet<FloorSectorKey>(sectors);
            if (remaining.Count <= 1) return true;
            var queue = new Queue<FloorSectorKey>();
            var first = remaining.First();
            remaining.Remove(first);
            queue.Enqueue(first);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var adjacent = remaining.Where(candidate => AreSectorsAdjacent(current, candidate)).ToArray();
                foreach (var candidate in adjacent)
                {
                    remaining.Remove(candidate);
                    queue.Enqueue(candidate);
                }
            }
            return remaining.Count == 0;
        }

        public static bool Contains(FloorSectorKey sector, JunctionKey junction)
        {
            var point = JunctionToWorld(junction);
            var center = ToWorld(HexCenter(sector.Hex));
            var a = ToWorld(HexCorner(sector.Hex, sector.Sector));
            var b = ToWorld(HexCorner(sector.Hex, sector.Sector + 1));
            return PointInTriangle(point, center, a, b);
        }

        public static bool IsSupportedByFloor(
            JunctionKey junction, IEnumerable<FloorSectorKey> floors) =>
            floors.Any(sector => Contains(sector, junction));

        public static JunctionKey RotateJunctionOffset(JunctionKey offset, int yawStep)
        {
            var q = offset.XKey;
            var rNumerator = offset.YKey - offset.XKey;
            if ((rNumerator & 1) != 0)
                throw new ArgumentException($"Junction offset {offset} is outside the navigation lattice.");
            var r = rNumerator / 2;
            for (var i = 0; i < NormalizeSector(yawStep); i++)
            {
                var nextQ = -r;
                var nextR = q + r;
                q = nextQ;
                r = nextR;
            }
            return new JunctionKey(q, 2 * r + q);
        }

        private static bool PointInTriangle(Float2 point, Float2 a, Float2 b, Float2 c)
        {
            const float epsilon = 0.0001f;
            var d1 = Cross(point, a, b);
            var d2 = Cross(point, b, c);
            var d3 = Cross(point, c, a);
            var hasNegative = d1 < -epsilon || d2 < -epsilon || d3 < -epsilon;
            var hasPositive = d1 > epsilon || d2 > epsilon || d3 > epsilon;
            return !(hasNegative && hasPositive);
        }

        private static float Cross(Float2 point, Float2 a, Float2 b) =>
            (point.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (point.Y - b.Y);
    }
}
