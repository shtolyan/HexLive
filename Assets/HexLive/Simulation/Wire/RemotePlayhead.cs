#nullable enable
using System;

namespace HexLive.Simulation.Wire
{

/// <summary>
/// Turns "frames arrive over a network, whenever they arrive" into the steady
/// tick + alpha the renderer expects — as ONE continuous playhead over the
/// server's tick timeline, not a tick-quantized phase.
/// <para>
/// <b>The one rule that governs this class</b> (inherited from the old
/// <c>RemoteTickClock</c>): <c>SpeedMultiplier</c> and <c>TickAlpha</c> must
/// come from the SAME estimate. The renderer feeds the multiplier to
/// <c>NpcActorView.SetSimSpeed</c>, and the whole animation layer hangs off it,
/// while alpha drives the root's position by lerping between snapshot poses.
/// Here both are literally the same number: the multiplier IS the playhead's
/// derivative (times the tick period), so they cannot disagree (§83.2.9).
/// </para>
/// <para>
/// Why a playhead and not a phase: the old clock presented whole ticks when an
/// accumulated phase crossed 1.0 and measured the world's rate from main-thread
/// frame gaps. Both quantizations showed on screen — a rate mis-estimate made
/// two ticks land in one rendered frame (the renderer then lerped a two-tick
/// distance over one tick of alpha: a 2x-speed burst), and the estimate itself
/// was noise because arrival instants were rounded to render frames. The
/// playhead instead follows a target built from SOCKET-THREAD arrival
/// timestamps through a min-delay filter (the Source/GGPO trick: the fastest
/// observed path is the truth, everything slower is jitter), and corrects only
/// by slewing its rate a few percent — below what a walk cycle can show.
/// </para>
/// <para>
/// Lives in the Simulation assembly on purpose: no UnityEngine, so the
/// headless test project can drive it through synthetic arrival schedules
/// (steady, jittery, bursty, starved) instead of reading source text.
/// </para>
/// </summary>
public sealed class RemotePlayhead
{
    /// <summary>
    /// Interpolation delay in ticks: how far behind the newest arrived tick the
    /// playhead aims to run. Small enough that the world does not feel a second
    /// behind, big enough to ride out ordinary jitter.
    /// </summary>
    public const double DefaultDelayTicks = 3.0;

    /// <summary>
    /// A gap this large is not lag, it is a different session (reconnect, or a
    /// server restart): resync instead of trying to interpolate across it.
    /// </summary>
    private const int DiscontinuityTicks = 24;

    /// <summary>
    /// Rate slew per tick of target error. One tick of error reads as a 3%
    /// tempo change — invisible; the old clock already accepted ±10%.
    /// </summary>
    private const double SlewPerTickOfError = 0.03;

    /// <summary>Hard cap on the slew, reached at ~3.3 ticks of error.</summary>
    private const double MaxSlew = 0.10;

    /// <summary>Errors inside this band are noise, not something to chase.</summary>
    private const double DeadbandTicks = 0.05;

    /// <summary>
    /// The min-delay filter's memory. Long enough to have seen a fast path,
    /// short enough to follow a genuine route change.
    /// </summary>
    private const double OffsetWindowSeconds = 5.0;

    /// <summary>
    /// Starvation ease-out: over the last quarter-tick before the newest
    /// arrived data, the playhead decelerates smoothly to a stop instead of
    /// freezing mid-stride — and because it never passes real data, the late
    /// frame's arrival is a smooth speed-up, never a snap-back.
    /// </summary>
    private const double StarveEaseTicks = 0.25;

    /// <summary>
    /// §83: adaptive interpolation delay (1 tick + jitter margin). The old
    /// fear — "against the poll-loop server the 62.5 ms send quantization
    /// would make it under-buffer" — did not survive measurement: on a
    /// synthetic 62.5 ms-quantized schedule the adaptive buffer settles at
    /// its floor and stays perfectly smooth. What DID show up on the player's
    /// measured 16 KB/s link (bug #339) was the opposite failure: rare large
    /// frames repeat every 10–30 s while the jitter window remembers only
    /// 5 s, so the buffer kept shrinking between spikes and starved on the
    /// next one. Hence the slow shrink and the 1.5-tick floor below.
    /// </summary>
    public bool AdaptiveDelay { get; set; }

