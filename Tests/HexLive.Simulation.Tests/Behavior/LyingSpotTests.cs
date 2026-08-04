using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §113 «лечь рядом, а не внутрь». Гекс с костром/валуном не запрещён для
/// лежания — запрещена сама вещь: тело ищет свободное место той же шеренги
/// (§29G r3) и при нужде разворачивается по осям гекса.
///
/// <para>
/// Арена, а не соак, ровно по причине из CLAUDE.md: вопрос здесь «дошло ли до
/// дела вообще» (легла ли она мимо огня), а не «сколько раз за восемь дней».
/// </para>
/// </summary>
public sealed class LyingSpotTests
{
    private static NPCState Girl(WorldState world) => world.Entities.Npcs.Values.First();

    // Вещь ровно в центре её гекса — так их и ставит §66 (одна постройка на
    // гекс, в середине).
    private static WorldObjectState SpawnAtCentre(WorldState world, NPCState girl, string definitionId)
    {
        var centre = StructurePlacement.CenterJunction(world, girl.Tile);
        Assert.That(centre, Is.Not.Null, "У гекса должен быть центральный узел.");
        return WorldObjectMutations.SpawnObject(
            world, definitionId, girl.Fragment, girl.Tile, centre.Value);
    }

    private static float DistanceToBody(WorldObjectState thing, WorldState world, NPCState girl)
    {
        Assert.That(LyingSpot.TryAnchor(world, thing, out var anchor), Is.True);
        var d = anchor - girl.Position;
        var radians = girl.RotationDegrees * (System.MathF.PI / 180f);
        var forward = new Float2(System.MathF.Cos(radians), System.MathF.Sin(radians));
        var lateral = new Float2(-forward.Y, forward.X);

        // Зазор от вещи до ПРЯМОУГОЛЬНИКА тела (та же метрика, что у решателя):
        // сколько ещё осталось до ближайшего борта. Отрицательное = задела.
        var along = System.MathF.Abs(d.X * forward.X + d.Y * forward.Y) - LyingSpot.BodyHalfLength;
        var side = System.MathF.Abs(d.X * lateral.X + d.Y * lateral.Y) - LyingSpot.BodyHalfWidth;
        return System.MathF.Max(along, side);
    }

    /// <summary>Пустой гекс — ровно геометрический центр, как до §113.</summary>
    [Test]
    public void EmptyHex_SheStillLiesOnTheExactCentre()
    {
        var world = TestWorld.CreateWorld();
        var girl = Girl(world);
        foreach (var other in world.Entities.Npcs.Values.Where(n => !n.Id.Equals(girl.Id)).ToList())
        {
            other.Tile = new TileCoord(girl.Tile.Q + 4, girl.Tile.R); // не мешать шеренгой
        }

        ExecutionSystem.LieDownCentered(world, girl);

        var centre = HexSpatialMath.TileToWorld(girl.Tile);
        Assert.That(HexSpatialMath.Distance(girl.Position, centre), Is.LessThan(0.001f),
            "Одинокое тело на пустом гексе ложится в центр — инвариант §60.2a.");
    }

    /// <summary>
    /// ⭐ Ядро §113: она вырубилась НА гексе костра — и лежит рядом с огнём,
    /// на том же гексе, а не в нём.
    /// </summary>
    [Test]
    public void Campfire_SheLiesBesideTheFire_NotInIt()
    {
        var world = TestWorld.CreateWorld();
        var girl = Girl(world);
        foreach (var other in world.Entities.Npcs.Values.Where(n => !n.Id.Equals(girl.Id)).ToList())
        {
            other.Tile = new TileCoord(girl.Tile.Q + 4, girl.Tile.R);
        }

        var fire = SpawnAtCentre(world, girl, "campfire.spot");
        var tile = girl.Tile;

        ExecutionSystem.LieDownCentered(world, girl);

        Assert.That(girl.Tile, Is.EqualTo(tile),
            "Гекс костра не запрещён — с него не уходят.");
        Assert.That(DistanceToBody(fire, world, girl), Is.GreaterThan(0f),
            "⭐ Тело не задевает огонь: место в центре занято костром, легла сбоку.");
        Assert.That(HexSpatialMath.Distance(girl.Position, HexSpatialMath.TileToWorld(tile)),
            Is.LessThan(HexSpatialMath.HexRadius),
            "…но всё ещё внутри своего гекса, а не за кромкой.");
        Assert.That(world.Events.Items.Any(e => e.Type == "LieDownBerth" && e.Message.Contains("Fit=Clear")),
            Is.True, "Решатель обязан отчитаться, что место найдено чистым.");
    }

