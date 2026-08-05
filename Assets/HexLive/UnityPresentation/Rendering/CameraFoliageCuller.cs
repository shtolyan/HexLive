#nullable enable
using HexLive.UnityPresentation.Environment;
using HexLive.UnityPresentation.Input;
using UnityEngine;

namespace HexLive.UnityPresentation.Rendering
{
    /// <summary>
    /// Spec §112: the palms step out of the shot. The rule is a DEPTH CUT at the
    /// followed colonist: every <see cref="FoliageOccluder"/> standing nearer to
    /// the lens than she does is hidden for that frame, everything at her depth
    /// or beyond is left alone. So the foreground clears and the backdrop — the
    /// grove she is walking into — stays a grove.
    ///
    /// With no colonist to frame (free camera over the ground) there is no plane
    /// to cut at, and the culler clears only the sight line to the pivot. Both
    /// modes also clear anything pressed right against the lens — the camera
    /// standing INSIDE a crown, which is what fills half the screen with fronds.
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

        [Tooltip("How far IN FRONT of the followed colonist the cut plane sits. " +
                 "Leaves within this margin of her own depth stay — the palm she " +
                 "is chopping must not vanish out from under the axe.")]
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

            // The depth rule needs a subject. While a colonist is being followed
            // the cut is her own plane: everything standing NEARER to the lens
            // than she does goes, everything at her depth or beyond stays —
            // whether or not it happens to sit on the sight line. With no
            // subject (free camera over the ground) that rule would strip the
            // whole island from a top-down shot, so there the culler falls back
            // to clearing just the sight line.
            var forward = transform.forward;
            var planeCut = _rig != null && _rig.HasFramedSubject;
            var cutDepth = Vector3.Dot(toFocus, forward) - _focusClearance;

            for (var i = 0; i < occluders.Count; i++)
            {
                var occluder = occluders[i];
                if (occluder == null || !occluder.isActiveAndEnabled)
                {
                    continue;
                }

                var toRoot = occluder.transform.position - origin;
                if (toRoot.sqrMagnitude > coarseRangeSqr)
                {
                    continue; // far behind the framed point — it cannot be in the way
                }

                if (planeCut)
                {
                    // Where the plant STANDS decides it — the trunk's foot, not
                    // the swaying crown, so the verdict is the one a player
                    // would read off the ground: this palm is in front of her.
                    var depth = Vector3.Dot(toRoot, forward);
                    if (depth > 0f && depth < cutDepth)
                    {
                        occluder.BlockUntil(blockUntil);
                        continue;
                    }
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

                    // Whatever survived the depth cut still goes if it is pressed
                    // against the lens (a crown rooted behind her can still hang
                    // into the objective), or — with no subject — if it stands on
                    // the sight line.
                    if (bounds.SqrDistance(origin) <= clearanceSqr ||
                        (!planeCut && bounds.IntersectRay(ray, out var hit) && hit < sightLength))
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