    /// <summary>
    /// Adaptive shrink, ticks per second. Deliberately far slower than the
    /// instant growth: the spikes worth buffering against recur on tens of
    /// seconds, so forgetting one must take minutes, not the 5-second jitter
    /// window. At 0.01 t/s the buffer sheds a full tick in 100 s — on the
    /// measured link, spikes 30 s apart cost only 0.3 tick of learned margin
    /// between them, so the buffer stays ahead of the next one.
    /// </summary>
    private const double AdaptiveShrinkPerSecond = 0.01;

    /// <summary>
    /// Floor of the adaptive jitter margin, in ticks over the base 1.0. On a
    /// clean link the delay settles at 1.0 + this = 1.5 ticks (375 ms at 1x)
    /// — half the fixed default, still enough that one late frame never
    /// starves the playhead outright.
    /// </summary>
    private const double AdaptiveMinMarginTicks = 0.5;

    private const int WindowCapacity = 64;
    private readonly double[] _offsetValues = new double[WindowCapacity];
    private readonly double[] _offsetTimes = new double[WindowCapacity];
    private readonly double[] _jitterScratch = new double[WindowCapacity];
    private int _offsetCount;
    private int _offsetHead;

    private float _tickDeltaTime = 0.25f;
    private double _declaredSpeed = 1.0;
    private double _playhead;
    private double _delayTicks = DefaultDelayTicks;
    private double _rateScale = 1.0;
    private double _easeScale = 1.0;
    private double _lastErrorTicks;
    private int _newestArrivedTick = int.MinValue;
    private bool _paused = true;
    private bool _started;

    /// <summary>The tick of the frame the mirror last decoded.</summary>
    public int PresentedTick { get; private set; } = -1;

    /// <summary>
    /// Decode queued frames whose tick is ≤ this. At 1x the playhead moves
    /// ~0.07 tick per rendered frame, so this admits at most one frame per
    /// Update by construction — the 2x-burst of the old present-loop cannot
    /// happen. During operator fast-forward it legitimately admits several.
    /// </summary>
    public int TickToDecode => _started ? (int)Math.Floor(_playhead) + 1 : int.MinValue;

    /// <summary>
    /// Phase of the lerp between the previously shown pose and the pose of
    /// <see cref="PresentedTick"/>. Fraction of the same playhead that decides
    /// what to decode, so position and cadence share one clock.
    /// </summary>
    public float TickAlpha =>
        !_started || PresentedTick < 0
            ? 0f
            : (float)Clamp01(_playhead - PresentedTick + 1.0);

    /// <summary>
    /// The speed the world is OBSERVED to run at — the playhead's actual rate,
    /// slew and starvation ease included, expressed the way the local runner
    /// expresses it. This is the number the whole animation layer hangs off.
    /// </summary>
    public float SpeedMultiplier =>
        _paused ? 1f : (float)Math.Max(0.01, _declaredSpeed * _rateScale * _easeScale);

    public bool IsPaused => _paused;

    // ── diagnostics (HUD, LocomotionLagRecorder) ──────────────────────────

    /// <summary>How far ahead of the playhead the newest arrived data is.</summary>
    public float BufferedTicks =>
        _started ? (float)(_newestArrivedTick - _playhead) : 0f;

    /// <summary>The interpolation delay currently in force, in ticks.</summary>
    public float DelayTicks => (float)_delayTicks;

    /// <summary>Signed distance from the playhead to its target, in ticks.</summary>
    public float TargetErrorTicks => (float)_lastErrorTicks;

    /// <summary>P95 of arrival lateness relative to the fastest observed path.</summary>
    public float JitterP95Milliseconds
    {
        get
        {
            if (_offsetCount < 4)
            {
                return 0f;
            }

            // A scratch copy + Array.Sort: netstandard2.1 (Unity's profile for
            // this assembly) has no Span.Sort, and 64 doubles 4 times a second
            // is nothing.
            var sorted = _jitterScratch;
            for (var i = 0; i < _offsetCount; i++)
            {
                sorted[i] = _offsetValues[(_offsetHead + i) % WindowCapacity];
            }

            Array.Sort(sorted, 0, _offsetCount);
            var p95 = sorted[(int)((_offsetCount - 1) * 0.95)];
            return (float)((p95 - sorted[0]) * 1000.0);
        }
    }

