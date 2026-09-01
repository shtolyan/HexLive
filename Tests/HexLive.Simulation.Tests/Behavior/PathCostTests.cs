using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §40.17 v2 — цена прыжка живёт на РЕБРЕ, а не на шов-узле, стоит по-разному
/// вверх и вниз, и поиск пути её честно минимизирует.
/// <para>
/// Что здесь ловится, по одному тесту на грабли:
/// </para>
/// <list type="bullet">
/// <item>предпосчёт при worldgen обязан совпадать с живым резолвером — иначе
/// маршрут и исполнение разойдутся в том, что считать прыжком (и это ЕДИНСТВЕННАЯ
/// проверка предпосчёта: молча прочитанный ноль снова сделал бы прыжки
/// бесплатными);</item>
/// <item>обход ВДОЛЬ уступа, лишь задевающий шов-узел, больше не платит как
/// прыжок — прежняя узловая цена штрафовала ровно тот маршрут, который должна
/// была поощрять;</item>
/// <item>прыжок вверх дороже прыжка вниз, потому что он и правда занимает вдвое
/// больше тиков;</item>
/// <item>штраф не превращается в запрет: когда обход реально длиннее, она
/// прыгает;</item>
/// <item>релаксация: узел, впервые найденный через дорогое ребро, обязан
/// подешеветь, когда до него доходит дешёвый путь.</item>
/// </list>
/// </summary>
public sealed class PathCostTests
{
    [Test]
    public void PrecomputedStepDeltasMatchTheLiveResolver()
    {
        var world = TestWorld.CreateWorld();
        var checkedEdges = 0;

        foreach (var junction in world.Junctions.Items.Values)
        {
            Assert.That(junction.NeighborStepDelta, Is.Not.Null,
                $"Junction {junction.Id.Value} без предпосчитанных перепадов: " +
                "BuildStepDeltas не прошёл по всему графу.");
            Assert.That(junction.NeighborStepDelta.Length, Is.EqualTo(junction.Neighbors.Count),
                $"Junction {junction.Id.Value}: массив перепадов разошёлся по длине " +
                "со списком соседей — индексы больше не параллельны.");

            for (var i = 0; i < junction.Neighbors.Count; i++)
            {
                var live = HexPathfinder.ResolveStepDelta(
                    world, junction.Id, junction.Neighbors[i]);
                Assert.That((int)junction.NeighborStepDelta[i], Is.EqualTo(live),
                    $"Ребро {junction.Id.Value}->{junction.Neighbors[i].Value}: " +
                    "предпосчёт разошёлся с живым резолвером.");
                checkedEdges++;
            }
        }

        Assert.That(checkedEdges, Is.GreaterThan(1000),
            "Проверено подозрительно мало рёбер — мир собрался не тот.");
    }

    [Test]
    public void TouchingASeamJunctionIsNotAJump()
    {
        var world = TestWorld.CreateWorld();
        var flatEdgesAtSeams = 0;
        var crossingEdgesAtSeams = 0;

        foreach (var seamId in world.ClimbSeams)
        {
            if (!world.Junctions.Items.TryGetValue(seamId, out var seam) ||
                seam.NeighborStepDelta is null)
            {
                continue;
            }

            for (var i = 0; i < seam.Neighbors.Count; i++)
            {
                if (seam.NeighborStepDelta[i] == 0)
                {
                    flatEdgesAtSeams++;
                }
                else
                {
                    crossingEdgesAtSeams++;
                }
            }
        }

        // Оба числа должны быть заметными: шов-узел граничит с ОБОИМИ уровнями,
        // поэтому часть его рёбер перепад пересекает, а часть идёт вдоль стены.
        // Прежняя узловая цена не различала их вообще.
        Assert.That(flatEdgesAtSeams, Is.GreaterThan(0),
            "У шов-узлов нет ни одного плоского ребра — тогда перенос цены на " +
            "ребро ничего бы не поменял, и тест ниже проверяет фантазию.");
        Assert.That(crossingEdgesAtSeams, Is.GreaterThan(0),
            "У шов-узлов нет ни одного ребра с перепадом — предпосчёт врёт.");
    }

