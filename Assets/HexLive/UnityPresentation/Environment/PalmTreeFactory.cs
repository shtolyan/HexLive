#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Spec §54.2: the standing palm is the assembled <c>palm_final</c> prefab
    /// (Kenney trunk + procedural pinnate frond crown, built in Blender). Only the
    /// BIG palm exists now — the 2-segment small palm is retired. The prefab is
    /// authored at its true 1:1 world size in Blender, so it is instantiated AS-IS —
    /// NO fit-scaling (the whole prefab dropped onto the scene is already correct);
    /// the caller must NOT run FitObjectPrefab on it either.
    /// </summary>
    public static class PalmTreeFactory
    {
        public static bool IsPalm(string definitionId) => definitionId == "tree.palm";

        public static GameObject? Build(string definitionId)
        {
            var prefab = Resources.Load<GameObject>("HexLive/Objects/palm_final");
            if (prefab == null)
            {
                return null; // no prefab — let the caller fall back
            }

            var palm = Object.Instantiate(prefab);
            palm.name = $"Palm {definitionId}";
            return palm; // 1:1 authored size — no scaling
        }
    }
}
