#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{
    /// <summary>
    /// Spec 40.8-K: one clock for every painter, and only one painter repaints
    /// at a time.
    ///
    /// Each painter used to own a private 0.25 s timer. That reads as "four
    /// repaints a second", but the count is per PAINTER and there are far more
    /// of them than there are colonists: one per body, one per WORN GARMENT,
    /// one per mob. A four-girl dog fight runs ~19 of them — and nothing
    /// staggered them, because they are all driven by the same simulation
    /// tick, so their windows expired in lockstep and every repaint landed on
    /// the SAME frame. What the player felt was not the cost of one repaint.
    ///
    /// The rule now is deliberately dumb: every painter is rebuilt once per
    /// <see cref="CycleSeconds"/>, and the cycle is SPREAD — a cursor steps to
    /// the next painter at most once a second, so rebuilds never share a frame
    /// and a busy world simply takes longer to sweep.
    ///
    /// The exception is a FRESH wound, which the eye is waiting for: that gets
    /// drawn on the next frame through the painter's cheap path, ahead of the
    /// cycle (see IPaintTarget.PaintFresh).
    /// </summary>
    public interface IPaintTarget
    {
        /// <summary>A mark appeared that must not wait for the cycle. Must be
        /// cheap — polled every frame.</summary>
        bool WantsFreshPass { get; }

        /// <summary>Draw only what just appeared, the cheap way, on top of the
        /// existing composite.</summary>
        void PaintFresh();

        /// <summary>The scheduled full rebuild — this painter's turn.</summary>
        void PaintCycle();
    }

    [DefaultExecutionOrder(1000)] // after the renderer pushed this tick's state
    public sealed class SkinPaintScheduler : MonoBehaviour
    {
        /// <summary>How long between full rebuilds of any one painter, in
        /// unscaled seconds.</summary>
        public const float CycleSeconds = 10f;

        /// <summary>Never two rebuilds closer together than this. With ~19
        /// painters the cycle would want one every half second; one a second is
        /// calmer and simply stretches the sweep to ~19 s, which for grime and
        /// healing is indistinguishable.</summary>
        private const float MinSpacingSeconds = 1f;

        private static SkinPaintScheduler? _instance;
        private static readonly List<IPaintTarget> Targets = new();
        private static int _cursor;
        private static float _nextAdvance;

        public static void Register(IPaintTarget target)
        {
            if (!Targets.Contains(target))
            {
                Targets.Add(target);
            }

            EnsureInstance();
        }

        public static void Unregister(IPaintTarget target)
        {
            var index = Targets.IndexOf(target);
            if (index < 0)
            {
                return;
            }

            Targets.RemoveAt(index);
            // Keep the cursor on the same ring position, or a removal would
            // silently skip whoever shuffled down into this slot.
            if (index < _cursor)
            {
                _cursor--;
            }
        }

        private static void EnsureInstance()
        {
            if (_instance != null)
            {
                return;
            }

            var host = new GameObject("SkinPaintScheduler") { hideFlags = HideFlags.DontSave };
            _instance = host.AddComponent<SkinPaintScheduler>();
            DontDestroyOnLoad(host);
        }

        private void LateUpdate()
        {
            if (Targets.Count == 0)
            {
                return;
            }

            var now = Time.unscaledTime;

            // The scheduled turn goes FIRST. It comes round about once a
            // second, so it costs the fresh lane almost nothing — but checking
            // fresh marks first would let a steady stream of bites starve the
            // cycle completely, and the cycle is what upgrades a fresh
            // rectangle to its seam-free form.
            if (now >= _nextAdvance)
            {
                _nextAdvance = now + Mathf.Max(MinSpacingSeconds, CycleSeconds / Targets.Count);

                if (_cursor >= Targets.Count)
                {
                    _cursor = 0;
                }

                var due = Targets[_cursor];
                _cursor++;
                due?.PaintCycle();
                return; // one repaint per frame, whatever kind it was
            }

            // Fresh marks, at most one per frame — a pack of dogs biting at
            // once must not stack four rebuilds into one frame.
            for (var i = 0; i < Targets.Count; i++)
            {
                var target = Targets[i];
                if (target != null && target.WantsFreshPass)
                {
                    target.PaintFresh();
                    return;
                }
            }
        }

        // No-domain-reload plays keep statics; a stale list would hold painters
        // destroyed with the previous session.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Targets.Clear();
            _cursor = 0;
            _nextAdvance = 0f;
            _instance = null;
        }
    }
}
