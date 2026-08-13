using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// §135.5: поиск пути стал A* — и обязан остаться ОПТИМАЛЬНЫМ.
/// <para>
/// ⭐ Эвристика, завышающая остаток, не падает и не логируется: она просто
/// начинает возвращать маршруты дороже кратчайших, и мир едет по чуть-чуть
/// неправильным дорогам. Заметить это глазами нельзя — поэтому здесь оракул:
/// <see cref="HexPathfinder.FindCosts"/> остался ЧИСТОЙ Дейкстрой без
/// эвристики, считает по тем же правилам рёбер, и его цена до цели — это
/// определение кратчайшего. Цена маршрута от <c>FindPath</c> обязана совпасть
/// с ней ровно, а не «примерно».
/// </para>
/// <para>
/// Второй тест — про бюджет узлов: он обязан отвечать «дороги нет», а не
/// «вот путь подешевле». Молчаливый обрез маршрута был бы худшим из исходов.
/// </para>
/// </summary>
public sealed class PathfinderOptimalityGateTests
{
    [Test]
    public void AStarReturnsTheSameCostAsPlainDijkstra()
    {
        var world = TestWorld.CreateWorld();
        var walkable = world.Junctions.Items.Values
            .Where(j => !j.Blocked && !j.Door && j.Tiles.Count > 0)
            .OrderBy(j => j.Id.Value)
            .ToList();
        Assert.That(walkable.Count, Is.GreaterThan(200), "мир для теста подозрительно мал");

        // Пары раскиданы по всему острову детерминированно (шаг взаимно прост
        // с размером списка), чтобы выборка не села в один угол карты.
        var compared = 0;
        for (var i = 0; i < walkable.Count; i += 97)
        {
            var start = walkable[i];
            var goal = walkable[(i * 7 + 13) % walkable.Count];
            if (start.Id.Equals(goal.Id))
            {
                continue;
            }

            var path = HexPathfinder.FindPath(world, start.Id, goal.Id, null);
            var reference = HexPathfinder.FindCosts(
                world, start.Id, new[] { goal.Id }, null);

            if (!reference.TryGetValue(goal.Id, out var optimal))
            {
                Assert.That(path, Is.Empty,
                    $"Дейкстра говорит «недостижимо», а A* вернул путь: " +
                    $"{start.Id.Value} -> {goal.Id.Value}");
                continue;
            }

            Assert.That(path, Is.Not.Empty,
                $"путь есть по Дейкстре, но A* его не нашёл: " +
                $"{start.Id.Value} -> {goal.Id.Value}");
            Assert.That(PathCost(world, path), Is.EqualTo(optimal),
                $"A* вернул НЕ кратчайший маршрут {start.Id.Value} -> {goal.Id.Value} — " +
                "эвристика завышает остаток (обязана быть допустимой: " +
                "floor(расстояние / самое длинное ребро) × FlatCost)");
            compared++;
        }

        Assert.That(compared, Is.GreaterThan(2),
            $"сравнено всего {compared} пар — сломана выборка, а не патфайндер");
    }

    [Test]
    public void ExhaustedNodeBudgetReportsNoPathInsteadOfAShortcut()
    {
        var world = TestWorld.CreateWorld();
        var walkable = world.Junctions.Items.Values
            .Where(j => !j.Blocked && !j.Door && j.Tiles.Count > 0)
            .OrderBy(j => j.Id.Value)
            .ToList();

        // Самый ДОРОГОЙ из достижимых, а не просто геометрически дальний:
        // остров не односвязен (вода, скалы), и «дальний» легко оказывается
        // недостижимым — тогда тест меряет не бюджет, а связность.
        var start = walkable.First();
        var costs = HexPathfinder.FindCosts(
            world, start.Id, walkable.Select(j => j.Id).ToList(), null);
        var goal = costs
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key.Value)
            .First().Key;

        Assert.That(HexPathfinder.FindPath(world, start.Id, goal, null),
            Is.Not.Empty, "без бюджета дальний путь обязан находиться");

        // Один узел развёрнут — дальше потолок. Ответ «дороги нет»; вызывающий
        // (§29C.3) решит это по-своему, но обрезанного маршрута он не получит.
        var starved = HexPathfinder.FindPath(
            world, start.Id, goal, null, maxExpansions: 1);
        Assert.That(starved, Is.Empty,
            "исчерпанный бюджет обязан отвечать «дороги нет», а не коротким огрызком");
    }

    private static long PathCost(WorldState world, IReadOnlyList<JunctionId> path)
    {
        var total = 0L;
        for (var i = 1; i < path.Count; i++)
        {
            if (!world.Junctions.Items.TryGetValue(path[i - 1], out var from))
            {
                continue;
            }

            var index = from.Neighbors.IndexOf(path[i]);
            Assert.That(index, Is.GreaterThanOrEqualTo(0),
                $"шаг {path[i - 1].Value} -> {path[i].Value} не ребро графа");
            total += EdgeCost(world, from, index, path[i]);
        }

        return total;
    }

    // Зеркало HexPathfinder.ClimbCost (он приватный) при weightClimb: true.
    private static long EdgeCost(WorldState world, Junction from, int index, JunctionId to)
    {
        if (world.StraitJunctions.Contains(to)) return 20L;
        if (world.SwimJunctions.Contains(to)) return 40L;
        var delta = HexPathfinder.StepDelta(world, from, index, to);
        if (delta == 0) return 10L;
        return delta > 0 ? 36L : 24L;
    }
}

}
