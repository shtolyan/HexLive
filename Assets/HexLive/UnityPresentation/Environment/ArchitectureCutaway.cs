#nullable enable
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// §120: the ONE rule for "which parts of a building stand between the
    /// camera and the room, and how are they taken away".
    ///
    /// <para>
    /// The canonical one-hex hut answers this inside <see cref="HutAssembly"/>,
    /// because there the whole house is one mesh and its walls are named bays it
    /// can group into six pairs. A player plan has no such mesh: it is N separate
    /// world objects, and the constructor preview already ranks THOSE by how far
    /// each one faces the camera. This is that ranking, lifted out of the
    /// preview so the constructor and the raised house share it instead of
    /// growing a second cutaway.
    /// </para>
    ///
    /// <para>
    /// A hidden part switches to <see cref="ShadowCastingMode.ShadowsOnly"/>,
    /// never to an inactive GameObject: the room must keep its shadows while its
    /// near wall is invisible, and the piece must keep taking delivered material
    /// exactly as before (this is what the monolith does — see
    /// <c>HutAssembly.SetCutawayVisibility</c>).
    /// </para>
    /// </summary>
    public static class ArchitectureCutaway
    {
        /// <summary>One third of an envelope is taken away, at least one piece.</summary>
        public const int HiddenShare = 3;

        /// <summary>
        /// Fills <paramref name="hidden"/> with the indices of the parts that
        /// stand between <paramref name="camera"/> and the room: the ones whose
        /// outward radius (from <paramref name="centre"/>) points most nearly at
        /// the camera. Deterministic — ties keep list order, so a camera resting
        /// exactly between two parts cannot make them alternate.
        /// </summary>
        public static void RankByCameraFacing(
            IReadOnlyList<Vector2> positions, Vector2 centre, Vector2 camera, List<int> hidden)
        {
            hidden.Clear();
            if (positions == null || positions.Count == 0) return;

            var scores = new float[positions.Count];
            for (var i = 0; i < positions.Count; i++)
            {
                var position = positions[i];
                var toCamera = (camera - position).normalized;
                var radial = position - centre;
                var outward = radial.sqrMagnitude > 0.001f ? radial.normalized : toCamera;
                scores[i] = Vector2.Dot(outward, toCamera);
            }

            var take = Mathf.Max(1, positions.Count / HiddenShare);
            for (var picked = 0; picked < take; picked++)
            {
                var best = -1;
                var bestScore = float.NegativeInfinity;
                for (var i = 0; i < positions.Count; i++)
                {
                    if (hidden.Contains(i)) continue;
                    if (scores[i] <= bestScore) continue;
                    bestScore = scores[i];
                    best = i;
                }

                if (best < 0) return;
                hidden.Add(best);
            }
        }

        /// <summary>
        /// Hides or restores one renderer the cutaway owns. Shadows remain while
        /// the piece itself is invisible.
        /// </summary>
        public static void SetVisible(Renderer? renderer, bool visible)
        {
            if (renderer == null) return;
            var mode = visible ? ShadowCastingMode.On : ShadowCastingMode.ShadowsOnly;
            if (renderer.shadowCastingMode != mode) renderer.shadowCastingMode = mode;
        }
    }
}
