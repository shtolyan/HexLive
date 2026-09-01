using System.Collections.Generic;
using System.IO;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §156 фаза 1: решётка чанков, активный набор и окно догона. Поведение мира
/// эти тесты не трогают — механика выключена, и главное здесь именно то, что
/// при выключенном тумблере всё отвечает «как раньше».
/// </summary>
public sealed class ChunkGeometryTests
{
    /// <summary>
    /// Усечённое деление склеило бы Q ∈ [-7..7] в один чанк вдвое шире прочих —
    /// и ровно на нулевом меридиане, где стоит стартовый лагерь.
    /// </summary>
    [Test]
    public void FloorDivRoundsTowardsMinusInfinity()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ChunkMath.FloorDiv(0, 8), Is.EqualTo(0));
            Assert.That(ChunkMath.FloorDiv(7, 8), Is.EqualTo(0));
            Assert.That(ChunkMath.FloorDiv(8, 8), Is.EqualTo(1));
            Assert.That(ChunkMath.FloorDiv(-1, 8), Is.EqualTo(-1));
            Assert.That(ChunkMath.FloorDiv(-8, 8), Is.EqualTo(-1));
            Assert.That(ChunkMath.FloorDiv(-9, 8), Is.EqualTo(-2));
        });
    }

    /// <summary>Каждый чанк накрывает РОВНО сторону тайлов, включая отрицательные.</summary>
    [Test]
    public void EveryChunkCoversExactlyChunkSizeTilesPerAxis()
    {
        var size = ChunkBalance.ChunkSizeTiles;
        var counts = new Dictionary<ChunkCoord, int>();
        for (var q = -3 * size; q < 3 * size; q++)
        {
            for (var r = -3 * size; r < 3 * size; r++)
            {
                var chunk = ChunkMath.ChunkOf(new TileCoord(q, r));
                counts.TryGetValue(chunk, out var seen);
                counts[chunk] = seen + 1;
            }
        }

        Assert.That(counts, Is.Not.Empty);
        foreach (var pair in counts)
        {
            Assert.That(pair.Value, Is.EqualTo(size * size),
                $"чанк {pair.Key} накрыл {pair.Value} тайлов вместо {size * size}");
        }
    }

    /// <summary>
    /// Активный набор обязан накрыть ВЕСЬ диск пробуждения каждой NPC: чанк,
    /// оставшийся спящим внутри чьего-то радиуса, — это ровно то враньё, ради
    /// запрета которого узаконено пере-покрытие по углам.
    /// </summary>
    [Test]
    public void ActiveSetCoversEveryTileWithinEachNpcWakeRadius()
    {
        var previous = ChunkBalance.ChunkSleepEnabled;
        ChunkBalance.ChunkSleepEnabled = true;
        try
        {
            var world = TestWorld.CreateEngine().World;
            ChunkMath.RebuildActiveChunks(world);

            foreach (var npc in world.Entities.Npcs.Values)
            {
                if (npc.Health <= 0f)
                {
                    continue;
                }

                var radius = ChunkMath.WakeRadiusTiles(npc);
                for (var dq = -radius; dq <= radius; dq++)
                {
                    for (var dr = -radius; dr <= radius; dr++)
                    {
                        var tile = new TileCoord(npc.Tile.Q + dq, npc.Tile.R + dr);
                        if (HexSpatialMath.HexDistance(npc.Tile, tile) > radius)
                        {
                            continue;
                        }

                        Assert.That(ChunkMath.IsAwake(world, tile), Is.True,
                            $"тайл {tile} в {radius} гексах от NPC {npc.Id.Value} остался спящим");
                    }
                }
            }
        }
        finally
        {
            ChunkBalance.ChunkSleepEnabled = previous;
        }
    }

    /// <summary>
    /// ⭐ Самая опасная деталь §156.6. При выключенной механике штампов нет, и
    /// наивный возврат нуля заставил бы формулы догона интегрировать погоду с
    /// сотворения мира — то есть наполнил бы бутылку из ничего на первом же
    /// такте. Окно обязано быть ровно одним slow-тактом.
    /// </summary>
    [Test]
    public void SleepWindowIsOneSlowTickWhileChunkSleepIsOff()
    {
        Assert.That(ChunkBalance.ChunkSleepEnabled, Is.False,
            "тумблер §156 включается отдельным коммитом с пересъёмом эталонов");

        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        world.Tick = 4096;

        var start = ChunkMath.SleepWindowStart(world, new TileCoord(999, -999));
        Assert.That(world.SlowIntervalTicks, Is.EqualTo(engine.Settings.SlowInterval));
        Assert.That(world.Tick - start, Is.EqualTo(world.SlowIntervalTicks));
    }

    /// <summary>Штамп ставится только на активные чанки и только при включённой механике.</summary>
    [Test]
    public void StampRecordsCurrentTickForActiveChunksOnly()
    {
        var previous = ChunkBalance.ChunkSleepEnabled;
        ChunkBalance.ChunkSleepEnabled = true;
        try
        {
            var world = TestWorld.CreateEngine().World;
            world.Tick = 640;
            ChunkMath.RebuildActiveChunks(world);
            ChunkMath.StampSimulated(world);

            Assert.That(world.Chunks.Items, Is.Not.Empty);
            Assert.That(world.Chunks.Items.Count,
                Is.EqualTo(world.Caches.ActiveChunks.Count));
            foreach (var pair in world.Chunks.Items)
            {
                Assert.That(pair.Value.LastSimulatedTick, Is.EqualTo(640));
            }

            // Далёкий чанк не получил записи — окно там начинается с нуля мира.
            var far = new TileCoord(4096, 4096);
            Assert.That(ChunkMath.IsAwake(world, far), Is.False);
            Assert.That(ChunkMath.SleepWindowStart(world, far), Is.Zero);
        }
        finally
        {
            ChunkBalance.ChunkSleepEnabled = previous;
        }
    }

    /// <summary>Карта чанков переживает сейв — иначе весь остров «спал с нуля».</summary>
    [Test]
    public void ChunkMapSurvivesSaveRoundTrip()
    {
        var world = TestWorld.CreateWorld(4242);
        world.Chunks.Items[new ChunkCoord(2, -3)] = new ChunkState { LastSimulatedTick = 512 };
        world.Chunks.Items[new ChunkCoord(-1, 0)] = new ChunkState { LastSimulatedTick = 96 };

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        stream.Position = 0;
        var loaded = TestWorld.CreateWorld(4242);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        WorldSaveSerializer.Read(loaded, reader);

        Assert.Multiple(() =>
        {
            Assert.That(loaded.Chunks.Items.Count, Is.EqualTo(2));
            Assert.That(loaded.Chunks.Items[new ChunkCoord(2, -3)].LastSimulatedTick,
                Is.EqualTo(512));
            Assert.That(loaded.Chunks.Items[new ChunkCoord(-1, 0)].LastSimulatedTick,
                Is.EqualTo(96));
        });
    }

    /// <summary>
    /// Байты сейва не зависят от порядка словаря: две карты с одинаковым
    /// содержимым, наполненные в разном порядке, обязаны дать одинаковый блоб.
    /// </summary>
    [Test]
    public void ChunkMapBytesDoNotDependOnDictionaryOrder()
    {
        var forward = TestWorld.CreateWorld(77);
        var backward = TestWorld.CreateWorld(77);
        var coords = new[]
        {
            new ChunkCoord(-2, 5), new ChunkCoord(0, 0),
            new ChunkCoord(3, -1), new ChunkCoord(3, -9),
        };

        for (var i = 0; i < coords.Length; i++)
        {
            forward.Chunks.Items[coords[i]] = new ChunkState { LastSimulatedTick = 16 * (i + 1) };
        }

        for (var i = coords.Length - 1; i >= 0; i--)
        {
            backward.Chunks.Items[coords[i]] = new ChunkState { LastSimulatedTick = 16 * (i + 1) };
        }

        Assert.That(Blob(backward), Is.EqualTo(Blob(forward)));
    }

    private static byte[] Blob(WorldState world)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        return stream.ToArray();
    }
}

}
