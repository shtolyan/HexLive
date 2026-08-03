#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Spec §112: marks a view as leaves the camera is allowed to look THROUGH.
    /// <see cref="Rendering.CameraFoliageCuller"/> asks every live marker, once a
    /// frame, whether it stands between the lens and what the rig is framing, and
    /// hides the ones that do.
    ///
    /// "Hidden" here means <see cref="ShadowCastingMode.ShadowsOnly"/>, not a
    /// disabled renderer: the palm's shadow stays on the sand, so the ground keeps
    /// its dappled light and only the geometry in the way disappears. The original
    /// mode of each renderer is remembered, so a piece authored with shadows OFF
    /// comes back with shadows off.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FoliageOccluder : MonoBehaviour
    {
        private static readonly List<FoliageOccluder> LiveOccluders = new();

        /// <summary>Every marker currently in the scene, in creation order.</summary>
        public static IReadOnlyList<FoliageOccluder> All => LiveOccluders;

        /// <summary>
        /// Which world objects are "leaves": the standing palms, the dropped
        /// crowns and the yucca stalks. Everything else (rocks, fires, beds,
        /// bodies) stays put — a camera that dissolved furniture would read as a
        /// rendering bug, not as a courtesy.
        /// </summary>
        public static bool IsFoliage(string definitionId) =>
            definitionId.StartsWith("tree.", StringComparison.Ordinal) ||
            definitionId.StartsWith("resource.palm_crown", StringComparison.Ordinal) ||
            definitionId == "plant.yucca";

        private Renderer[] _renderers = Array.Empty<Renderer>();
        private ShadowCastingMode[] _shadowModes = Array.Empty<ShadowCastingMode>();
        private bool _hidden;
        private float _blockedUntil;

        private void Awake()
        {
            _renderers = GetComponentsInChildren<Renderer>(true);
            _shadowModes = new ShadowCastingMode[_renderers.Length];
            for (var i = 0; i < _renderers.Length; i++)
            {
                _shadowModes[i] = _renderers[i].shadowCastingMode;
            }

            LiveOccluders.Add(this);
        }

        private void OnDestroy()
        {
            LiveOccluders.Remove(this);
        }

        /// <summary>The per-piece world bounds the culler ray-tests against.</summary>
        public IReadOnlyList<Renderer> Renderers => _renderers;

        /// <summary>
        /// Keeps the view out of the shot until <paramref name="until"/> (unscaled
        /// time). The deadline is a de-flicker grace period: a frond that grazes
        /// the sight line for a single frame must not strobe.
        /// </summary>
        public void BlockUntil(float until)
        {
            if (until > _blockedUntil)
            {
                _blockedUntil = until;
            }
        }

        /// <summary>Puts the view back at once (the culler going away, a kill switch).</summary>
        public void ClearBlock()
        {
            _blockedUntil = 0f;
            ApplyVisibility(float.MaxValue);
        }

        /// <summary>Applies the accumulated verdict. Called once a frame by the culler.</summary>
        public void ApplyVisibility(float unscaledTime)
        {
            var shouldHide = unscaledTime < _blockedUntil;
            if (shouldHide == _hidden)
            {
                return;
            }

            _hidden = shouldHide;
            for (var i = 0; i < _renderers.Length; i++)
            {
                var renderer = _renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                renderer.shadowCastingMode = shouldHide
                    ? ShadowCastingMode.ShadowsOnly
                    : _shadowModes[i];
            }
        }
    }
}