    [Test]
    public void ReachabilityRejectsTheSameSeamWalkAsThePathfinder()
    {
        var world = TestWorld.CreateWorld(104729);
        JunctionId from = default;
        JunctionId to = default;
        var found = false;
        foreach (var seamId in world.ClimbSeams)
        {
            if (!world.Junctions.Items.TryGetValue(seamId, out var seam))
            {
                continue;
            }

            foreach (var neighbor in seam.Neighbors)
            {
                if (!world.ClimbSeams.Contains(neighbor))
                {
                    continue;
                }

                from = seamId;
                to = neighbor;
                found = true;
                break;
            }

            if (found)
            {
                break;
            }
        }

        Assert.That(found, Is.True, "Fixture needs one adjacent seam pair.");
        foreach (var junction in world.Junctions.Items.Values)
        {
            junction.Blocked = !junction.Id.Equals(from) && !junction.Id.Equals(to);
        }
        world.TopologyVersion++;

        Assert.Multiple(() =>
        {
            Assert.That(HexPathfinder.FindPath(world, from, to), Is.Empty,
                "Walking seam-to-seam is forbidden by the live router.");
            Assert.That(Connectivity.Reachable(world, from, to), Is.False,
                "Planning reachability must be a projection of the live router.");
        });
    }

    [Test]
    public void StepDeltasAreAntisymmetric()
    {
        var world = TestWorld.CreateWorld();

        foreach (var junction in world.Junctions.Items.Values)
        {
            for (var i = 0; i < junction.Neighbors.Count; i++)
            {
                var neighborId = junction.Neighbors[i];
                if (!world.Junctions.Items.TryGetValue(neighborId, out var neighbor))
                {
                    continue;
                }

                var back = neighbor.Neighbors.IndexOf(junction.Id);
                if (back < 0)
                {
                    continue; // односторонних рёбер в решётке нет, но не падаем на этом
                }

                Assert.That((int)neighbor.NeighborStepDelta[back],
                    Is.EqualTo(-(int)junction.NeighborStepDelta[i]),
                    $"Перепад {junction.Id.Value}->{neighborId.Value} не зеркалится " +
                    "обратным ребром: подъём с одной стороны обязан быть спуском с другой.");
            }
        }
    }

    [Test]
    public void WalkingAroundBeatsSawtoothing()
    {
        // Пила: один шаг вниз (25) и один вверх (45) = 70 против обхода в
        // четыре плоских ребра (40). Ровно то, на что жаловались: «спрыгнула,
        // запрыгнула, потеряла время, а можно было чуть обойти».
        var world = new SyntheticGraph()
            .Edge("S", "Sag", -1).Edge("Sag", "G", +1)
            .Chain("S", "G", 3)
            .Build();

        var path = HexPathfinder.FindPath(world, Id("S"), Id("G"), null);

        Assert.That(Names(path), Is.EqualTo(new[] { "S", "a1", "a2", "a3", "G" }),
            "Пила через два прыжка дороже обхода — маршрут обязан идти в обход.");
    }

    [Test]
    public void ButJumpsWhenTheDetourIsGenuinelyLonger()
    {
        // Тот же граф, но обход растянут до девяти плоских рёбер (90) — теперь
        // пила (70) действительно быстрее. Штраф остаётся ценой, а не запретом.
        var world = new SyntheticGraph()
            .Edge("S", "Sag", -1).Edge("Sag", "G", +1)
            .Chain("S", "G", 8)
            .Build();

        var path = HexPathfinder.FindPath(world, Id("S"), Id("G"), null);

        Assert.That(Names(path), Is.EqualTo(new[] { "S", "Sag", "G" }),
            "Когда обход длиннее, прыгать правильно — цена не должна быть запретом.");
    }

    [Test]
    public void DroppingIsCheaperThanClimbing()
    {
        // Две ветви одинаковой формы: одна начинается спуском, другая подъёмом.
        // Единственная разница — знак перепада, и она обязана решать.
        var world = new SyntheticGraph()
            .Edge("S", "Up", +1).Edge("Up", "G", 0)
            .Edge("S", "Down", -1).Edge("Down", "G", 0)
            .Build();

        var path = HexPathfinder.FindPath(world, Id("S"), Id("G"), null);

        Assert.That(Names(path), Is.EqualTo(new[] { "S", "Down", "G" }),
            "Спуску не нужен ни разбег, ни подъём собственного веса — он обязан " +
            "стоить меньше подъёма. §21.21B v23 сравнял ОКНА прыжка, и цены на " +
            "секунду сравнялись следом; предпочтение спуска от длительности " +
            "клипа не зависит и держится отдельным множителем.");
    }

