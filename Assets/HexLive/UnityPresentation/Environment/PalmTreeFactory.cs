#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Spec §54.2: the standing palm is assembled at runtime from the three
    /// authored trunk segments and the same procedural crown used by a felled
    /// palm. Keeping the pieces separate is intentional: the standing tree must
    /// visibly consist of the logs and leaves it later drops.
    /// </summary>
    public static class PalmTreeFactory
    {
        public static bool IsPalm(string definitionId) => definitionId == "tree.palm";

        public static GameObject? Build(string definitionId)
        {
            var segments = new GameObject?[3];
            for (var i = 0; i < segments.Length; i++)
            {
                segments[i] = Resources.Load<GameObject>($"HexLive/Objects/palm_seg{i}");
                if (segments[i] == null)
                {
                    return null;
                }
            }

            var palm = new GameObject($"Palm {definitionId}");
            var segmentHeight = ObjectFit.PalmSegmentLength *
                HexLive.UnityPresentation.Spatial.SimulationUnityMapper.HexRadius;
            var top = 0f;
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = Object.Instantiate(segments[i]!, palm.transform);
                segment.name = $"Trunk {i + 1}";
                segment.transform.localPosition = Vector3.zero;
                segment.transform.localRotation = Quaternion.identity;

                if (ObjectFit.WorldBounds(segment, out var before) && before.size.y > 0.0001f)
                {
                    segment.transform.localScale *= segmentHeight / before.size.y;
                    if (ObjectFit.WorldBounds(segment, out var after))
                    {
                        segment.transform.localPosition += Vector3.up * (top - after.min.y);
                    }
                }

                top += segmentHeight;
            }

            var crown = PalmCrownFactory.Build(
                HexLive.UnityPresentation.Spatial.SimulationUnityMapper.HexRadius * 0.85f,
                HexLive.Simulation.Runtime.SimBalance.BigPalmCrownLeaves);
            if (crown == null)
            {
                Object.Destroy(palm);
                return null;
            }

            crown.name = "Crown";
            crown.transform.SetParent(palm.transform, false);
            crown.transform.localPosition = Vector3.up * top;
            palm.name = $"Palm {definitionId}";
            return palm;
        }
    }
}
