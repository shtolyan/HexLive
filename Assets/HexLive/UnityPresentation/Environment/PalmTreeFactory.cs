#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Spec §54.2: builds a palm tree as a STACK of trunk-segment logs topped by
    /// a leaf crown — the exact pieces that drop when it's felled, so a big palm
    /// (3 segments) yields 3 logs + a crown, a small palm (2 segments) yields 2 +
    /// a crown, and every dropped log is the same size as a standing segment.
    ///
    /// Each segment is the resource.log prefab, sized by the shared ObjectFit
    /// table exactly as a ground log — then rotated upright and stacked. Because
    /// it's already absolute-sized, the caller must NOT run FitObjectPrefab on it.
    /// </summary>
    public static class PalmTreeFactory
    {
        public static bool IsPalm(string definitionId) =>
            definitionId == "tree.palm" || definitionId == "tree.palm_small";

        public static int SegmentCount(string definitionId) =>
            definitionId == "tree.palm_small" ? 2 : 3;

        public static GameObject? Build(string definitionId)
        {
            var logPrefab = Resources.Load<GameObject>("HexLive/Objects/resource.log");
            if (logPrefab == null)
            {
                return null; // no AI log yet — let the caller fall back
            }

            var segments = SegmentCount(definitionId);
            var root = new GameObject($"Palm {definitionId} ({segments} segments)");

            var runningY = 0f;
            for (var i = 0; i < segments; i++)
            {
                var seg = Object.Instantiate(logPrefab, root.transform);
                seg.name = $"Segment {i}";
                // Same physical size as a dropped log.
                seg.transform.localScale *= ObjectFit.FitScaleFactor(seg, "resource.log");
                // Stand the log upright: its length runs along local X → rotate to +Y.
                // A little yaw per segment so the stacked trunk doesn't look extruded.
                seg.transform.localRotation = Quaternion.Euler(0f, i * 23f, 90f);
                runningY = StackOnTop(seg, runningY);
            }

            // Spec §54.2: crown the trunk with a fluffy cluster of leaves (NO
            // trunk stub) — a fuller crown on the big palm than the small one,
            // matching each size's leaf drop.
            var segmentLen = runningY / Mathf.Max(1, segments);
            var frondCount = segments >= 3
                ? HexLive.Simulation.Runtime.SimBalance.BigPalmCrownLeaves
                : HexLive.Simulation.Runtime.SimBalance.SmallPalmCrownLeaves;
            var crown = PalmCrownFactory.Build(segmentLen * 1.35f, frondCount);
            if (crown != null)
            {
                crown.transform.SetParent(root.transform, false);
                // Sit the fronds' base just below the trunk top so they sprout
                // from it rather than floating.
                crown.transform.localPosition = new Vector3(0f, runningY - segmentLen * 0.12f, 0f);
            }

            return root;
        }

        // Position a freshly-scaled/rotated child so it sits centred on the trunk
        // axis (parent x,z) with the bottom of its world bounds at baseY. The AI
        // log's cross-section centre isn't at its mesh origin and each segment has
        // a little yaw, so without re-centring the segments drift sideways — this
        // snaps every one back onto the trunk line. Returns the new top.
        private static float StackOnTop(GameObject go, float baseY)
        {
            if (!ObjectFit.WorldBounds(go, out var b))
            {
                return baseY;
            }

            var parentPos = go.transform.parent != null ? go.transform.parent.position : Vector3.zero;
            var dx = parentPos.x - b.center.x;
            var dz = parentPos.z - b.center.z;
            var lift = baseY - b.min.y;
            go.transform.localPosition += new Vector3(dx, lift, dz);
            return baseY + b.size.y;
        }
    }
}
