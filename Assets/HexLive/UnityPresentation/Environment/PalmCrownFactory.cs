#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Spec §54.2: a felled crown is assembled from the same separately authored
    /// <c>leaf_final</c> frond used by the approved standing <c>palm_final</c>.
    /// The native FBX mirrors that exact GLB so the mesh survives Player builds.
    /// </summary>
    public static class PalmCrownFactory
    {
        private static readonly float[] Pitches = { -58f, -40f, -22f, -4f, 16f, 30f };

        public static GameObject? Build(float frondLength, int frondCount)
        {
            var frondPrefab = HexLive.UnityPresentation.Content.AtomicResources.Load<GameObject>(
                "HexLive/Objects/palm_frond_native");
            if (frondPrefab == null || !ObjectFit.HasRenderableGeometry(frondPrefab))
            {
                return null;
            }

            var root = new GameObject("PalmCrown");
            var probe = Object.Instantiate(frondPrefab);
            if (!ObjectFit.HasRenderableGeometry(probe) ||
                !ObjectFit.WorldBounds(probe, out var probeBounds))
            {
                DestroyProbe(probe);
                Object.Destroy(root);
                return null;
            }

            var nativeLength = Mathf.Max(
                probeBounds.size.x,
                Mathf.Max(probeBounds.size.y, probeBounds.size.z));
            DestroyProbe(probe);
            if (nativeLength <= 0.0001f)
            {
                Object.Destroy(root);
                return null;
            }

            var frondScale = frondLength / nativeLength;
            var count = Mathf.Max(1, frondCount);
            for (var i = 0; i < count; i++)
            {
                var frond = Object.Instantiate(frondPrefab, root.transform);
                frond.name = "Frond";
                var pitch = Pitches[i % Pitches.Length];
                var ringScale = pitch > 0f ? 0.85f : 1f;
                frond.transform.localScale = Vector3.one * (frondScale * ringScale);
                frond.transform.localRotation = Quaternion.Euler(
                    pitch, i * 137.5f, (i % 3 - 1) * 10f);
                frond.transform.localPosition = Vector3.zero;
            }

            return root;
        }

        private static void DestroyProbe(GameObject probe)
        {
            if (Application.isPlaying)
            {
                probe.SetActive(false);
                Object.Destroy(probe);
            }
            else
            {
                Object.DestroyImmediate(probe);
            }
        }
    }
}
