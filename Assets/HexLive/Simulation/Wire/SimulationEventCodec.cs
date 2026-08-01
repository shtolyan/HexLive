using System.Collections.Generic;
using System.IO;
using HexLive.Simulation.Runtime;

namespace HexLive.Simulation.Wire
{

/// <summary>
/// The event stream, encoded as a range of <see cref="SimulationEvent.Seq"/>.
/// <para>
/// A separate channel from the snapshot on purpose. Sound, colonist speech and
/// the colony history are all driven off discrete events, and the snapshot's
/// <c>TraceEvents</c> list cannot carry them: it holds the ENTIRE 2048-entry ring
/// and is gated behind the debug-details flag, so it is both absent in normal
/// play and ~200 KB per frame when present.
/// </para>
/// <para>
/// The frame also states the oldest seq the sender still had. A receiver whose
/// watermark is below that has missed events to ring trimming and must SKIP the
/// gap — replaying it writes duplicate colony-history lines, since
/// <c>GameHistoryLog</c> does not dedup.
/// </para>
/// </summary>
public static class SimulationEventCodec
{
    public const int WireVersion = 1;

    private const int EndMarker = unchecked((int)0x45564E54); // "EVNT"

    /// <summary>
    /// Writes every event in <paramref name="events"/>, plus the sender's
    /// oldest-retained seq so the receiver can detect a gap.
    /// </summary>
    public static void Write(IReadOnlyList<SimulationEvent> events, long oldestRetainedSeq, BinaryWriter w)
    {
        w.Write(WireVersion);
        w.Write(oldestRetainedSeq);
        w.Write(events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            w.Write(e.Seq);
            w.Write(e.Tick);
            WireIo.WriteString(w, e.Type);
            // Message rides VERBATIM: SoundManager parses mob ids out of it
            // ("Dog={id} …") and the history formatter reads it too. Do not
            // "optimize" it into structured fields without fixing both.
            WireIo.WriteString(w, e.Message);
            WireIo.WriteNullableInt(w, e.EntityId);
        }

        w.Write(EndMarker);
    }

    /// <summary>
    /// Appends decoded events to <paramref name="into"/> and returns the sender's
    /// oldest-retained seq (the caller compares it against its own watermark to
    /// spot a gap).
    /// </summary>
    public static long Read(BinaryReader r, List<SimulationEvent> into)
    {
        var version = r.ReadInt32();
        if (version != WireVersion)
        {
            throw new InvalidDataException(
                $"Event wire version {version}, expected {WireVersion} — " +
                "the two ends are running different builds of HexLive.Simulation.");
        }

        var oldestRetainedSeq = r.ReadInt64();
        var count = r.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            into.Add(new SimulationEvent
            {
                Seq = r.ReadInt64(),
                Tick = r.ReadInt32(),
                Type = r.ReadString(),
                Message = r.ReadString(),
                EntityId = WireIo.ReadNullableInt(r)
            });
        }

        var marker = r.ReadInt32();
        if (marker != EndMarker)
        {
            throw new InvalidDataException(
                "Event frame did not end where it should — reader and writer disagree about the layout.");
        }

        return oldestRetainedSeq;
    }
}

}
