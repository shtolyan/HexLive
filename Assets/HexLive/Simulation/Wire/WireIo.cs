using System.Collections.Generic;
using System.IO;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Wire
{

/// <summary>
/// The small vocabulary the wire codecs are written in: the value types the
/// snapshot is built from, plus the two shapes that keep tripping hand-written
/// serializers — nullable ints and string lists.
/// <para>
/// Deliberately hand-rolled and dependency-free. The simulation assembly is
/// declared with <c>noEngineReferences</c> and no package references at all
/// (spec §59.3, and the asmdef comment says never to add one), so there is no
/// JSON or MessagePack library available here — and that is the point: server
/// and client link THIS assembly, so both sides are the same code and the format
/// cannot fork.
/// </para>
/// </summary>
internal static class WireIo
{
    // ── writing ───────────────────────────────────────────────────────────

    public static void WriteTile(BinaryWriter w, TileCoord value)
    {
        w.Write(value.Q);
        w.Write(value.R);
    }

    public static void WriteFloat2(BinaryWriter w, Float2 value)
    {
        w.Write(value.X);
        w.Write(value.Y);
    }

    /// <summary>Null-safe: a null string is written as empty, never as a crash.</summary>
    public static void WriteString(BinaryWriter w, string value) => w.Write(value ?? string.Empty);

    public static void WriteNullableInt(BinaryWriter w, int? value)
    {
        w.Write(value.HasValue);
        w.Write(value ?? 0);
    }

    /// <summary>§121.11: «не задано» — полноправное значение, а не false.
    /// Пишется той же парой «есть значение + значение», что int и tile, чтобы
    /// третьего формата необязательного поля в протоколе не заводилось.</summary>
    public static void WriteNullableBool(BinaryWriter w, bool? value)
    {
        w.Write(value.HasValue);
        w.Write(value ?? false);
    }

    public static void WriteNullableTile(BinaryWriter w, TileCoord? value)
    {
        w.Write(value.HasValue);
        WriteTile(w, value ?? TileCoord.Zero);
    }

    public static void WriteStrings(BinaryWriter w, List<string> values)
    {
        if (values == null)
        {
            w.Write(0);
            return;
        }

        w.Write(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            WriteString(w, values[i]);
        }
    }

    public static void WriteInts(BinaryWriter w, List<int> values)
    {
        w.Write(values?.Count ?? 0);
        if (values == null) return;
        for (var i = 0; i < values.Count; i++) w.Write(values[i]);
    }

    public static void WriteJunctions(BinaryWriter w, List<JunctionId> values)
    {
        if (values == null)
        {
            w.Write(0);
            return;
        }

        w.Write(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            w.Write(values[i].Value);
        }
    }

    // ── reading ───────────────────────────────────────────────────────────

    public static TileCoord ReadTile(BinaryReader r)
    {
        var q = r.ReadInt32();
        var rr = r.ReadInt32();
        return new TileCoord(q, rr);
    }

    public static Float2 ReadFloat2(BinaryReader r)
    {
        var x = r.ReadSingle();
        var y = r.ReadSingle();
        return new Float2(x, y);
    }

    public static int? ReadNullableInt(BinaryReader r)
    {
        var has = r.ReadBoolean();
        var value = r.ReadInt32();
        return has ? value : (int?)null;
    }

    public static bool? ReadNullableBool(BinaryReader r)
    {
        var has = r.ReadBoolean();
        var value = r.ReadBoolean();
        return has ? value : (bool?)null;
    }

    public static TileCoord? ReadNullableTile(BinaryReader r)
    {
        var has = r.ReadBoolean();
        var value = ReadTile(r);
        return has ? value : (TileCoord?)null;
    }

    /// <summary>Refills the list in place — snapshot list properties are get-only.</summary>
    public static void ReadStrings(BinaryReader r, List<string> into)
    {
        into.Clear();
        var count = r.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            into.Add(r.ReadString());
        }
    }

    public static void ReadInts(BinaryReader r, List<int> into)
    {
        into.Clear();
        var count = r.ReadInt32();
        for (var i = 0; i < count; i++) into.Add(r.ReadInt32());
    }

    public static void ReadJunctions(BinaryReader r, List<JunctionId> into)
    {
        into.Clear();
        var count = r.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            into.Add(new JunctionId(r.ReadInt32()));
        }
    }

    /// <summary>
    /// Grows or shrinks a list of reusable payload objects to
    /// <paramref name="count"/> entries, keeping the ones already there.
    /// Decoding reuses the previous frame's objects for the same reason the
    /// exporter does: at 4 frames a second, reallocating every NPC, object and
    /// mob is pure garbage.
    /// </summary>
    public static void Resize<T>(List<T> list, int count) where T : new()
    {
        while (list.Count > count)
        {
            list.RemoveAt(list.Count - 1);
        }

        while (list.Count < count)
        {
            list.Add(new T());
        }
    }
}

}
