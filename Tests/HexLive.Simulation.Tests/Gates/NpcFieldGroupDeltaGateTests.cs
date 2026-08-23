using System.Collections.Generic;
using System.IO;
using System.Text;
using HexLive.Simulation.Common;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>
/// §83.2 r12: запись колонистки едет группами полей, и дельта шлёт только те,
/// чьи байты изменились.
/// <para>
/// Цена ошибки здесь выше обычной: забытая группа портит зеркало не на кадр, а
/// до следующего ключевого — то есть колонистка может минуту стоять в чужой
/// одежде или с чужим здоровьем, и никакой ошибки при этом не будет. Поэтому
/// главная проверка тут не «размер упал», а «зеркало побайтово равно оригиналу»
/// на настоящей игре, где группы меняются вразнобой.
/// </para>
/// </summary>
public sealed class NpcFieldGroupDeltaGateTests
{
    [TestCase(12345)]
    [TestCase(4242)]
    [TestCase(872812195)]
    public void MirrorStaysByteIdenticalOverRealPlay(int seed)
    {
        var engine = TestWorld.CreateEngine(seed);
        var encoder = new SnapshotDeltaEncoder();
        var mirror = new WorldSnapshot();
        var mirrorTick = -1;

        // Тайлы по проводу не едут: клиент строит их из сида, а совпадение
        // доказывает контрольная сумма топологии (§83.3). Зеркалу здесь их надо
        // выдать ровно так же, иначе тест поймает СВОЮ недостачу, а не чужую.
        foreach (var tile in WorldSnapshotExporter.Export(engine.World).Tiles)
        {
            mirror.Tiles.Add(new TileSnapshot
            {
                Coord = tile.Coord,
                Elevation = tile.Elevation,
                Walkable = tile.Walkable,
                Blocked = tile.Blocked,
                Indoor = tile.Indoor,
                Water = tile.Water,
                HasFloor = tile.HasFloor,
            });
        }

        for (var i = 0; i < 200; i++)
        {
            engine.Step();
            var source = WorldSnapshotExporter.Export(engine.World);

            encoder.EnsureBaselineValid(source);
            byte[] frame;
            var keyframe = encoder.BaselineTick < 0;
            if (keyframe)
            {
                frame = Encode(w => WorldSnapshotCodec.Write(source, w, false));
                encoder.Encode(source, false); // базовая линия догоняет ключевой кадр
            }
            else
            {
                frame = encoder.Encode(source, false);
            }

            using (var stream = new MemoryStream(frame))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (keyframe)
                {
                    WorldSnapshotCodec.Read(reader, mirror);
                }
                else
                {
                    SnapshotDeltaReader.Apply(reader, mirror, mirrorTick);
                }
            }

            mirrorTick = mirror.Tick;

            var sourceBytes = Encode(w => WorldSnapshotCodec.Write(source, w, false));
            var mirrorBytes = Encode(w => WorldSnapshotCodec.Write(mirror, w, false));
            if (!Same(sourceBytes, mirrorBytes))
            {
                Assert.Fail($"сид {seed}, тик {source.Tick}: зеркало разошлось с оригиналом " +
                            $"({sourceBytes.Length} B против {mirrorBytes.Length} B) — " +
                            "какая-то группа полей не доехала, и это уже не лечится до ключевого кадра");
            }
        }
    }

    /// <summary>
    /// Ради чего всё затевалось: шаг колонистки везёт позицию, а не рюкзак.
    /// Проверяется по маске групп в самом кадре, а не по размеру, — размер
    /// зависит от мира, а маска говорит ровно то, что мы утверждаем.
    /// </summary>
    [Test]
    public void MovingCarriesOnlyTheTransformGroup()
    {
        var snapshot = OneNpc();
        var encoder = new SnapshotDeltaEncoder();
        encoder.Encode(snapshot, false); // первый кадр — вся запись

        snapshot.Tick++;
        snapshot.Npcs[0].Position = new Float2(3.5f, 4.25f);
        var mask = NpcMaskOf(encoder.Encode(snapshot, false));

        Assert.That(mask, Is.EqualTo(1 << WorldSnapshotCodec.NpcGroup.Transform),
            "шаг должен везти только группу Transform");
    }

    [Test]
    public void ChangingWornStateCarriesOnlyTheWearGroup()
    {
        var snapshot = OneNpc();
        var encoder = new SnapshotDeltaEncoder();
        encoder.Encode(snapshot, false);

        snapshot.Tick++;
        snapshot.Npcs[0].WornDirtiness[0] = "0.91";
        var mask = NpcMaskOf(encoder.Encode(snapshot, false));

        Assert.That(mask, Is.EqualTo(1 << WorldSnapshotCodec.NpcGroup.Wear));
    }

    [Test]
    public void ChangingGarmentOwnerCarriesOnlyItsPhysicalItemGroup()
    {
        var snapshot = OneNpc();
        var encoder = new SnapshotDeltaEncoder();
        encoder.Encode(snapshot, false);

        snapshot.Tick++;
        snapshot.Npcs[0].WornOwnerIds[0] = 42;
        var wornMask = NpcMaskOf(encoder.Encode(snapshot, false));
        Assert.That(wornMask, Is.EqualTo(1 << WorldSnapshotCodec.NpcGroup.Wear));

        snapshot.Tick++;
        snapshot.Npcs[0].InventoryOwnerIds[0] = 84;
        var carriedMask = NpcMaskOf(encoder.Encode(snapshot, false));
        Assert.That(carriedMask, Is.EqualTo(1 << WorldSnapshotCodec.NpcGroup.Inventory));
    }

    [Test]
    public void AnUnchangedColonistCostsNothingAtAll()
    {
        var snapshot = OneNpc();
        var encoder = new SnapshotDeltaEncoder();
        encoder.Encode(snapshot, false);

        snapshot.Tick++;
        Assert.That(NpcUpsertCount(encoder.Encode(snapshot, false)), Is.Zero,
            "неизменившаяся колонистка не должна занимать ни байта");
    }

    /// <summary>
    /// §83.2 r13: набор runtime-тайлов меняется, когда достроен настил, — раз в
    /// несколько игровых часов, — а ехал каждым кадром: 22% дельты после разреза
    /// записи NPC. Теперь за флагом; неизменный набор стоит один байт.
    /// </summary>
    [Test]
    public void UnchangedTilesCostOneByteAndAChangedSetStillArrives()
    {
        var snapshot = OneNpc();
        var coord = new TileCoord(2, 3);
        snapshot.Tiles.Add(new TileSnapshot { Coord = coord, Walkable = true, Elevation = 1, Indoor = true });

        var encoder = new SnapshotDeltaEncoder();
        var mirror = new WorldSnapshot();
        mirror.Tiles.Add(new TileSnapshot { Coord = coord, Walkable = true, Elevation = 1 });
        ApplyDelta(encoder.Encode(snapshot, false), mirror, -1);
        Assert.That(mirror.Tiles[0].Indoor, Is.True, "первый кадр обязан привезти набор целиком");

        snapshot.Tick++;
        var quiet = encoder.Encode(snapshot, false);
        Assert.That(TilesRide(quiet), Is.False, "неизменившийся набор тайлов не должен ехать вовсе");

        // Применяем и его: цепочка дельт неразрывна, пропущенное звено ловится
        // читателем — что он и сделал, когда этого вызова здесь не было.
        ApplyDelta(quiet, mirror, mirror.Tick);
        Assert.That(mirror.Tiles[0].Indoor, Is.True, "молчание про тайлы не должно гасить флаг зеркала");

        snapshot.Tick++;
        snapshot.Tiles[0].HasFloor = true;
        var withFloor = encoder.Encode(snapshot, false);
        Assert.That(TilesRide(withFloor), Is.True, "достроенный настил обязан доехать в тот же тик");

        ApplyDelta(withFloor, mirror, mirror.Tick);
        Assert.That(mirror.Tiles[0].HasFloor, Is.True);
    }

    // ── вспомогательное ───────────────────────────────────────────────────

    private static void ApplyDelta(byte[] bytes, WorldSnapshot mirror, int mirrorTick)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        SnapshotDeltaReader.Apply(reader, mirror, mirrorTick);
    }

    /// <summary>Есть ли в кадре блок тайлов.</summary>
    private static bool TilesRide(byte[] delta)
    {
        using var stream = new MemoryStream(delta);
        using var r = new BinaryReader(stream, Encoding.UTF8);
        r.ReadInt32();
        r.ReadBoolean();
        r.ReadInt32();
        r.ReadInt32();
        if (r.ReadBoolean())
        {
            r.ReadBytes(r.ReadInt32());
        }

        return r.ReadBoolean();
    }

    private static WorldSnapshot OneNpc()
    {
        var snapshot = new WorldSnapshot { Tick = 1 };
        var npc = new NpcSnapshot { Id = new EntityId(1), DisplayName = "npc.mira.name" };
        npc.WornItems.Add("wear.top");
        npc.WornOwnerIds.Add(1);
        npc.WornDirtiness.Add("0.10");
        npc.InventoryItems.Add("tool.knife");
        npc.InventoryOwnerIds.Add(1);
        npc.Attributes.Add("Strength:7");
        snapshot.Npcs.Add(npc);
        return snapshot;
    }

    /// <summary>Маска групп первой записи NPC в дельте.</summary>
    private static int NpcMaskOf(byte[] delta) => ReadNpcSection(delta, out var mask) > 0 ? mask : 0;

    private static int NpcUpsertCount(byte[] delta) => ReadNpcSection(delta, out _);

    /// <summary>
    /// Доходит по кадру до секции NPC — тем же порядком, каким его пишет
    /// <c>SnapshotDeltaEncoder.Encode</c>.
    /// </summary>
    private static int ReadNpcSection(byte[] delta, out int firstMask)
    {
        firstMask = 0;
        using var stream = new MemoryStream(delta);
        using var r = new BinaryReader(stream, Encoding.UTF8);

        r.ReadInt32();   // wire version
        r.ReadBoolean(); // includeDebugDetails
        r.ReadInt32();   // baseline tick
        r.ReadInt32();   // tick

        if (r.ReadBoolean())
        {
            r.ReadBytes(r.ReadInt32()); // header block
        }

        if (r.ReadBoolean())
        {
            r.ReadBytes(r.ReadInt32()); // tile block
        }

        if (r.ReadBoolean())
        {
            r.ReadBytes(r.ReadInt32()); // §148: explored block
        }

        SkipSection(r); // objects

        var gone = r.ReadUInt16();
        r.ReadBytes(gone * 4);

        var upserts = r.ReadUInt16();
        if (upserts > 0)
        {
            r.ReadInt32();              // id
            r.ReadInt32();              // record length
            firstMask = r.ReadUInt16(); // маска групп
        }

        return upserts;
    }

    private static void SkipSection(BinaryReader r)
    {
        var gone = r.ReadUInt16();
        r.ReadBytes(gone * 4);
        var upserts = r.ReadUInt16();
        for (var i = 0; i < upserts; i++)
        {
            r.ReadInt32();
            r.ReadBytes(r.ReadInt32());
        }
    }

    private static byte[] Encode(System.Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            write(writer);
            writer.Flush();
        }

        return stream.ToArray();
    }

    private static bool Same(IReadOnlyList<byte> a, IReadOnlyList<byte> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }
}