    /// <summary>Валун — та же логика: не обходим гекс, обходим глыбу.</summary>
    [Test]
    public void Boulder_SheLiesBesideIt()
    {
        var world = TestWorld.CreateWorld();
        var girl = Girl(world);
        foreach (var other in world.Entities.Npcs.Values.Where(n => !n.Id.Equals(girl.Id)).ToList())
        {
            other.Tile = new TileCoord(girl.Tile.Q + 4, girl.Tile.R);
        }

        var boulder = SpawnAtCentre(world, girl, "rock.boulder");

        ExecutionSystem.LieDownCentered(world, girl);

        Assert.That(DistanceToBody(boulder, world, girl), Is.GreaterThan(0f),
            "Валун объявлен Obstacle без габарита — работает пол LieSolidRadiusFloorFactor.");
    }

    /// <summary>
    /// Выключатель возвращает поведение до §113 — тело ложится в костёр.
    /// Ручка существует ровно ради такого сравнения (и ради бисекта соаком).
    /// </summary>
    [Test]
    public void KillSwitchOff_SheLiesStraightIntoTheFire()
    {
        var world = TestWorld.CreateWorld();
        var girl = Girl(world);
        foreach (var other in world.Entities.Npcs.Values.Where(n => !n.Id.Equals(girl.Id)).ToList())
        {
            other.Tile = new TileCoord(girl.Tile.Q + 4, girl.Tile.R);
        }

        var fire = SpawnAtCentre(world, girl, "campfire.spot");
        var was = Spec49.LieAroundObstacles;
        try
        {
            Spec49.LieAroundObstacles = false;
            ExecutionSystem.LieDownCentered(world, girl);
        }
        finally
        {
            Spec49.LieAroundObstacles = was;
        }

        Assert.That(DistanceToBody(fire, world, girl), Is.LessThanOrEqualTo(0f),
            "С выключенной ручкой центр снова «свободен» — ровно поведение §29G r3.");
    }

    /// <summary>
    /// §29G r3 не сломан: две легли на один гекс — параллельно, на разных
    /// местах, а не одна в другой.
    /// </summary>
    [Test]
    public void TwoBodies_StillLieParallelInSeparateBerths()
    {
        var world = TestWorld.CreateWorld();
        var all = world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).ToList();
        Assert.That(all.Count, Is.GreaterThanOrEqualTo(2));
        var first = all[0];
        var second = all[1];
        foreach (var other in all.Skip(2))
        {
            other.Tile = new TileCoord(first.Tile.Q + 4, first.Tile.R);
        }

        second.Tile = first.Tile;
        first.Mind.FaintedUntilTick = world.Tick + 100;  // лежит — значит держит место
        second.Mind.FaintedUntilTick = world.Tick + 100;

        ExecutionSystem.LieDownCentered(world, first);
        ExecutionSystem.LieDownCentered(world, second);

        Assert.That(second.RotationDegrees, Is.EqualTo(first.RotationDegrees).Within(0.01f),
            "Курс у шеренги общий — задаёт первая легшая.");
        Assert.That(HexSpatialMath.Distance(first.Position, second.Position),
            Is.GreaterThan(LyingSpot.BerthSpacing * 0.9f),
            "Вторая легла НА СОСЕДНЕЕ место, а не в первую.");
    }
}

}