    public void Configure(float tickDeltaTime, int tick, float declaredSpeed, bool paused)
    {
        _tickDeltaTime = tickDeltaTime > 0f ? tickDeltaTime : 0.25f;
        _declaredSpeed = declaredSpeed > 0f ? declaredSpeed : 1.0;
        _paused = paused;
        _started = false;
        PresentedTick = tick;
        _playhead = tick;
        _newestArrivedTick = int.MinValue;
        _rateScale = 1.0;
        _easeScale = 1.0;
        _delayTicks = DefaultDelayTicks;
        ClearOffsetWindow();
    }

    /// <summary>The server told us its clock changed. Feed it forward, do not wait to measure it.</summary>
    public void OnServerClock(float declaredSpeed, bool paused)
    {
        var declared = declaredSpeed > 0f ? declaredSpeed : 1.0;
        var speedChanged = Math.Abs(declared - _declaredSpeed) > 0.0001;
        var resumed = _paused && !paused;
        _declaredSpeed = declared;
        _paused = paused;

        if (speedChanged || resumed)
        {
            // The offset samples were taken against the OLD tick period (or
            // against a timeline that stood still). They would poison the
            // min-delay filter; drop them and re-learn from the next arrivals.
            ClearOffsetWindow();
        }
    }

    /// <summary>
    /// A frame for <paramref name="tick"/> arrived at
    /// <paramref name="arrivalSeconds"/> — the SOCKET-THREAD receipt instant
    /// (Stopwatch-based, same epoch as the <c>nowSeconds</c> given to
    /// <see cref="Advance"/>). Measuring here, not on the render thread, is
    /// what frees the estimate from render-frame quantization.
    /// Returns false for a stale duplicate that should not count.
    /// </summary>
    public bool OnFrameArrived(int tick, double arrivalSeconds, out bool discontinuity)
    {
        discontinuity = false;

        if (!_started)
        {
            // First data of the session: show it immediately (playhead lands
            // one tick behind, so the keyframe decodes on this very Update) and
            // let the slew build the interpolation buffer over the next few
            // seconds — a brief, invisible slow-motion instead of a black wait.
            _started = true;
            PresentedTick = tick - 1;
            _playhead = tick - 1;
            _newestArrivedTick = tick;
            AddOffsetSample(tick, arrivalSeconds);
            return true;
        }

        if (tick <= PresentedTick)
        {
            // Already shown, or the session restarted and ticks went backwards.
            if (PresentedTick - tick > DiscontinuityTicks)
            {
                discontinuity = true;
                Resync(tick);
                return true;
            }

            return false;
        }

        // Measured against the PLAYHEAD, not the last decoded tick: after a
        // queue overflow the recovery keyframe legitimately sits far ahead of
        // what was presented, and waiting for a ±10% slew to cover that gap
        // would freeze the view for a minute. Far ahead of the playhead means
        // "too far to interpolate across" regardless of how we got here.
        if (tick - _playhead > DiscontinuityTicks)
        {
            discontinuity = true;
            Resync(tick);
            return true;
        }

        if (tick > _newestArrivedTick)
        {
            _newestArrivedTick = tick;
        }

        AddOffsetSample(tick, arrivalSeconds);
        return true;
    }

    private void Resync(int tick)
    {
        PresentedTick = tick - 1;
        _playhead = tick - 1;
        _newestArrivedTick = tick;
        _rateScale = 1.0;
        _easeScale = 1.0;
        ClearOffsetWindow();
    }