    [Test]
    public void ANodeFirstReachedExpensivelyGetsRelaxed()
    {
        // X виден из S сразу, но через перепад (45). Дешёвый путь S->B->X стоит
        // 20 и находится ПОЗЖЕ. Старый поиск закрывал X при первом касании и
        // навсегда оставлял ему цену 45 — то есть возвращал маршрут дороже того,
        // который сам же мог построить.
        var world = new SyntheticGraph()
            .Edge("S", "X", +1)          // первым в списке соседей — и дорогой
            .Edge("S", "B", 0)
            .Edge("B", "X", 0)
            .Edge("X", "G", 0)
            .Build();

        var path = HexPathfinder.FindPath(world, Id("S"), Id("G"), null);

        Assert.That(Names(path), Is.EqualTo(new[] { "S", "B", "X", "G" }),
            "Дешёвый путь к X (10+10) обязан вытеснить дорогой (45) — это и есть " +
            "релаксация, которой в поиске не было.");
    }

    // ── синтетический граф ────────────────────────────────────────────────
    //
    // Дельты задаются РУКАМИ, а не через тайлы: тестируется функция стоимости,
    // и мир из двух хексов с нужными высотами был бы лишним слоем допущений.
    // Порядок Neighbors — порядок объявления рёбер, поэтому тест релаксации
    // может гарантировать «дорогое ребро найдено первым».
    private sealed class SyntheticGraph
    {
        private readonly List<(string from, string to, int delta)> _edges = new();

        public SyntheticGraph Edge(string from, string to, int delta)
        {
            _edges.Add((from, to, delta));
            return this;
        }

        // Плоская цепочка from -> a1 -> ... -> aN -> to (N промежуточных узлов).
        public SyntheticGraph Chain(string from, string to, int inner)
        {
            var previous = from;
            for (var i = 1; i <= inner; i++)
            {
                var node = $"a{i}";
                Edge(previous, node, 0);
                previous = node;
            }

            return Edge(previous, to, 0);
        }

        public WorldState Build()
        {
            var world = new WorldState();
            var names = new List<string>();
            foreach (var edge in _edges)
            {
                if (!names.Contains(edge.from)) { names.Add(edge.from); }
                if (!names.Contains(edge.to)) { names.Add(edge.to); }
            }

            foreach (var name in names)
            {
                world.Junctions.Items[Id(name)] = new Junction { Id = Id(name) };
            }

            var deltas = new Dictionary<JunctionId, List<sbyte>>();
            foreach (var name in names)
            {
                deltas[Id(name)] = new List<sbyte>();
            }

            void Link(string from, string to, int delta)
            {
                var junction = world.Junctions.Items[Id(from)];
                junction.Neighbors.Add(Id(to));
                deltas[Id(from)].Add((sbyte)delta);
            }

            foreach (var edge in _edges)
            {
                Link(edge.from, edge.to, edge.delta);
                Link(edge.to, edge.from, -edge.delta);
            }

            foreach (var name in names)
            {
                world.Junctions.Items[Id(name)].NeighborStepDelta = deltas[Id(name)].ToArray();
            }

            return world;
        }
    }

    private static JunctionId Id(string name) => new(Hash(name));

    // Стабильный положительный id из имени — читаемее, чем ручная нумерация.
    private static int Hash(string name)
    {
        var hash = 17;
        foreach (var c in name)
        {
            hash = hash * 31 + c;
        }

        return hash & 0x7FFFFFFF;
    }

    private static string[] Names(List<JunctionId> path)
    {
        var known = new[]
        {
            "S", "G", "Sag", "Up", "Down", "X", "B",
            "a1", "a2", "a3", "a4", "a5", "a6", "a7", "a8", "a9",
        };
        var result = new string[path.Count];
        for (var i = 0; i < path.Count; i++)
        {
            result[i] = "?" + path[i].Value;
            foreach (var name in known)
            {
                if (Id(name).Equals(path[i]))
                {
                    result[i] = name;
                    break;
                }
            }
        }

        return result;
    }
}

}
