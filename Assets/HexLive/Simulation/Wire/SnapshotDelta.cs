using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HexLive.Simulation.Debug;

namespace HexLive.Simulation.Wire
{

/// <summary>
/// Sends only what moved since the last frame.
/// <para>
/// About 218 objects live in a world and, on a typical tick, none of them change:
/// no fast-layer system walks the object table, and a hundred of them (boulders,
/// loose stones, yucca) are touched by nothing at all until a colonist picks them
/// up. Re-sending all of that four times a second is most of the bandwidth.
/// </para>
/// <para>
/// <b>The load-bearing decision: this encoder never looks at a field.</b> It asks
/// the ordinary record writer — the SAME one a keyframe uses — for an entity's
/// bytes, and compares them to the bytes it sent last time. Equal means unchanged.
/// </para>
/// <para>
/// That is not an optimisation, it is the safety property. A hand-written
/// "did this change?" check would be a second place to forget a field, and a
/// forgotten field in a delta does not corrupt one frame the way it would in a
/// full snapshot — it corrupts the receiver's state until the next keyframe, which
/// may be a minute away. With byte comparison there is exactly one place a field
/// can go missing, and it is the place the reflection coverage gate already
/// watches.
/// </para>
/// </summary>
public sealed class SnapshotDeltaEncoder
{
    /// <summary>Bytes last sent for each entity, keyed by id. The baseline.</summary>
    private readonly Dictionary<int, byte[]> _objects = new();

    /// <summary>
    /// §83.2 r12: у колонистки базовая линия хранится ПО ГРУППАМ ПОЛЕЙ — один
    /// блоб со всеми группами подряд и границы между ними. Сравнение идёт
    /// диапазонами, поэтому шаг колонистки везёт двадцать байт позиции, а не
    /// три с половиной килобайта вместе с рюкзаком и навыками.
    /// </summary>
    private readonly Dictionary<int, NpcBaseline> _npcs = new();

    // §28.15C v3: тела. Дешевле всех остальных секций: труп не двигается и не
    // меняется, поэтому стоит один апсерт в кадре появления — и ноль байт
    // навсегда после. Ровно ради этого дельта и сравнивает БАЙТЫ, а не поля.
    private readonly Dictionary<int, NpcBaseline> _corpses = new();

    private sealed class NpcBaseline
    {
        public byte[] Blob = Array.Empty<byte>();

        /// <summary>Смещение КОНЦА каждой группы внутри <see cref="Blob"/>.</summary>
        public readonly int[] Ends = new int[WorldSnapshotCodec.NpcGroup.Count];
    }
    private readonly Dictionary<int, byte[]> _mobs = new();
    private readonly Dictionary<int, byte[]> _crabs = new();
    private readonly Dictionary<int, byte[]> _sharks = new();
    private readonly Dictionary<int, byte[]> _mobSlots = new();

    // §136: дневники. Ради этого словаря секция и отделена от записи NPC —
    // дневник меняется раз в игровой час (1000 тиков), а колонистка шевелится
    // каждый. Внутри её записи он уезжал бы 4 раза в секунду; здесь после
    // первого кадра это ноль байт до следующей записи.
    private readonly Dictionary<int, byte[]> _journals = new();

    private byte[] _header = Array.Empty<byte>();
    private byte[] _tiles = Array.Empty<byte>();
    private int _deaths;

    private readonly MemoryStream _scratch = new();
    private readonly MemoryStream _npcScratch = new();
    private readonly int[] _groupEnds = new int[WorldSnapshotCodec.NpcGroup.Count];
    private readonly List<int> _gone = new();
    private readonly HashSet<int> _present = new();

    private bool _includeDebugDetails;

    /// <summary>The tick the baseline holds; -1 before the first keyframe.</summary>
    public int BaselineTick { get; private set; } = -1;

    /// <summary>
    /// Throws the baseline away. The next frame must be a keyframe — used when a
    /// viewer reconnects, when the debug-detail flag flips (it changes which
    /// fields exist), or when anything at all is in doubt.
    /// </summary>
    public void Reset()
    {
        _objects.Clear();
        _npcs.Clear();
        _corpses.Clear();
        _mobs.Clear();
        _crabs.Clear();
        _sharks.Clear();
        _mobSlots.Clear();
        _journals.Clear();
        _header = Array.Empty<byte>();
        _tiles = Array.Empty<byte>();
        _deaths = 0;
        BaselineTick = -1;
    }