    /// <summary>
    /// Advances the playhead by one rendered frame. <paramref name="nowSeconds"/>
    /// must share the epoch of the arrival stamps.
    /// </summary>
    public void Advance(double nowSeconds, float unscaledDeltaTime)
    {
        if (!_started || unscaledDeltaTime <= 0f)
        {
            return;
        }

        PruneOffsetWindow(nowSeconds);

        var period = _tickDeltaTime / _declaredSpeed;

        if (_paused)
        {
            // §83: drain what is buffered before freezing, so the pause shows
            // the CURRENT state instead of one D ticks in the past. The server
            // stops producing frames, so this converges and stops.
            _rateScale = 1.0;
            AdvanceToward(_newestArrivedTick, period, unscaledDeltaTime);
            _lastErrorTicks = 0.0;
            return;
        }

        // Target: where the server's timeline is NOW (per the fastest observed
        // path), minus the interpolation delay. With an empty window (right
        // after a speed change or a long stall) fall back to trailing the
        // newest data by the same delay.
        var target = _offsetCount > 0
            ? (nowSeconds - MinOffset()) / period - _delayTicks
            : _newestArrivedTick - _delayTicks;

        if (AdaptiveDelay)
        {
            // Grow immediately on evidence of lateness, shrink slowly —
            // standard jitter-buffer practice, prevents oscillation.
            var desired = 1.0 + Clamp(
                JitterP95Milliseconds / 1000.0 / period, AdaptiveMinMarginTicks, 3.0);

            // Ground truth beats estimation: the target passing the newest
            // arrived data means the buffer HAS just proven too small by
            // exactly this many ticks — the P95-over-5s window systematically
            // misses rare spikes that recur on tens of seconds (measured on
            // the 16 KB/s link, bug #339). Grow by the observed deficit, so
            // the NEXT spike of this size is absorbed; the slow shrink below
            // remembers it for minutes, not seconds.
            var deficit = target - _newestArrivedTick;
            if (deficit > 0.0)
            {
                desired = Math.Max(desired, Math.Min(_delayTicks + deficit, 4.0));
            }

            _delayTicks = desired > _delayTicks
                ? desired
                : Math.Max(desired, _delayTicks - AdaptiveShrinkPerSecond * unscaledDeltaTime);
        }

        if (target > _newestArrivedTick)
        {
            target = _newestArrivedTick;
        }

        var error = target - _playhead;
        _lastErrorTicks = error;

        var magnitude = Math.Abs(error) - DeadbandTicks;
        var slew = magnitude <= 0.0
            ? 0.0
            : Math.Min(magnitude * SlewPerTickOfError, MaxSlew);
        _rateScale = error >= 0.0 ? 1.0 + slew : 1.0 - slew;

        AdvanceToward(_newestArrivedTick, period, unscaledDeltaTime);
    }

    private void AdvanceToward(double limit, double period, float dt)
    {
        var distance = limit - _playhead;
        // The smoothstep below converges algebraically, so the last few
        // thousandths of a tick would take forever: snap them. Half a
        // millisecond of world time — invisible.
        if (distance <= 0.002)
        {
            _easeScale = 0.0;
            _playhead = limit;
            return;
        }

        // Smoothstep ease-out over the final quarter-tick before running out
        // of data: decelerate instead of freezing (starvation), and never pass
        // the newest real frame — extrapolation on a hex lattice guesses into
        // blocked junctions.
        var t = Clamp01(distance / StarveEaseTicks);
        _easeScale = t * t * (3.0 - 2.0 * t);

        var advance = dt / period * _rateScale * _easeScale;
        _playhead = Math.Min(_playhead + advance, limit);
    }

    public void OnTickPresented(int tick) => PresentedTick = tick;

    // ── min-delay offset window ───────────────────────────────────────────

    private void AddOffsetSample(int tick, double arrivalSeconds)
    {
        var period = _tickDeltaTime / _declaredSpeed;
        var offset = arrivalSeconds - tick * period;

        if (_offsetCount == WindowCapacity)
        {
            _offsetHead = (_offsetHead + 1) % WindowCapacity;
            _offsetCount--;
        }

        var slot = (_offsetHead + _offsetCount) % WindowCapacity;
        _offsetValues[slot] = offset;
        _offsetTimes[slot] = arrivalSeconds;
        _offsetCount++;
    }

    private void PruneOffsetWindow(double nowSeconds)
    {
        while (_offsetCount > 0 &&
               nowSeconds - _offsetTimes[_offsetHead] > OffsetWindowSeconds)
        {
            _offsetHead = (_offsetHead + 1) % WindowCapacity;
            _offsetCount--;
        }
    }

    private double MinOffset()
    {
        var min = double.MaxValue;
        for (var i = 0; i < _offsetCount; i++)
        {
            var v = _offsetValues[(_offsetHead + i) % WindowCapacity];
            if (v < min)
            {
                min = v;
            }
        }

        return min;
    }

    private void ClearOffsetWindow()
    {
        _offsetCount = 0;
        _offsetHead = 0;
    }

    private static double Clamp01(double value) =>
        value < 0.0 ? 0.0 : value > 1.0 ? 1.0 : value;

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;
}

}
