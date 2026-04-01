using System;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Spatial
{

public static class HexSpatialMath
{
    public const float HexRadius = 1.5f;
    public const float Sqrt3 = 1.7320508f;
    public const float HexApothemFactor = Sqrt3 * 0.5f;
    public const float HexWidthFactor = Sqrt3;
    public const float HexRowStepFactor = 1.5f;
    public const float InteriorRingRadiusFactor = HexApothemFactor * 0.5f;

    public static float HexApothem => HexRadius * HexApothemFactor;

    public static float InteriorRingRadius => HexRadius * InteriorRingRadiusFactor;

    public static Float2 TileToWorld(TileCoord coord)
    {
        var x = HexRadius * HexWidthFactor * (coord.Q + coord.R * 0.5f);
        var y = HexRadius * HexRowStepFactor * coord.R;
        return new Float2(x, y);
    }

    public static Float2 PointToWorld(TileCoord tile, Float2 offset)
    {
        return TileToWorld(tile) + offset;
    }

    public static float Distance(Float2 a, Float2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    public static float AngleDegrees(Float2 direction)
    {
        return MathF.Atan2(direction.Y, direction.X) * (180f / MathF.PI);
    }

    public static Float2 Normalize(Float2 direction)
    {
        var length = Distance(Float2.Zero, direction);
        if (length <= 0.0001f)
        {
            return Float2.Zero;
        }

        return new Float2(direction.X / length, direction.Y / length);
    }
}

}
