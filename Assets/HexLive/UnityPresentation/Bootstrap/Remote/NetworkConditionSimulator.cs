#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace HexLive.UnityPresentation.Bootstrap.Remote
{

/// <summary>
/// §83: the Singapore rehearsal — <c>-hexlive-netsim &lt;baseMs&gt;:&lt;jitterMs&gt;[:seed]</c>
/// delays every received message so a localhost server feels like a distant
/// one. Lives on the socket thread inside the receive pump, BEFORE dispatch,
/// so arrival stamps, stall detection and the playhead all see the simulated
/// latency exactly as they would see a real one.
/// <para>
/// The model is TCP's: messages are delayed, never reordered and never lost —
/// <c>releaseAt = max(previousRelease, now + base + jitter)</c> keeps FIFO by
/// construction. A seeded <see cref="Random"/> makes a jitter pattern
/// replayable, so a before/after smoothness comparison measures the change,
/// not the dice. <c>-hexlive-loopback</c> rehearses the codec; this rehearses
/// the clock — the gap §83.2.9 itself complains about.
/// </para>
/// </summary>
public sealed class NetworkConditionSimulator
{
    private readonly double _baseSeconds;
    private readonly double _jitterSeconds;
    private readonly Random _rng;
    private double _lastReleaseSeconds;

    public NetworkConditionSimulator(double baseMs, double jitterMs, int seed)
    {
        _baseSeconds = Math.Max(0.0, baseMs) / 1000.0;
        _jitterSeconds = Math.Max(0.0, jitterMs) / 1000.0;
        _rng = new Random(seed);
    }

    /// <summary>Null when the session runs without <c>-hexlive-netsim</c> — the normal case.</summary>
    public static NetworkConditionSimulator? FromSessionConfig()
    {
        var spec = SessionConfig.NetSim;
        return spec is null
            ? null
            : new NetworkConditionSimulator(spec.Value.BaseMs, spec.Value.JitterMs, spec.Value.Seed);
    }

    /// <summary>
    /// Called sequentially from the single receive pump — no other caller, so
    /// the release watermark needs no lock.
    /// </summary>
    public async Task DelayAsync(CancellationToken cancel)
    {
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        var release = Math.Max(
            _lastReleaseSeconds,
            now + _baseSeconds + _jitterSeconds * _rng.NextDouble());
        _lastReleaseSeconds = release;

        var wait = release - now;
        if (wait > 0.0005)
        {
            await Task.Delay(TimeSpan.FromSeconds(wait), cancel).ConfigureAwait(false);
        }
    }
}

}
