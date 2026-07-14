#nullable enable
using UnityEngine;
using HexLive.Simulation.Runtime;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Spec §54.2: the palm crown (верхушка) is a FLUFFY CLUSTER OF FRONDS — no
    /// trunk. It is assembled from EXACTLY <see cref="SimBalance.PalmCrownLeafYield"/>
    /// copies of the <c>palm_frond</c> prefab (the leaf mesh re-pivoted at its
    /// STEM, so a frond radiates from one point) — the SAME count that drops as
    /// loose leaves when the crown is chopped. So what you see on the palm is
    /// exactly what you get: change the one knob and both the look and the yield
    /// move together. Fronds are fanned by the golden angle across a few pitch
    /// bands for an even, bushy head.
    /// </summary>
    public static class PalmCrownFactory
    {
        // Steep-up → outward → drooping: cycled so any frond count spreads full.
        private static readonly float[] Pitches = { -58f, -40f, -22f, -4f, 16f, 30f };

        public static GameObject? Build(float frondLength, int frondCount)
        {
            var frondPrefab = Resources.Load<GameObject>("HexLive/Objects/palm_frond");
            if (frondPrefab == null)
            {
                return null;
            }

            var root = new GameObject("PalmCrown");

            // Scale a frond so its length (blade tip) reaches frondLength.
            var probe = Object.Instantiate(frondPrefab);
            ObjectFit.WorldBounds(probe, out var pb);
            var nativeLen = Mathf.Max(pb.size.x, Mathf.Max(pb.size.y, pb.size.z));
            var frondScale = nativeLen > 0.0001f ? frondLength / nativeLen : 1f;
            Object.DestroyImmediate(probe);

            // EXACTLY the drop count of fronds — so the crown you chop yields the
            // same number of leaves you saw on it (per palm size).
            var count = Mathf.Max(1, frondCount);
            for (var i = 0; i < count; i++)
            {
                var frond = Object.Instantiate(frondPrefab, root.transform);
                frond.name = "Frond";
                // A touch smaller toward the drooping outer bands keeps it tidy.
                var pitch = Pitches[i % Pitches.Length];
                var ringScale = pitch > 0f ? 0.85f : 1f;
                frond.transform.localScale = Vector3.one * (frondScale * ringScale);
                // Golden-angle yaw spreads any count evenly around the head; the
                // stem sits at the crown centre and pitch arcs the blade up/out.
                var yaw = i * 137.5f;
                var twist = (i % 3 - 1) * 10f;
                frond.transform.localRotation = Quaternion.Euler(pitch, yaw, twist);
                frond.transform.localPosition = Vector3.zero;
            }

            return root;
        }
    }
}
