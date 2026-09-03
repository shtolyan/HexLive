using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §158.4: локальное перечисление обязано накрывать ВСЁ, что накрыл бы полный
/// обход с тем же радиусом, — иначе «ближайший подходящий узел» тихо
/// становится «ближайшим из тех, что попались». Сверка против полного обхода
/// на случайных точках и радиусах, плюс порядок по id и нижняя граница колец.
/// </summary>
public sealed class LocalSearchCoverageTests
{
    [Test]
    public void TileRadiusCoversEveryJunctionWithinWorldDistance()
    {
        var world = TestWorld.CreateWorld();
        var rng = new Random(1584);
        var all = world.Junctions.Items.Values.ToList();
        var collected = new List<Junction>();
        for (var trial = 0; trial < 120; trial++)
        {
            var origin = all[rng.Next(all.Count)];
            if (origin.Tiles.Count == 0)
            {
                continue;
            }

            // Точка отсчёта — где-то внутри тайла, как позиция NPC.
            var jitter = new Float2(
                (float)(rng.NextDouble() * 2 - 1) * HexSpatialMath.HexRadius * 0.5f,
                (float)(rng.NextDouble() * 2 - 1) * HexSpatialMath.HexRadius * 0.5f);
            var point = HexSpatialMath.TileToWorld(origin.Tiles[0]) + jitter;
            var distance = (float)(rng.NextDouble() * 20.0 + 0.3);

            var expected = all
                .Where(j => j.Tiles.Count > 0 && HexSpatialMath.Distance(j.WorldPosition, point) <= distance)
                .Select(j => j.Id).ToHashSet();
            LocalSearch.CollectWithinTiles(
                world, origin.Tiles[0], LocalSearch.TileRadiusCovering(distance), collected);
            var got = collected.Select(j => j.Id).ToHashSet();
            var missing = expected.Where(id => !got.Contains(id)).ToList();
            Assert.That(missing, Is.Empty,
                $"radius {distance:0.00} wu от тайла {origin.Tiles[0].Q},{origin.Tiles[0].R}: " +
                $"пропущено {missing.Count} узлов из {expected.Count}");

            for (var i = 1; i < collected.Count; i++)
            {
                Assert.That(collected[i].Id.Value, Is.GreaterThan(collected[i - 1].Id.Value),
                    "кандидаты обязаны идти по возрастанию id — это порядок прежнего обхода");
            }
        }
    }

    [Test]
    public void RingLowerBoundNeverOverestimates()
    {
        var world = TestWorld.CreateWorld();
        var rng = new Random(15841);
        var tiles = world.Tiles.Items.Keys.ToList();
        var seen = new HashSet<JunctionId>();
        var ring = new List<Junction>();
        for (var trial = 0; trial < 60; trial++)
        {
            var center = tiles[rng.Next(tiles.Count)];
            var point = HexSpatialMath.TileToWorld(center) + new Float2(
                (float)(rng.NextDouble() * 2 - 1) * HexSpatialMath.HexRadius * 0.6f,
                (float)(rng.NextDouble() * 2 - 1) * HexSpatialMath.HexRadius * 0.6f);
            seen.Clear();
            for (var k = 0; k < 6; k++)
            {
                LocalSearch.CollectRing(world, center, k, ring, seen);
                var bound = LocalSearch.RingLowerBound(k);
                foreach (var junction in ring)
                {
                    Assert.That(HexSpatialMath.Distance(junction.WorldPosition, point),
                        Is.GreaterThanOrEqualTo(bound - 1e-3f),
                        $"кольцо {k}: узел {junction.Id.Value} ближе нижней границы {bound:0.00}");
                }
            }
        }
    }
}

}
