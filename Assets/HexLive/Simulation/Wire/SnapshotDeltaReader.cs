using System;
using System.Collections.Generic;
using System.IO;
using HexLive.Simulation.Debug;

namespace HexLive.Simulation.Wire
{

/// <summary>
/// Applies a delta onto the receiver's mirror of the world.
/// <para>
/// The mirror is a long-lived <see cref="WorldSnapshot"/> whose entity lists are
/// kept in ascending-id order — the same order the exporter produces. Entities
/// are found by binary search on that id, never by position: the sender's list
/// order and the receiver's share no contract beyond "sorted by id", and objects
/// spawn and despawn constantly.
/// </para>
/// </summary>
public static class SnapshotDeltaReader
{
    /// <summary>
    /// Applies one delta frame. Throws <see cref="InvalidDataException"/> if the
    /// frame was built against a different baseline than the mirror holds — the
    /// caller answers that by asking for a keyframe, never by applying it anyway.
    /// </summary>
    public static void Apply(BinaryReader r, WorldSnapshot into, int mirrorTick)
    {
        var version = r.ReadInt32();
        if (version != WorldSnapshotCodec.WireVersion)
        {
            throw new InvalidDataException(
                $"Delta wire version {version}, expected {WorldSnapshotCodec.WireVersion}.");
        }

        var includeDebugDetails = r.ReadBoolean();
        var baselineTick = r.ReadInt32();
        if (baselineTick != mirrorTick)
        {
            throw new InvalidDataException(
                $"Delta was built against tick {baselineTick}, the mirror holds {mirrorTick} — " +
                "the chain is broken and a keyframe is required.");
        }

        var tick = r.ReadInt32();

        if (r.ReadBoolean())
        {
            var length = r.ReadInt32();
            var bytes = r.ReadBytes(length);
            using var stream = new MemoryStream(bytes);
            using var reader = new BinaryReader(stream);
            WorldSnapshotCodec.ReadHeaderRecord(reader, into);
        }

        // Тайлы едут только когда изменились (§83.2 r13). Ни одного байта здесь
        // не значит «набор тот же», а НЕ «тайлов нет» — зеркало держит своё.
        if (r.ReadBoolean())
        {
            var length = r.ReadInt32();
            if (length < 0 || length > 1_000_000)
            {
                throw new InvalidDataException($"Delta tile block of {length} bytes is not plausible.");
            }

            var bytes = r.ReadBytes(length);
            using var tileStream = new MemoryStream(bytes);
            using var tileReader = new BinaryReader(tileStream);
            WorldSnapshotCodec.ReadTiles(tileReader, into);
        }

        // §148: разведка — по тому же правилу «прислали, только если изменилось».
        if (r.ReadBoolean())
        {
            var length = r.ReadInt32();
            if (length < 0 || length > 1_000_000)
            {
                throw new InvalidDataException(
                    $"Delta explored block of {length} bytes is not plausible.");
            }

            var bytes = r.ReadBytes(length);
            using var exploredStream = new MemoryStream(bytes);
            using var exploredReader = new BinaryReader(exploredStream);
            WorldSnapshotCodec.ReadExplored(exploredReader, into);
        }

        ApplySection(r, into.Objects, o => o.Id.Value,
            (reader, o) => WorldSnapshotCodec.ReadObjectRecord(reader, o));

        ApplySection(r, into.Npcs, n => n.Id.Value,
            (reader, n) => WorldSnapshotCodec.ReadNpcRecord(reader, n, includeDebugDetails));

        ApplySection(r, into.Corpses, n => n.Id.Value,
            (reader, n) => WorldSnapshotCodec.ReadNpcRecord(reader, n, includeDebugDetails));

        ApplySection(r, into.Mobs, m => m.Id,
            (reader, m) => WorldSnapshotCodec.ReadMobRecord(reader, m));

        ApplySection(r, into.Crabs, c => c.Id,
            (reader, c) => WorldSnapshotCodec.ReadCrabRecord(reader, c));

        ApplySection(r, into.Sharks, s => s.Id,
            (reader, s) => WorldSnapshotCodec.ReadSharkRecord(reader, s));

        ApplySection(r, into.MobSlots, s => s.SlotId,
            (reader, s) => WorldSnapshotCodec.ReadMobSlotRecord(reader, s));

        // §136: дневники — той же секционной механикой; порядок обязан
        // совпадать с SnapshotDelta.Encode, иначе кадр не сойдётся на маркере.
        ApplySection(r, into.Journals, j => j.NpcId,
            (reader, j) => WorldSnapshotCodec.ReadJournalRecord(reader, j));

        var appended = r.ReadUInt16();
        if (appended > into.DeathRecords.Count)
        {
            // A full resend (the sender's list shrank, e.g. a save restore).
            into.DeathRecords.Clear();
        }

        for (var i = 0; i < appended; i++)
        {
            var record = new DeathRecordSnapshot();
            WorldSnapshotCodec.ReadDeathRecordRecord(r, record);
            into.DeathRecords.Add(record);
        }

        var marker = r.ReadInt32();
        if (marker != SnapshotDeltaEncoder.EndMarker)
        {
            throw new InvalidDataException(
                "Delta frame did not end where it should — reader and writer disagree about the layout.");
        }

        // Tick last: a throw above must leave the mirror's tick pointing at the
        // state it actually holds, so the keyframe request names the right baseline.
        into.Tick = tick;
    }

    private static void ApplySection<T>(BinaryReader r, List<T> list, Func<T, int> idOf,
        Action<BinaryReader, T> readRecord) where T : new()
    {
        var removed = r.ReadUInt16();
        for (var i = 0; i < removed; i++)
        {
            var id = r.ReadInt32();
            var at = IndexOf(list, idOf, id);
            if (at >= 0)
            {
                list.RemoveAt(at);
            }
        }

        var upserts = r.ReadUInt16();
        for (var i = 0; i < upserts; i++)
        {
            var id = r.ReadInt32();
            var length = r.ReadInt32();
            // A record bigger than this is not a record, it is corruption —
            // fail loudly before ReadBytes tries to allocate it.
            if (length < 0 || length > 1_000_000)
            {
                throw new InvalidDataException($"Delta record of {length} bytes is not plausible.");
            }

            var bytes = r.ReadBytes(length);

            using var stream = new MemoryStream(bytes);
            using var reader = new BinaryReader(stream);

            var at = IndexOf(list, idOf, id);
            if (at >= 0)
            {
                readRecord(reader, list[at]);
                continue;
            }

            // New to us. Insert at the sorted position so the list stays in the
            // one order both ends agree on.
            var item = new T();
            readRecord(reader, item);
            list.Insert(~at, item);
        }
    }

    /// <summary>
    /// Binary search by id. Returns the index, or the bitwise complement of the
    /// insertion point when absent — the same convention as
    /// <c>List.BinarySearch</c>, so <c>~result</c> is where a new entity goes.
    /// </summary>
    private static int IndexOf<T>(List<T> list, Func<T, int> idOf, int id)
    {
        var low = 0;
        var high = list.Count - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var midId = idOf(list[mid]);
            if (midId == id)
            {
                return mid;
            }

            if (midId < id)
            {
                low = mid + 1;
                continue;
            }

            high = mid - 1;
        }

        return ~low;
    }
}

}
