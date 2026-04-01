using System;
using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Spatial
{
    public static class HexPointLayout
    {
        private const int InteriorRadius = 2;
        private const int ConnectionRadius = 3;
        private const float PointGridRadiusFactor = 1f / (3f * HexSpatialMath.Sqrt3);

        private static readonly IReadOnlyList<HexPointTemplate> InteriorTemplates = BuildInteriorTemplates();
        private static readonly IReadOnlyList<ConnectionPointTemplate> ConnectionTemplates = BuildConnectionTemplates();

        public static IReadOnlyList<HexPointTemplate> GetInteriorTemplates()
        {
            return InteriorTemplates;
        }

        public static IReadOnlyList<ConnectionPointTemplate> GetConnectionTemplates()
        {
            return ConnectionTemplates;
        }

        private static IReadOnlyList<HexPointTemplate> BuildInteriorTemplates()
        {
            var templates = new List<HexPointTemplate>();
            var slot = 0;
            for (var r = -InteriorRadius; r <= InteriorRadius; r++)
            {
                var qMin = Math.Max(-InteriorRadius, -r - InteriorRadius);
                var qMax = Math.Min(InteriorRadius, -r + InteriorRadius);
                for (var q = qMin; q <= qMax; q++)
                {
                    var axial = new AxialPoint(q, r);
                    templates.Add(new HexPointTemplate(
                        slot,
                        GetInteriorRole(slot),
                        ToLocalOffset(axial)));
                    slot++;
                }
            }

            return templates;
        }

        private static IReadOnlyList<ConnectionPointTemplate> BuildConnectionTemplates()
        {
            var templates = new List<ConnectionPointTemplate>(ConnectionRadius * 6);
            var slot = 0;
            var ring = BuildRing(ConnectionRadius);
            foreach (var axial in ring)
            {
                templates.Add(new ConnectionPointTemplate(
                    slot++,
                    PointRole.Access,
                    ProjectToHexBoundary(ToLocalOffset(axial))));
            }

            return templates;
        }

        private static PointRole GetInteriorRole(int slot)
        {
            switch (slot)
            {
                case 1:
                case 6:
                    return PointRole.Item;
                case 7:
                    return PointRole.Observe;
                case 12:
                    return PointRole.Sit;
                case 17:
                    return PointRole.Sleep;
                default:
                    return PointRole.Access;
            }
        }

        private static Float2 ToLocalOffset(AxialPoint axial)
        {
            var pointGridRadius = HexSpatialMath.HexRadius * PointGridRadiusFactor;
            var x = pointGridRadius * 1.5f * axial.Q;
            var y = pointGridRadius * HexSpatialMath.Sqrt3 * (axial.R + (axial.Q * 0.5f));
            return new Float2(x, y);
        }

        private static Float2 ProjectToHexBoundary(Float2 offset)
        {
            var direction = HexSpatialMath.Normalize(offset);
            if (Math.Abs(direction.X) <= 0.0001f && Math.Abs(direction.Y) <= 0.0001f)
            {
                return offset;
            }

            var boundaryDistance = HexSpatialMath.HexApothem / GetMaxHexNormalProjection(direction);
            return direction * boundaryDistance;
        }

        private static float GetMaxHexNormalProjection(Float2 direction)
        {
            var max = float.MinValue;
            for (var i = 0; i < 6; i++)
            {
                var angle = (float)Math.PI / 3f * i;
                var normal = new Float2((float)Math.Cos(angle), (float)Math.Sin(angle));
                var projection = (direction.X * normal.X) + (direction.Y * normal.Y);
                if (projection > max)
                {
                    max = projection;
                }
            }

            return max;
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

    public readonly struct HexPointTemplate
    {
        public HexPointTemplate(int slot, PointRole role, Float2 offset)
        {
            Slot = slot;
            Role = role;
            Offset = offset;
        }

        public int Slot { get; }

        public PointRole Role { get; }

        public Float2 Offset { get; }
    }

    public readonly struct ConnectionPointTemplate
    {
        public ConnectionPointTemplate(int slot, PointRole role, Float2 offset)
        {
            Slot = slot;
            Role = role;
            Offset = offset;
        }

        public int Slot { get; }

        public PointRole Role { get; }

        public Float2 Offset { get; }
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
