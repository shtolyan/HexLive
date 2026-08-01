#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Bootstrap.Remote
{

/// <summary>
/// Turns "frames arrive over a network, whenever they arrive" into the steady
/// tick + alpha the renderer expects.
/// <para>
/// <b>The one rule that governs this class:</b> <c>SpeedMultiplier</c> and
/// <c>TickAlpha</c> must come from the SAME estimate. The renderer feeds the
/// multiplier to <c>NpcActorView.SetSimSpeed</c>, and the whole animation layer
/// hangs off it — <c>_animator.speed</c>, the hop arc timer, action phases —
/// while alpha drives the root's position by lerping between snapshot poses. If
/// the two disagree by even ten percent, the feet slide and a jump arc overshoots
/// its landing. So: one measured rate, both numbers derived from it, and never
/// the speed the server merely CLAIMS.
/// </para>
/// </summary>
public sealed class RemoteTickClock
{
    /// <summary>
    /// How many ticks to keep in hand before presenting. Small enough that the
    /// world does not feel a second behind, big enough to ride out ordinary
    /// jitter. Below this the clock coasts slightly slow; above it, slightly
    /// fast — a soft pull rather than a jump, so the correction is invisible.
    /// </summary>
    private const int TargetBufferDepth = 3;

    /// <summary>
    /// A gap this large is not lag, it is a different session (reconnect, or a
    /// server restart): resync instead of trying to interpolate across it.
    /// </summary>
    private const int DiscontinuityTicks = 24;

    private float _tickDeltaTime = 0.25f;

    private float _alpha;
    private float _rate;          // ticks per second, smoothed
    private float _observedRate;
    private float _sinceLastFrame;

    private bool _paused = true;
    private bool _started;

    public int PresentedTick { get; private set; } = -1;

    public float TickAlpha => Mathf.Clamp01(_alpha);

    /// <summary>
    /// The speed the world is OBSERVED to run at, expressed the way the local
    /// runner expresses it. Derived from the same rate that advances alpha.
    /// </summary>
    public float SpeedMultiplier =>
        _paused || _tickDeltaTime <= 0f ? 1f : Mathf.Max(0.01f, _rate * _tickDeltaTime);

    public bool IsPaused => _paused;

    public void Configure(float tickDeltaTime, int tick, float declaredSpeed, bool paused)
    {
        _tickDeltaTime = tickDeltaTime > 0f ? tickDeltaTime : 0.25f;
        PresentedTick = tick;
        _alpha = 0f;
        _paused = paused;
        _started = false;
        // Seed the estimator from what the server says, then let observation
        // take over. A declaration is a good first guess and a bad steady state.
        _observedRate = declaredSpeed / _tickDeltaTime;
        _rate = _observedRate;
    }

    /// <summary>The server told us its clock changed. Feed it forward, do not wait to measure it.</summary>
    public void OnServerClock(float declaredSpeed, bool paused)
    {
        _paused = paused;
        var declared = declaredSpeed / _tickDeltaTime;
        if (!Mathf.Approximately(declared, _observedRate))
        {
            _observedRate = declared;
            _rate = declared;
        }
    }

    /// <summary>A frame for <paramref name="tick"/> arrived. Returns false if it should be dropped.</summary>
    public bool OnFrameArrived(int tick, out bool discontinuity)
    {
        discontinuity = false;

        if (!_started)
        {
            _started = true;
            PresentedTick = tick - 1;
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

        if (tick - PresentedTick > DiscontinuityTicks)
        {
            discontinuity = true;
            Resync(tick);
            return true;
        }

        // Measure arrivals rather than trusting the wall clock between them.
        if (_sinceLastFrame > 0.0001f)
        {
            var instant = 1f / _sinceLastFrame;
            _observedRate = Mathf.Lerp(_observedRate, instant, 0.1f);
        }

        _sinceLastFrame = 0f;
        return true;
    }

    private void Resync(int tick)
    {
        PresentedTick = tick - 1;
        _alpha = 0f;
    }

    /// <summary>
    /// Advances by one rendered frame. Returns how many ticks should be
    /// presented now (0 or more), given how many are buffered.
    /// </summary>
    public int Advance(float unscaledDeltaTime, int bufferedTicks)
    {
        _sinceLastFrame += unscaledDeltaTime;

        if (_paused || !_started)
        {
            return 0;
        }

        // Soft PLL: drain a fat buffer a touch faster, coast on a thin one.
        // ±10% is below the threshold where a walk cycle looks wrong, so the
        // correction never reads as a speed change.
        var correction = Mathf.Clamp(1f + 0.05f * (bufferedTicks - TargetBufferDepth), 0.9f, 1.1f);
        _rate = Mathf.Max(0.01f, _observedRate * correction);

        // alpha is measured in ticks; rate is ticks per second.
        _alpha += unscaledDeltaTime * _rate;

        var present = 0;
        while (_alpha >= 1f && present < bufferedTicks)
        {
            _alpha -= 1f;
            present++;
        }

        if (_alpha >= 1f)
        {
            // Starved. HOLD the last pose — never extrapolate: on a hex lattice
            // a guessed position lands inside a blocked junction and snaps back
            // the moment the real frame arrives, which reads far worse than a
            // brief freeze.
            _alpha = 1f;
        }

        return present;
    }

    public void OnTickPresented(int tick) => PresentedTick = tick;
}

}
