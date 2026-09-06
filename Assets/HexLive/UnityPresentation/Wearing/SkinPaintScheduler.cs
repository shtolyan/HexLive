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

    /// <summary>Optional initial-appearance acknowledgement. Ordinary ambient
    /// painters (for example mob pelts) keep implementing only IPaintTarget;
    /// corpse skin and garments opt into this stronger contract.</summary>
    public interface IPresentationPaintTarget : IPaintTarget
    {
        /// <summary>True only when the current requested state is already
        /// materialized in the target textures.</summary>
        bool PresentationReady { get; }

        /// <summary>One bounded attempt at materializing initial presentation.
        /// False may mean asynchronous geometry or RT budget is still pending.</summary>
        bool TryPaintPresentation();
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
        private static readonly List<IPresentationPaintTarget> PresentationQueue = new();
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
            if (target is IPresentationPaintTarget presentationTarget)
            {
                PresentationQueue.Remove(presentationTarget);
            }
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

        /// <summary>
        /// Bug #354: a restored corpse stays render-gated until its historical
        /// wounds and garment wear have reached the GPU. This queue is a
        /// priority lane, but remains bounded to one painter per frame.
        /// </summary>
        public static void RequestPresentationPass(IPresentationPaintTarget target)
        {
            if (target != null && !target.PresentationReady &&
                !PresentationQueue.Contains(target))
            {
                PresentationQueue.Add(target);
            }

            EnsureInstance();
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

            // Initial restored appearance is correctness, not slow ambience:
            // process one requested painter before the ordinary cycle/fresh
            // lanes. An async geometry target moves to the back so it cannot
            // block ready garments behind it.
            while (PresentationQueue.Count > 0)
            {
                var target = PresentationQueue[0];
                PresentationQueue.RemoveAt(0);
                if (target is Object unityTarget && unityTarget == null)
                {
                    continue;
                }

                if (!target.PresentationReady && !target.TryPaintPresentation())
                {
                    PresentationQueue.Add(target);
                }
                return; // never more than one presentation repaint per frame
            }

            // Дыра «вечного трупа»: painter, добавленный на НЕАКТИВНЫЙ объект
            // (тёплое появление собирает вью выключенным), у которого объект
            // умер до первой активации, не получает OnDestroy — Unregister
            // не зовётся, а интерфейсная ссылка проходит `!= null` по ссылке,
            // не по Unity-жизни. Каждый такой труп съедал односекундный ход
            // кольца, и за длинную сессию обход раздувался до минут — сушка
            // глянца «не доезжала» до перепечатки часами. Чистим по месту.
            for (var i = Targets.Count - 1; i >= 0; i--)
            {
                if (Targets[i] is Object unityTarget && unityTarget == null)
                {
                    Targets.RemoveAt(i);
                    if (i < _cursor)
                    {
                        _cursor--;
                    }
                }
            }

            if (Targets.Count == 0)
            {
                return;
            }

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
            PresentationQueue.Clear();
            _cursor = 0;
            _nextAdvance = 0f;
            _instance = null;
        }
    }
}
