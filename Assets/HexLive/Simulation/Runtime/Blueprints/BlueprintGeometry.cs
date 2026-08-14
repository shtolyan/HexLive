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

        public static IReadOnlyList<HexBuildNodeKey> RoofSupports(RoofSectorKey sector) =>
            new[]
            {
                HexCenter(sector.Hex),
                HexCorner(sector.Hex, sector.Sector),
                HexCorner(sector.Hex, sector.Sector + 1)
            };

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
