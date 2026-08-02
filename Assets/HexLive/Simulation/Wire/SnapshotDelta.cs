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
    private readonly Dictionary<int, byte[]> _npcs = new();
    // §28.15C v3: тела. Дешевле всех остальных секций: труп не двигается и не
    // меняется, поэтому стоит один апсерт в кадре появления — и ноль байт
    // навсегда после. Ровно ради этого дельта и сравнивает БАЙТЫ, а не поля.
    private readonly Dictionary<int, byte[]> _corpses = new();
    private readonly Dictionary<int, byte[]> _mobs = new();
    private readonly Dictionary<int, byte[]> _crabs = new();
    private readonly Dictionary<int, byte[]> _sharks = new();
    private byte[] _header = Array.Empty<byte>();
    private int _deaths;

    private readonly MemoryStream _scratch = new();
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
        _header = Array.Empty<byte>();
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

        WriteSection(w, snapshot.Objects, _objects,
            (o) => o.Id.Value, (sw, o) => WorldSnapshotCodec.WriteObjectRecord(sw, o));

        WriteSection(w, snapshot.Npcs, _npcs,
            (n) => n.Id.Value, (sw, n) => WorldSnapshotCodec.WriteNpcRecord(sw, n, includeDebugDetails));

        WriteSection(w, snapshot.Corpses, _corpses,
            (n) => n.Id.Value, (sw, n) => WorldSnapshotCodec.WriteNpcRecord(sw, n, includeDebugDetails));

        WriteSection(w, snapshot.Mobs, _mobs,
            (m) => m.Id, (sw, m) => WorldSnapshotCodec.WriteMobRecord(sw, m));

        WriteSection(w, snapshot.Crabs, _crabs,
            (c) => c.Id, (sw, c) => WorldSnapshotCodec.WriteCrabRecord(sw, c));

        WriteSection(w, snapshot.Sharks, _sharks,
            (s) => s.Id, (sw, s) => WorldSnapshotCodec.WriteSharkRecord(sw, s));

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
