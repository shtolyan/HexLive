using System;
using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Spatial
{
    public static class HexPointLayout
    {
        public const int InteriorRadius = 3;
        public const int BoundaryRadius = 4;

        private static readonly float PointGridRadiusFactor = 1f / (BoundaryRadius * HexSpatialMath.Sqrt3);

        private static readonly IReadOnlyList<JunctionTemplate> InteriorTemplates = BuildInteriorTemplates();
        private static readonly IReadOnlyList<JunctionTemplate> BoundaryTemplates = BuildBoundaryTemplates();

        /// <summary>
        /// The 6 neighbor offsets in (xKey, yKey) space for the flat-top sub-grid.
        /// </summary>
        public static readonly (int dx, int dy)[] NeighborKeyOffsets =
        {
            (+1, +1),
            (-1, -1),
            (0, +2),
            (0, -2),
            (+1, -1),
            (-1, +1)
        };

        public static IReadOnlyList<JunctionTemplate> GetInteriorTemplates()
        {
            return InteriorTemplates;
        }

        public static IReadOnlyList<JunctionTemplate> GetBoundaryTemplates()
        {
            return BoundaryTemplates;
        }

        private static IReadOnlyList<JunctionTemplate> BuildInteriorTemplates()
        {
            var templates = new List<JunctionTemplate>();
            var slot = 0;
            for (var r = -InteriorRadius; r <= InteriorRadius; r++)
            {
                var qMin = Math.Max(-InteriorRadius, -r - InteriorRadius);
                var qMax = Math.Min(InteriorRadius, -r + InteriorRadius);
                for (var q = qMin; q <= qMax; q++)
                {
                    var axial = new AxialPoint(q, r);
                    templates.Add(new JunctionTemplate(slot, ToLocalOffset(axial), axial));
                    slot++;
                }
            }

            return templates;
        }

        private static IReadOnlyList<JunctionTemplate> BuildBoundaryTemplates()
        {
            var templates = new List<JunctionTemplate>(BoundaryRadius * 6);
            var slot = 0;
            var ring = BuildRing(BoundaryRadius);
            foreach (var axial in ring)
            {
                templates.Add(new JunctionTemplate(slot++, ToLocalOffset(axial), axial));
            }

            return templates;
        }

        public static Float2 ToLocalOffset(AxialPoint axial)
        {
            var pointGridRadius = HexSpatialMath.HexRadius * PointGridRadiusFactor;
            var x = pointGridRadius * 1.5f * axial.Q;
            var y = pointGridRadius * HexSpatialMath.Sqrt3 * (axial.R + (axial.Q * 0.5f));
            return new Float2(x, y);
        }

        public static (int xKey, int yKey) GetJunctionKeyPair(TileCoord tile, AxialPoint subAxial)
        {
            var xKey = 2 * BoundaryRadius * tile.Q + BoundaryRadius * tile.R + subAxial.Q;
            var yKey = 3 * BoundaryRadius * tile.R + 2 * subAxial.R + subAxial.Q;
            return (xKey, yKey);
        }

        private static IReadOnlyList<AxialPoint> BuildRing(int radius)
        {
            if (radius <= 0)
            {
                return new[] { new AxialPoint(0, 0) };
            }

            var results = new List<AxialPoint>(radius * 6);
            var directions = new[]
            {
                new AxialPoint(1, 0),
                new AxialPoint(1, -1),
                new AxialPoint(0, -1),
                new AxialPoint(-1, 0),
                new AxialPoint(-1, 1),
                new AxialPoint(0, 1)
            };

            var current = new AxialPoint(-radius, radius);
            for (var side = 0; side < directions.Length; side++)
            {
                for (var step = 0; step < radius; step++)
                {
                    results.Add(current);
                    current = new AxialPoint(current.Q + directions[side].Q, current.R + directions[side].R);
                }
            }

            return results;
        }
    }

    public readonly struct JunctionTemplate
    {
        public JunctionTemplate(int slot, Float2 offset, AxialPoint subAxial)
        {
            Slot = slot;
            Offset = offset;
            SubAxial = subAxial;
        }

        public int Slot { get; }

        public Float2 Offset { get; }

        public AxialPoint SubAxial { get; }
    }

    public readonly struct AxialPoint
    {
        public AxialPoint(int q, int r)
        {
            Q = q;
            R = r;
        }

        public int Q { get; }

        public int R { get; }
    }
}