    /// <summary>
    /// Detects states a delta cannot describe and resets the baseline so the
    /// caller sends a keyframe instead. Today that is one case: the death list
    /// SHRANK (a save restore rebuilt it). The wire encodes deaths as
    /// append-only, and "resend all" on top of a mirror that still holds the old
    /// list would duplicate every entry — starting over is the only honest move.
    /// Call before consulting <see cref="BaselineTick"/> to pick a frame kind.
    /// </summary>
    public void EnsureBaselineValid(WorldSnapshot snapshot)
    {
        if (BaselineTick >= 0 && snapshot.DeathRecords.Count < _deaths)
        {
            Reset();
        }
    }

    /// <summary>
    /// Builds a delta against the current baseline and advances it.
    /// <paramref name="includeDebugDetails"/> must match what the receiver
    /// expects; a change resets the baseline, because it changes the byte layout
    /// of every NPC record.
    /// </summary>
    public byte[] Encode(WorldSnapshot snapshot, bool includeDebugDetails)
    {
        if (BaselineTick < 0 || includeDebugDetails != _includeDebugDetails)
        {
            Reset();
            _includeDebugDetails = includeDebugDetails;
        }

        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Encoding.UTF8);

        w.Write(WorldSnapshotCodec.WireVersion);
        w.Write(includeDebugDetails);
        w.Write(BaselineTick);
        w.Write(snapshot.Tick);

        // The header is small and mostly changes together (clock, weather, sun),
        // so it is replaced whole when any of it moved rather than field-masked.
        var header = Capture(sw => WorldSnapshotCodec.WriteHeaderRecord(sw, snapshot));
        var headerChanged = !Same(_header, header);
        w.Write(headerChanged);
        if (headerChanged)
        {
            w.Write(header.Length);
            w.Write(header);
            _header = header;
        }

        // Architectural floors are few, but their tile state is mutable, so the
        // wire carries the COMPLETE runtime tile set — that is what lets the
        // grass vanish on the exact construction tick and lets a restore clear a
        // flag the mirror still holds.
        //
        // Complete, but not every frame. It changes when the colony finishes a
        // floor, i.e. once in several game hours, and it was riding four times a
        // second: measured at 22% of what a delta cost after the NPC record was
        // split (~260 B a frame). Same trick as the header above, and the same
        // reason it is safe — the bytes come from the ordinary tile writer, so
        // nothing here knows what a tile field is.
        var tiles = Capture(sw => WorldSnapshotCodec.WriteTiles(snapshot, sw));
        var tilesChanged = !Same(_tiles, tiles);
        w.Write(tilesChanged);
        if (tilesChanged)
        {
            w.Write(tiles.Length);
            w.Write(tiles);
            _tiles = tiles;
        }

        WriteSection(w, snapshot.Objects, _objects,
            (o) => o.Id.Value, (sw, o) => WorldSnapshotCodec.WriteObjectRecord(sw, o));

        WriteNpcSection(w, snapshot.Npcs, _npcs, includeDebugDetails);
        WriteNpcSection(w, snapshot.Corpses, _corpses, includeDebugDetails);

        WriteSection(w, snapshot.Mobs, _mobs,
            (m) => m.Id, (sw, m) => WorldSnapshotCodec.WriteMobRecord(sw, m));

        WriteSection(w, snapshot.Crabs, _crabs,
            (c) => c.Id, (sw, c) => WorldSnapshotCodec.WriteCrabRecord(sw, c));

        WriteSection(w, snapshot.Sharks, _sharks,
            (s) => s.Id, (sw, s) => WorldSnapshotCodec.WriteSharkRecord(sw, s));

        WriteSection(w, snapshot.MobSlots, _mobSlots,
            (s) => s.SlotId, (sw, s) => WorldSnapshotCodec.WriteMobSlotRecord(sw, s));

