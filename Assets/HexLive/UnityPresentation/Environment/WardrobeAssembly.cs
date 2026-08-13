#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>Player-safe loader for the authored, junction-sized wardrobe.</summary>
    public static class WardrobeAssembly
    {
        public const string ResourcePath = "HexLive/Objects/furniture.wardrobe";

        public static GameObject? BuildFinished()
        {
            var prefab = Resources.Load<GameObject>(ResourcePath);
            if (prefab == null) return null;

            // Same contract as BedAssembly: presentation owns an identity root;
            // the imported FBX keeps Unity's native Blender-axis transform on
            // its child. Placement code may now assign only a six-way yaw.
            var root = new GameObject("Wardrobe furniture (authored)");
            var model = Object.Instantiate(prefab, root.transform);
            model.name = "furniture.wardrobe model";
            foreach (var collider in model.GetComponentsInChildren<Collider>(true))
            {
                if (Application.isPlaying) Object.Destroy(collider);
                else Object.DestroyImmediate(collider);
            }
            return root;
        }
    }
}
