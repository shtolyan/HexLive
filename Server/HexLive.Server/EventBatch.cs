using System.Collections.Generic;

namespace HexLive.Server
{

/// <summary>
/// One event as a pulling consumer sees it (§144.6). A copy, not the live
/// <c>SimulationEvent</c>: the ring keeps mutating behind the world lock, and a
/// caller that held a reference would be reading the world without holding it.
/// </summary>
public readonly record struct EventRecord(
    long Seq,
    int Tick,
    string Type,
    string Message,
    int? EntityId);

/// <summary>
/// The answer to "what happened since Seq=N", with the three ways that question
/// can go wrong stated explicitly rather than left for the caller to infer from
/// a suspiciously short list.
/// </summary>
/// <param name="Events">Player-visible events after the caller's watermark, oldest first.</param>
/// <param name="Watermark">What to pass as <c>sinceSeq</c> next time. Advances past
/// filtered-out chatter, but never past a row that truncation withheld.</param>
/// <param name="OldestRetainedSeq">Oldest seq the ring still holds.</param>
/// <param name="Gap">The ring trimmed past the caller: events were lost for good and
/// must be skipped, not replayed.</param>
/// <param name="SessionReset">Seq restarted below the caller's watermark — a different
/// process, not this world. Loading a save does not trip this.</param>
/// <param name="Truncated">More events were waiting than <c>limit</c> allowed; call
/// again with the returned watermark.</param>
public sealed record EventBatch(
    IReadOnlyList<EventRecord> Events,
    long Watermark,
    long OldestRetainedSeq,
    bool Gap,
    bool SessionReset,
    bool Truncated);

}