        WriteSection(w, snapshot.Journals, _journals,
            (j) => j.NpcId, (sw, j) => WorldSnapshotCodec.WriteJournalRecord(sw, j));

        // Death records are only ever appended, so the delta is "how many are new".
        // A shrink cannot reach this line: EnsureBaselineValid resets the baseline
        // first and the caller sends a keyframe. The guard stays as a backstop —
        // better a redundant resend than a negative count on the wire.
        var appended = snapshot.DeathRecords.Count - _deaths;
        if (appended < 0)
        {
            appended = snapshot.DeathRecords.Count;
            _deaths = 0;
        }

        w.Write((ushort)appended);
        for (var i = snapshot.DeathRecords.Count - appended; i < snapshot.DeathRecords.Count; i++)
        {
            WorldSnapshotCodec.WriteDeathRecordRecord(w, snapshot.DeathRecords[i]);
        }

        _deaths = snapshot.DeathRecords.Count;

        w.Write(EndMarker);
        BaselineTick = snapshot.Tick;
        w.Flush();
        return stream.ToArray();
    }

    internal const int EndMarker = unchecked((int)0x444C5441); // "DLTA"

    private void WriteSection<T>(BinaryWriter w, List<T> items, Dictionary<int, byte[]> baseline,
        Func<T, int> idOf, Action<BinaryWriter, T> writeRecord)
    {
        _present.Clear();
        _gone.Clear();

        // Pass one: who is new or different.
        var upserts = new List<(int id, byte[] bytes)>();
        for (var i = 0; i < items.Count; i++)
        {
            var id = idOf(items[i]);
            _present.Add(id);

            var item = items[i];
            var bytes = Capture(sw => writeRecord(sw, item));
            if (baseline.TryGetValue(id, out var previous) && Same(previous, bytes))
            {
                continue;
            }

            baseline[id] = bytes;
            upserts.Add((id, bytes));
        }

        // Pass two: who vanished. Derived from the snapshot itself rather than
        // from spawn/despawn events — the event ring trims every ~11 ticks, and a
        // missed removal is a ghost object that never goes away.
        foreach (var pair in baseline)
        {
            if (!_present.Contains(pair.Key))
            {
                _gone.Add(pair.Key);
            }
        }

        for (var i = 0; i < _gone.Count; i++)
        {
            baseline.Remove(_gone[i]);
        }

        w.Write((ushort)_gone.Count);
        for (var i = 0; i < _gone.Count; i++)
        {
            w.Write(_gone[i]);
        }

        // An add and a change are the SAME opcode: "here is entity N's record,
        // create it if you do not have it". A receiver that somehow lost an
        // entity quietly gets it back instead of throwing.
        //
        // Record length is a full int, not a ushort. An NPC record with debug
        // details runs 12-15 KB today; a ushort would sit a growth-spurt away
        // from 65535, and an unchecked cast past it would not fail — it would
        // TRUNCATE, desynchronising the reader mid-frame in the quietest way
        // possible. Two extra bytes per changed entity buy never thinking
        // about it again.
        w.Write((ushort)upserts.Count);
        for (var i = 0; i < upserts.Count; i++)
        {
            w.Write(upserts[i].id);
            w.Write(upserts[i].bytes.Length);
            w.Write(upserts[i].bytes);
        }
    }

