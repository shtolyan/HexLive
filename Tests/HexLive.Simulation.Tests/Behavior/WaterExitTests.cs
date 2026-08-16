using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §40.18-C (баг #162). Вылезти из воды можно ВСЕГДА, пока человек в сознании:
/// берег в этом мире везде на ступень выше воды, поэтому общий запрет «без
/// прыжка вверх нельзя» превращал воду в ловушку в одну сторону для всякой,
/// кому повредили ноги.
/// </summary>
public sealed class WaterExitTests
{
    private static bool AllDry(HexLive.Simulation.Core.WorldState world, JunctionId id)
    {
        if (!world.Junctions.Items.TryGetValue(id, out var junction) || junction.Tiles.Count == 0)
        {
            return false;
        }

        foreach (var coord in junction.Tiles)
        {
            if (!world.Tiles.Items.TryGetValue(coord, out var tile) ||
                !tile.Flags.HasFlag(TileFlags.Walkable) ||
                tile.Flags.HasFlag(TileFlags.Water))
            {
                return false;
            }
        }

        return true;
    }

    [Test]
    public void EveryDeepWaterNodeHasADryExitWithoutJumping()
    {
        var world = TestWorld.CreateWorld(4711);
        var dry = world.Junctions.Items.Values
            .Where(junction => !junction.Blocked && AllDry(world, junction.Id))
            .Select(junction => junction.Id)
            .ToArray();
        Assert.That(dry, Is.Not.Empty);

        // Выборка, а не все 1200+ узлов: один мультицелевой поиск по всему
        // острову недёшев, а дыра была не точечной — она была всеобщей.
        var sample = world.SwimJunctions
            .Where(id => world.Junctions.Items.ContainsKey(id))
            .OrderBy(id => id.Value)
            .Take(40)
            .ToArray();
        Assert.That(sample, Is.Not.Empty, "в мире обязана быть глубокая вода");

        var trapped = new List<JunctionId>();
        foreach (var swim in sample)
        {
            if (HexPathfinder.FindCosts(world, swim, dry, null, true, canJump: false).Count == 0)
            {
                trapped.Add(swim);
            }
        }

        Assert.That(trapped, Is.Empty,
            "из воды обязан быть выход и без прыжка: " +
            string.Join(", ", trapped.Select(id => id.Value)));
    }

    [Test]
    public void TheExceptionIsDirected_WaterIsNotEnteredThroughIt()
    {
        var world = TestWorld.CreateWorld(4711);
        var checkedPairs = 0;
        // Береговые пары редки: большинство узлов воды — открытая вода, поэтому
        // ищем по всей воде и останавливаемся, набрав достаточную выборку.
        foreach (var swim in world.SwimJunctions.OrderBy(id => id.Value))
        {
            if (checkedPairs >= 30)
            {
                break;
            }

            if (!world.Junctions.Items.TryGetValue(swim, out var junction))
            {
                continue;
            }

            foreach (var neighborId in junction.Neighbors)
            {
                // Берег в этом мире почти всегда СМЕШАННЫЙ узел (земля + вода) —
                // полностью сухих соседей у глубокой воды не бывает вовсе, и
                // именно поэтому правило принимает «есть сухой ходибельный
                // тайл», а не «узел целиком сухой».
                if (world.SwimJunctions.Contains(neighborId) ||
                    world.StraitJunctions.Contains(neighborId) ||
                    !world.Junctions.Items.TryGetValue(neighborId, out var bank) ||
                    !bank.Tiles.Any(coord =>
                        world.Tiles.Items.TryGetValue(coord, out var tile) &&
                        tile.Flags.HasFlag(TileFlags.Walkable) &&
                        !tile.Flags.HasFlag(TileFlags.Water)))
                {
                    continue;
                }

                checkedPairs++;
                Assert.That(HexPathfinder.IsWaterExit(world, swim, neighborId), Is.True,
                    "шаг из воды на сушу — это выход");
                Assert.That(HexPathfinder.IsWaterExit(world, neighborId, swim), Is.False,
                    "обратный шаг — вход в воду, исключение на него не распространяется");
            }
        }

        Assert.That(checkedPairs, Is.GreaterThan(0), "фикстура обязана иметь берег");
    }

    [Test]
    public void ANoJumpRouteOutOfWaterNeverDivesBackIn()
    {
        var world = TestWorld.CreateWorld(4711);
        var dry = world.Junctions.Items.Values
            .Where(junction => !junction.Blocked && AllDry(world, junction.Id))
            .Select(junction => junction.Id)
            .ToArray();

        var swim = world.SwimJunctions.OrderBy(id => id.Value).First();
        var costs = HexPathfinder.FindCosts(world, swim, dry, null, true, canJump: false);
        Assert.That(costs, Is.Not.Empty, "выход обязан существовать");
        var goal = costs.OrderBy(pair => pair.Value).ThenBy(pair => pair.Key.Value).First().Key;

        var path = HexPathfinder.FindPath(world, swim, goal, null, true, canJump: false);
        Assert.That(path, Is.Not.Empty, "маршрут из воды обязан существовать");

        // Вышла — значит вышла: обратно в глубокую воду маршрут не возвращается.
        var left = false;
        for (var i = 0; i < path.Count; i++)
        {
            var inDeepWater = world.SwimJunctions.Contains(path[i]);
            if (!inDeepWater)
            {
                left = true;
                continue;
            }

            Assert.That(left, Is.False, $"маршрут ныряет обратно в воду на шаге {i}");
        }

        Assert.That(left, Is.True, "маршрут обязан довести до суши");
    }
}

}
