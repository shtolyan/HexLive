#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Spec §54.2: the standing palm is the approved <c>palm_final</c>
    /// composition introduced by b3b47b4a: the Kenney trunk from the first
    /// in-game palm plus the separately authored pinnate-frond crown. The
    /// native FBX is a build-safe mirror of that exact GLB, not another model.
    /// It is authored at 1:1 world size and must not be fit-scaled.
    /// </summary>
    public static class PalmTreeFactory
    {
        public static bool IsPalm(string definitionId) => definitionId == "tree.palm";

        public static GameObject? Build(string definitionId)
        {
            var prefab = Resources.Load<GameObject>(
                "HexLive/Objects/palm_final_native");
            if (prefab == null || !ObjectFit.HasRenderableGeometry(prefab))
            {
                return null;
            }

            var palm = Object.Instantiate(prefab);
            palm.name = $"Palm {definitionId}";
            return palm;
        }
    }
}
