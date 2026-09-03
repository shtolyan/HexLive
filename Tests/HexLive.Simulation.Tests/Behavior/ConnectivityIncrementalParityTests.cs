using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §158.3: инкрементальная связность обязана отвечать бит-в-бит так же, как
/// карта, построенная с нуля. Идентификаторы компонент разные — сравниваются
/// ОТВЕТЫ: достижимость пар (оба графа), размер плоской компоненты, размер
/// мира без прыжка, «дотянусь ли до материка», принадлежность материку.
/// Последовательности: случайные блокировки/разблокировки в открытом поле,
/// запечатывание тайла кольцом соседей (раскол на закрытую область и
/// обратное слияние) и стена поперёк острова (две большие половины —
/// запасной ход полной перестройки).
/// </summary>
public sealed class ConnectivityIncrementalParityTests
{
    private sealed record Answers(
        bool[] Jump, bool[] Flat, int[] FlatSize, int[] FlatWorld, bool[] Mainland, bool[] OnMainland);

    private static Answers Ask(WorldState world, List<(JunctionId a, JunctionId b)> pairs, List<JunctionId> singles)
    {
        var jump = new bool[pairs.Count];
        var flat = new bool[pairs.Count];
        for (var i = 0; i < pairs.Count; i++)
        {
            jump[i] = Connectivity.Reachable(world, pairs[i].a, pairs[i].b, canJump: true);
            flat[i] = Connectivity.Reachable(world, pairs[i].a, pairs[i].b, canJump: false);
        }

        var size = new int[singles.Count];
        var worldSize = new int[singles.Count];
        var mainland = new bool[singles.Count];
        var onMainland = new bool[singles.Count];
        for (var i = 0; i < singles.Count; i++)
        {
            size[i] = Connectivity.FlatComponentSizeAt(world, singles[i]);
            worldSize[i] = Connectivity.FlatWorldSizeAt(world, singles[i]);
            mainland[i] = Connectivity.FlatReachesMainland(world, singles[i]);
            onMainland[i] = Connectivity.ComponentOf(world, singles[i], canJump: false) ==
                world.LargestFlatComponentId;
        }

        return new Answers(jump, flat, size, worldSize, mainland, onMainland);
    }

    private static void AssertSame(Answers incremental, Answers rebuilt, string stage)
    {
        Assert.Multiple(() =>
        {
            Assert.That(incremental.Jump, Is.EqualTo(rebuilt.Jump), $"{stage}: достижимость (прыжковый граф)");
            Assert.That(incremental.Flat, Is.EqualTo(rebuilt.Flat), $"{stage}: достижимость (плоский граф)");
            Assert.That(incremental.FlatSize, Is.EqualTo(rebuilt.FlatSize), $"{stage}: размер плоской компоненты");
            Assert.That(incremental.FlatWorld, Is.EqualTo(rebuilt.FlatWorld), $"{stage}: размер мира без прыжка");
            Assert.That(incremental.Mainland, Is.EqualTo(rebuilt.Mainland), $"{stage}: дотянусь ли до материка");
            Assert.That(incremental.OnMainland, Is.EqualTo(rebuilt.OnMainland), $"{stage}: принадлежность материку");
        });
    }

    private static List<Junction> Passable(WorldState world) =>
        world.Junctions.Items.Values.Where(j => !j.Blocked && j.Tiles.Count > 0).ToList();

    private static (List<(JunctionId, JunctionId)> pairs, List<JunctionId> singles) Samples(
        WorldState world, Random rng, int count)
    {
        var all = world.Junctions.Items.Keys.ToList();
        var pairs = new List<(JunctionId, JunctionId)>();
        var singles = new List<JunctionId>();
        for (var i = 0; i < count; i++)
        {
            pairs.Add((all[rng.Next(all.Count)], all[rng.Next(all.Count)]));
            singles.Add(all[rng.Next(all.Count)]);
        }

        return (pairs, singles);
    }

    /// <summary>Сверка: ответы инкрементальной карты против карты с нуля
    /// (InvalidateAll заставляет следующий вопрос перестроить обе).</summary>
    private static void Check(WorldState world, List<(JunctionId, JunctionId)> pairs, List<JunctionId> singles, string stage)
    {
        var incremental = Ask(world, pairs, singles);
        WorldTopology.InvalidateAll(world);
        var rebuilt = Ask(world, pairs, singles);
        AssertSame(incremental, rebuilt, stage);
    }

    [Test]
    public void RandomBlockingMatchesFullRebuild()
    {
        var world = TestWorld.CreateWorld();
        var rng = new Random(158);
        var (pairs, singles) = Samples(world, rng, 400);
        Ask(world, pairs, singles); // первое построение

        var blocked = new List<Junction>();
        for (var round = 0; round < 12; round++)
        {
            var passable = Passable(world);
            for (var i = 0; i < 25; i++)
            {
                var victim = passable[rng.Next(passable.Count)];
                if (!victim.Blocked)
                {
                    WorldTopology.SetBlocked(world, victim, true);
                    blocked.Add(victim);
                }
            }

            for (var i = 0; i < 10 && blocked.Count > 0; i++)
            {
                var index = rng.Next(blocked.Count);
                WorldTopology.SetBlocked(world, blocked[index], false);
                blocked.RemoveAt(index);
            }

            Check(world, pairs, singles, $"раунд {round}");
        }
    }