    /// <summary>
    /// Секция колонисток — единственная, что сравнивает не запись целиком, а её
    /// ГРУППЫ ПОЛЕЙ, и по той же причине, по которой дельта вообще существует:
    /// на типичном тике меняется одна двадцатая записи, а ехала она вся. Формат
    /// апсерта тот же самый (id, длина, байты), просто внутри байтов теперь
    /// сначала маска групп — поэтому читатель секций не знает об этом ничего.
    /// <para>
    /// Свойство §83 сохранено дословно: сравниваются БАЙТЫ, выданные тем же
    /// писателем, которым пишется ключевой кадр. Здесь нет ни одного «а это
    /// поле изменилось?» — есть только «эти байты те же, что в прошлый раз?».
    /// </para>
    /// </summary>
    private void WriteNpcSection(BinaryWriter w, List<NpcSnapshot> items,
        Dictionary<int, NpcBaseline> baseline, bool includeDebugDetails)
    {
        _present.Clear();
        _gone.Clear();

        var groupMask = includeDebugDetails
            ? WorldSnapshotCodec.NpcGroup.All
            : WorldSnapshotCodec.NpcGroup.AllButDebug;

        var upserts = new List<(int id, byte[] bytes)>();
        for (var i = 0; i < items.Count; i++)
        {
            var npc = items[i];
            var id = npc.Id.Value;
            _present.Add(id);

            // Все группы — в один буфер, запоминая, где кончается каждая.
            _scratch.SetLength(0);
            _scratch.Position = 0;
            using (var gw = new BinaryWriter(_scratch, Encoding.UTF8, true))
            {
                for (var group = 0; group < WorldSnapshotCodec.NpcGroup.Count; group++)
                {
                    if ((groupMask & (1 << group)) != 0)
                    {
                        WorldSnapshotCodec.WriteNpcGroup(gw, npc, group);
                    }

                    gw.Flush();
                    _groupEnds[group] = (int)_scratch.Position;
                }
            }

            var blob = _scratch.ToArray();
            baseline.TryGetValue(id, out var previous);

            // ВСЕ сравнения — до того, как трогать базовую линию: previous и next
            // это один и тот же объект, когда колонистка уже известна.
            var changed = 0;
            for (var group = 0; group < WorldSnapshotCodec.NpcGroup.Count; group++)
            {
                if ((groupMask & (1 << group)) == 0)
                {
                    continue;
                }

                if (previous == null || !SameGroup(previous, blob, _groupEnds, group))
                {
                    changed |= 1 << group;
                }
            }

            if (previous != null && changed == 0)
            {
                continue;
            }

            _npcScratch.SetLength(0);
            _npcScratch.Position = 0;
            using (var pw = new BinaryWriter(_npcScratch, Encoding.UTF8, true))
            {
                pw.Write((ushort)changed);
                for (var group = 0; group < WorldSnapshotCodec.NpcGroup.Count; group++)
                {
                    if ((changed & (1 << group)) == 0)
                    {
                        continue;
                    }

                    var start = group == 0 ? 0 : _groupEnds[group - 1];
                    pw.Write(blob, start, _groupEnds[group] - start);
                }

                pw.Flush();
            }

            var next = previous ?? new NpcBaseline();
            next.Blob = blob;
            Array.Copy(_groupEnds, next.Ends, _groupEnds.Length);
            baseline[id] = next;

            upserts.Add((id, _npcScratch.ToArray()));
        }

        foreach (var pair in baseline)
        {
            if (!_present.Contains(pair.Key))
            {
                _gone.Add(pair.Key);
            }
        }

        for (var i = 0; i < _gone.Count; i++)
        {
            baseline.Remove(_gone[i]);
        }

        w.Write((ushort)_gone.Count);
        for (var i = 0; i < _gone.Count; i++)
        {
            w.Write(_gone[i]);
        }

        w.Write((ushort)upserts.Count);
        for (var i = 0; i < upserts.Count; i++)
        {
            w.Write(upserts[i].id);
            w.Write(upserts[i].bytes.Length);
            w.Write(upserts[i].bytes);
        }
    }

    private static bool SameGroup(NpcBaseline previous, byte[] blob, int[] ends, int group)
    {
        var oldStart = group == 0 ? 0 : previous.Ends[group - 1];
        var oldEnd = previous.Ends[group];
        var newStart = group == 0 ? 0 : ends[group - 1];
        var newEnd = ends[group];

        if (oldEnd - oldStart != newEnd - newStart)
        {
            return false;
        }

        for (var i = 0; i < oldEnd - oldStart; i++)
        {
            if (previous.Blob[oldStart + i] != blob[newStart + i])
            {
                return false;
            }
        }

        return true;
    }

    private byte[] Capture(Action<BinaryWriter> write)
    {
        _scratch.SetLength(0);
        _scratch.Position = 0;
        using (var w = new BinaryWriter(_scratch, Encoding.UTF8, true))
        {
            write(w);
            w.Flush();
        }

        return _scratch.ToArray();
    }

    private static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }
}

}
