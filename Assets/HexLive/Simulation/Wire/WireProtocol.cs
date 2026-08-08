using System;
using System.IO;
using System.Text;

namespace HexLive.Simulation.Wire
{

/// <summary>
/// Frame kinds on the socket. Every message is one byte of kind followed by the
/// payload the matching codec wrote.
/// </summary>
public enum FrameKind : byte
{
    /// <summary>Server → client, once, on connect. See <see cref="Handshake"/>.</summary>
    Handshake = 1,

    /// <summary>
    /// Server → client: a complete <c>WorldSnapshotCodec</c> frame. Sent on
    /// connect, after a reconnect, and whenever the delta chain breaks.
    /// </summary>
    Snapshot = 2,

    /// <summary>Server → client: <c>SimulationEventCodec</c> payload.</summary>
    Events = 3,

    /// <summary>Client → server: pause / resume / speed.</summary>
    Command = 4,

    /// <summary>
    /// Client → server, echoed straight back as <see cref="Pong"/>. Carries an
    /// opaque token the client matches to work out round-trip time — and, more
    /// importantly, to notice that the connection has gone quiet: a socket that
    /// has died without a close frame looks exactly like a paused world until
    /// something asks it a question.
    /// </summary>
    Ping = 5,

    /// <summary>Server → client: the echo of a <see cref="Ping"/>.</summary>
    Pong = 6,

    /// <summary>
    /// Server → client: the world's clock changed (someone paused it, or moved
    /// the speed). Without this a viewer would have to guess pause from silence,
    /// and a paused world is indistinguishable from a broken connection.
    /// </summary>
    ServerClock = 7,

    /// <summary>
    /// Server → client: only what moved since the tick stamped inside. The
    /// receiver refuses one whose baseline is not the tick its mirror holds —
    /// applying a delta to the wrong state is exactly the silent corruption the
    /// whole design is arranged to avoid.
    /// </summary>
    SnapshotDelta = 8,

    /// <summary>
    /// Client → server: "my chain is broken, send me everything." The one
    /// recovery path, used after a gap, a decode failure or a reconnect.
    /// </summary>
    RequestKeyframe = 9,
}

public enum CommandKind : byte
{
    Pause = 1,
    Resume = 2,
    SetSpeed = 3,
}

/// <summary>
/// What a client is told the moment it connects — everything it needs to build
/// its own copy of the static world before the first tick frame arrives.
/// <para>
/// The two interesting fields are <see cref="Seed"/> and <see cref="SimData"/>.
/// The seed lets the client regenerate the ~14 000 junctions locally instead of
/// receiving them (about 800 KB it never has to download), and the tuned catalog
/// travels WITH the world rather than being assumed to match: a client whose
/// ScriptableObjects had drifted would otherwise regenerate a subtly different
/// island and mis-render everything on it.
/// </para>
/// </summary>
public sealed class Handshake
{
    // 2: §21.21B v15 added HopFromTile to the NPC record.
    // 3: §121 added IsManualControl to the NPC record.
    public const int ProtocolVersion = 3;

    public int Seed { get; set; }

    public int Tick { get; set; }

    public float TickDeltaTime { get; set; }

    public float SpeedMultiplier { get; set; }

    public bool Paused { get; set; }

    /// <summary>Event seq the client should start asking from.</summary>
    public long EventSeq { get; set; }

    /// <summary>
    /// Checksum of the server's regenerated topology (tiles + junction ids and
    /// positions). The client recomputes it after its own worldgen and refuses
    /// to continue on a mismatch — that is the guard against the one soft spot
    /// in regenerating from a seed, a float rounding difference flipping a tile's
    /// elevation.
    /// </summary>
    public uint TopologyChecksum { get; set; }

    /// <summary>The server's <c>simdata.json</c>, verbatim.</summary>
    public string SimData { get; set; } = string.Empty;

    public void Write(BinaryWriter w)
    {
        w.Write(ProtocolVersion);
        w.Write(Seed);
        w.Write(Tick);
        w.Write(TickDeltaTime);
        w.Write(SpeedMultiplier);
        w.Write(Paused);
        w.Write(EventSeq);
        w.Write(TopologyChecksum);
        w.Write(SimData ?? string.Empty);
    }

    public static Handshake Read(BinaryReader r)
    {
        var version = r.ReadInt32();
        if (version != ProtocolVersion)
        {
            throw new InvalidDataException(
                $"Handshake protocol version {version}, expected {ProtocolVersion}.");
        }

        return new Handshake
        {
            Seed = r.ReadInt32(),
            Tick = r.ReadInt32(),
            TickDeltaTime = r.ReadSingle(),
            SpeedMultiplier = r.ReadSingle(),
            Paused = r.ReadBoolean(),
            EventSeq = r.ReadInt64(),
            TopologyChecksum = r.ReadUInt32(),
            SimData = r.ReadString(),
        };
    }
}

/// <summary>Framing helpers shared by both ends.</summary>
public static class Frame
{
    public static byte[] Wrap(FrameKind kind, byte[] payload)
    {
        var framed = new byte[payload.Length + 1];
        framed[0] = (byte)kind;
        Buffer.BlockCopy(payload, 0, framed, 1, payload.Length);
        return framed;
    }

    public static byte[] Handshake(Handshake handshake)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        handshake.Write(writer);
        writer.Flush();
        return Wrap(FrameKind.Handshake, stream.ToArray());
    }

    public static byte[] Command(CommandKind kind, float value)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write((byte)kind);
        writer.Write(value);
        writer.Flush();
        return Wrap(FrameKind.Command, stream.ToArray());
    }

    public static byte[] Ping(long token) => WithLong(FrameKind.Ping, token);

    public static byte[] Pong(long token) => WithLong(FrameKind.Pong, token);

    public static byte[] ServerClock(bool paused, float speedMultiplier)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(paused);
        writer.Write(speedMultiplier);
        writer.Flush();
        return Wrap(FrameKind.ServerClock, stream.ToArray());
    }

    public static (bool paused, float speedMultiplier) ReadServerClock(byte[] payload)
    {
        using var stream = new MemoryStream(payload);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        return (reader.ReadBoolean(), reader.ReadSingle());
    }

    public static long ReadLong(byte[] payload)
    {
        using var stream = new MemoryStream(payload);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        return reader.ReadInt64();
    }

    private static byte[] WithLong(FrameKind kind, long value)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(value);
        writer.Flush();
        return Wrap(kind, stream.ToArray());
    }
}

}