    [Test]
    public void SealingATileSplitsAndReopeningMergesExactly()
    {
        var world = TestWorld.CreateWorld();
        var rng = new Random(1583);
        var (pairs, singles) = Samples(world, rng, 300);

        var npc = world.Entities.Npcs.Values.First();
        var center = npc.Tile;
        var interior = world.Tiles.Items[center].Junctions
            .Select(id => world.Junctions.Items[id]).Where(j => j.Tiles.Count == 1).ToList();
        Assert.That(interior, Is.Not.Empty, "у тайла нет внутренних узлов — мир собрался не тот");

        // Внутренние узлы тайла — в выборке: именно они уходят в закрытую область.
        foreach (var j in interior)
        {
            singles.Add(j.Id);
            pairs.Add((j.Id, interior[0].Id));
            pairs.Add((j.Id, pairs[0].Item1));
        }

        Ask(world, pairs, singles);

        var ring = new List<Junction>();
        foreach (var direction in HexDirection.All)
        {
            var coord = new TileCoord(center.Q + direction.DQ, center.R + direction.DR);
            if (!world.Tiles.Items.TryGetValue(coord, out var tile))
            {
                continue;
            }

            foreach (var id in tile.Junctions)
            {
                var junction = world.Junctions.Items[id];
                if (!junction.Blocked && !junction.Tiles.Contains(center))
                {
                    ring.Add(junction);
                }
            }
        }

        // Первое кольцо — соседние тайлы целиком; второе — ребро между центром
        // и соседями, чтобы область запечаталась наверняка.
        foreach (var junction in ring)
        {
            WorldTopology.SetBlocked(world, junction, true);
        }

        var edge = world.Tiles.Items[center].Junctions
            .Select(id => world.Junctions.Items[id]).Where(j => j.Tiles.Count > 1 && !j.Blocked).ToList();
        foreach (var junction in edge)
        {
            WorldTopology.SetBlocked(world, junction, true);
        }

        var fullBefore = world.Caches.ConnectivityFullRebuilds;
        Check(world, pairs, singles, "тайл запечатан");
        Assert.That(Connectivity.Reachable(world, interior[0].Id, pairs[0].Item1, canJump: true), Is.False,
            "запечатанный тайл остался достижим снаружи");

        foreach (var junction in edge)
        {
            WorldTopology.SetBlocked(world, junction, false);
        }

        foreach (var junction in ring)
        {
            WorldTopology.SetBlocked(world, junction, false);
        }

        Check(world, pairs, singles, "тайл распечатан");
        Assert.That(Connectivity.Reachable(world, interior[0].Id, pairs[0].Item1, canJump: true),
            Is.EqualTo(Connectivity.Reachable(world, pairs[0].Item1, interior[0].Id, canJump: true)));
    }

    [Test]
    public void WallAcrossTheIslandFallsBackToAFullRebuildWithoutLying()
    {
        var world = TestWorld.CreateWorld();
        var rng = new Random(15833);
        var (pairs, singles) = Samples(world, rng, 300);
        Ask(world, pairs, singles);
        var before = world.Caches.ConnectivityFullRebuilds;

        // Стена: все узлы в полосе по X шириной в шаг суб-сетки — две большие
        // половины, локально не разрешить.
        var xs = world.Junctions.Items.Values.Select(j => j.WorldPosition.X).OrderBy(x => x).ToList();
        var mid = xs[xs.Count / 2];
        var wall = world.Junctions.Items.Values
            .Where(j => !j.Blocked && MathF.Abs(j.WorldPosition.X - mid) <= 0.4f).ToList();
        foreach (var junction in wall)
        {
            WorldTopology.SetBlocked(world, junction, true);
        }

        Check(world, pairs, singles, "стена поперёк");
        Assert.That(world.Caches.ConnectivityFullRebuilds, Is.GreaterThan(before),
            "две большие половины должны были уйти в полную перестройку — а ушли куда?");

        foreach (var junction in wall)
        {
            WorldTopology.SetBlocked(world, junction, false);
        }

        Check(world, pairs, singles, "стена снесена");
    }

    [Test]
    public void OpenFieldBlockingNeverRebuildsFully()
    {
        var world = TestWorld.CreateWorld();
        var rng = new Random(7);
        var (pairs, singles) = Samples(world, rng, 50);
        Ask(world, pairs, singles);
        var before = world.Caches.ConnectivityFullRebuilds;
        var npc = world.Entities.Npcs.Values.First();
        var around = world.Tiles.Items[npc.Tile].Junctions.Select(id => world.Junctions.Items[id]).Where(j => !j.Blocked).Take(6).ToList();
        foreach (var junction in around)
        {
            WorldTopology.SetBlocked(world, junction, true);
            Ask(world, pairs, singles);
            WorldTopology.SetBlocked(world, junction, false);
            Ask(world, pairs, singles);
        }

        Assert.That(world.Caches.ConnectivityFullRebuilds, Is.EqualTo(before),
            "бревно посреди поляны не имеет права ронять карту связности целиком");
    }
}

}
