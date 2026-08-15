using System;
using HexLive.Simulation.Common;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §141: обратный переход «мировая точка → тайл». Вид рисует тело по
/// непрерывной позиции, а высоту и «в доме ли она» спрашивал у дискретного
/// поля <c>npc.Tile</c>, которое отстаёт от тела — отсюда проход сквозь порог.
/// Инверсия обязана быть точной, иначе лечение окажется хуже болезни.
/// </summary>
public sealed class WorldToTileTests
{
    [Test]
    public void CentreOfEveryTileReturnsThatTile()
    {
        for (var q = -12; q <= 12; q++)
        {
            for (var r = -12; r <= 12; r++)
            {
                var tile = new TileCoord(q, r);
                Assert.That(HexSpatialMath.WorldToTile(HexSpatialMath.TileToWorld(tile)),
                    Is.EqualTo(tile), $"Центр {q},{r} обязан вернуть сам себя.");
            }
        }
    }

    /// <summary>
    /// Любая точка ВНУТРИ гекса — это его точка. Берём апофему с запасом
    /// (0.95), чтобы не спорить о самом ребре: там ответ законно достаётся
    /// одному из двух соседей.
    /// </summary>
    [Test]
    public void AnyPointInsideTheHexResolvesToIt()
    {
        var inside = HexSpatialMath.HexApothem * 0.95f;
        for (var q = -8; q <= 8; q++)
        {
            for (var r = -8; r <= 8; r++)
            {
                var tile = new TileCoord(q, r);
                var centre = HexSpatialMath.TileToWorld(tile);
                for (var step = 0; step < 12; step++)
                {
                    var angle = step * (MathF.PI / 6f);
                    var probe = new Float2(
                        centre.X + MathF.Cos(angle) * inside,
                        centre.Y + MathF.Sin(angle) * inside);
                    Assert.That(HexSpatialMath.WorldToTile(probe), Is.EqualTo(tile),
                        $"Точка внутри {q},{r} под углом {step * 30}° уехала в чужой гекс.");
                }
            }
        }
    }

    /// <summary>
    /// ⭐ То, ради чего это заведено: шаг от центра к соседу переключает ответ
    /// РОВНО на середине пути, а не раньше и не позже. Именно этот момент вид
    /// теперь считает «она вошла».
    /// </summary>
    [Test]
    public void TheAnswerFlipsAtTheMidpointBetweenNeighbours()
    {
        var from = new TileCoord(0, 0);
        var to = new TileCoord(1, 0);
        var a = HexSpatialMath.TileToWorld(from);
        var b = HexSpatialMath.TileToWorld(to);

        Float2 Lerp(float t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

        Assert.That(HexSpatialMath.WorldToTile(Lerp(0.45f)), Is.EqualTo(from),
            "Не дошла до середины — всё ещё в своём гексе.");
        Assert.That(HexSpatialMath.WorldToTile(Lerp(0.55f)), Is.EqualTo(to),
            "Перешла середину — уже в соседнем.");
    }
}

}
