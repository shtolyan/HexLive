#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Spec §54.2/§152: each felled crown is one self-contained owner bundle.
    /// Fronds and their materials are duplicated intentionally: loading a crown
    /// must never require object/resource.palm_leaf or the standing tree.
    /// </summary>
    public static class PalmCrownFactory
    {
        public static GameObject? Build(string id, float frondLength, int frondCount)
        {
            _ = frondLength;
            _ = frondCount;
            var prefab = WorldPropResources.Load(id);
            if (prefab == null || !ObjectFit.HasRenderableGeometry(prefab))
            {
                return null;
            }

            var root = Object.Instantiate(prefab);
            root.name = "PalmCrown";
            if (!ObjectFit.HasRenderableGeometry(root))
            {
                Object.Destroy(root);
                return null;
            }
            return root;
        }
    }
}
