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

    /// <summary>
    /// §141: ОБРАТНЫЙ переход — в каком гексе лежит мировая точка. Точная
    /// инверсия <see cref="TileToWorld"/> плюс кубическое округление, поэтому
    /// центр тайла всегда возвращает сам тайл, а точка на ребре достаётся
    /// одному из двух соседей детерминированно.
    /// <para>
    /// Понадобился виду: он рисует тело по непрерывной <c>Position</c>, а
    /// высоту и «в доме ли она» спрашивал у дискретного <c>npc.Tile</c>. Поле
    /// отстаёт от тела (замер: в 99% переездов тело уже внутри нового гекса,
    /// когда тайл только щёлкает), и на пороге хижины это читалось как проход
    /// сквозь ступеньку. Симуляция этот метод не зовёт — ей дискретный тайл и
    /// нужен.
    /// </para>
    /// </summary>
    public static TileCoord WorldToTile(Float2 world)
    {
        var r = world.Y / (HexRadius * HexRowStepFactor);
        var q = world.X / (HexRadius * HexWidthFactor) - r * 0.5f;

        // Кубическое округление: восстанавливаем третью ось, округляем все
        // три и правим ту, что дальше всего уехала — иначе дробные координаты
        // у ребра садятся не в тот гекс.
        var cubeY = -q - r;
        var rq = MathF.Round(q);
        var rr = MathF.Round(r);
        var ry = MathF.Round(cubeY);

        var dq = MathF.Abs(rq - q);
        var dr = MathF.Abs(rr - r);
        var dy = MathF.Abs(ry - cubeY);

        if (dq > dr && dq > dy)
        {
            rq = -rr - ry;
        }
        else if (dr > dy)
        {
            rr = -rq - ry;
        }

        return new TileCoord((int)rq, (int)rr);
    }

    public static float Distance(Float2 a, Float2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    // Axial hex grid distance in tiles.
    public static int HexDistance(TileCoord a, TileCoord b)
    {
        var dq = a.Q - b.Q;
        var dr = a.R - b.R;
        return (Math.Abs(dq) + Math.Abs(dr) + Math.Abs(dq + dr)) / 2;
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
