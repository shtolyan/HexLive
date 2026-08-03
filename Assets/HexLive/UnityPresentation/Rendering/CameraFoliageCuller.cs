#nullable enable
using HexLive.UnityPresentation.Environment;
using HexLive.UnityPresentation.Input;
using UnityEngine;

namespace HexLive.UnityPresentation.Rendering
{
    /// <summary>
    /// Spec §112: the palms step out of the shot. Once a frame the culler draws
    /// the sight line — from the lens to whatever the rig is framing (the followed
    /// colonist in orbit mode, the ground pivot in free mode) — and hides every
    /// <see cref="FoliageOccluder"/> whose geometry sits on it, plus anything
    /// pressed right against the lens (the camera standing INSIDE a crown, which
    /// is what fills half the screen with fronds).
    ///
    /// Hiding is shadows-only and re-checked every frame, so nothing is
    /// permanently deleted: step aside and the palm is back. The short restore
    /// delay is the anti-strobe: a frond that grazes the line for one frame must
    /// not blink.
    ///
    /// Lives on the main camera and runs after the camera controllers (execution
    /// order 200) so it tests the pose the frame will actually render.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(200)]
    public sealed class CameraFoliageCuller : MonoBehaviour
    {
        /// <summary>Kill switch — off puts every leaf back on the next frame.</summary>
        public static bool Enabled = true;

        [Tooltip("How far around the sight line leaves are cleared, in world units.")]
        [SerializeField] private float _sightPadding = 0.35f;

        [Tooltip("Anything this close to the lens is cleared regardless of the sight line.")]
        [SerializeField] private float _lensClearance = 1.6f;

        [Tooltip("Seconds a cleared leaf stays cleared after it stops blocking (anti-strobe).")]
        [SerializeField] private float _restoreDelay = 0.15f;

        [Tooltip("Leaves this close to the framed point are kept — the palm she is " +
                 "chopping must not vanish out from under the axe.")]
        [SerializeField] private float _focusClearance = 1f;

        [Tooltip("Fallback framing distance when no camera rig is present.")]
        [SerializeField] private float _fallbackFocusDistance = 8f;

        private RtsCameraController? _rig;

        private void LateUpdate()
        {
            var occluders = FoliageOccluder.All;
            if (occluders.Count == 0)
            {
                return;
            }

            var now = Time.unscaledTime;

            if (Enabled)
            {
                MarkBlockers(occluders, now);
            }

            for (var i = 0; i < occluders.Count; i++)
            {
                occluders[i].ApplyVisibility(now);
            }
        }

        // Turning the culler off must not leave a palm invisible forever.
        private void OnDisable()
        {
            var occluders = FoliageOccluder.All;
            for (var i = 0; i < occluders.Count; i++)
            {
                occluders[i].ClearBlock();
            }
        }

        private void MarkBlockers(System.Collections.Generic.IReadOnlyList<FoliageOccluder> occluders, float now)
        {
            var origin = transform.position;
            var focus = ResolveFocusPoint();
            var toFocus = focus - origin;
            var distance = toFocus.magnitude;
            if (distance < 0.01f)
            {
                return;
            }

            var ray = new Ray(origin, toFocus / distance);
            var blockUntil = now + _restoreDelay;
            var sightLength = Mathf.Max(0.1f, distance - _focusClearance);
            var coarseRange = distance + 8f; // a palm's base is up to ~8 wu from its crown
            var coarseRangeSqr = coarseRange * coarseRange;
            var clearanceSqr = _lensClearance * _lensClearance;
            var padding = _sightPadding * 2f;

            for (var i = 0; i < occluders.Count; i++)
            {
                var occluder = occluders[i];
                if (occluder == null || !occluder.isActiveAndEnabled)
                {
                    continue;
                }

                if ((occluder.transform.position - origin).sqrMagnitude > coarseRangeSqr)
                {
                    continue; // far behind the framed point — it cannot be in the way
                }

                var renderers = occluder.Renderers;
                for (var r = 0; r < renderers.Count; r++)
                {
                    var renderer = renderers[r];
                    if (renderer == null)
                    {
                        continue;
                    }

                    var bounds = renderer.bounds;
                    bounds.Expand(padding);

                    // Pressed against the lens, or standing on the sight line
                    // between the lens and what is being framed.
                    if (bounds.SqrDistance(origin) <= clearanceSqr ||
                        (bounds.IntersectRay(ray, out var hit) && hit < sightLength))
                    {
                        occluder.BlockUntil(blockUntil);
                        break;
                    }
                }
            }
        }

        // What the frame is about: the followed colonist while orbiting, the
        // ground pivot while panning. With no rig at all (test scenes) aim a
        // fixed distance down the lens so the near-camera clearing still works.
        private Vector3 ResolveFocusPoint()
        {
            if (_rig == null)
            {
                _rig = GetComponent<RtsCameraController>();
            }

            return _rig != null
                ? _rig.FocusPoint
                : transform.position + transform.forward * _fallbackFocusDistance;
        }
    }
}
