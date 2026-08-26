#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Spec §54.2: the felled-palm stump (the sim object <c>stump.palm</c>) — a
    /// short standing log with the ring-cut face on top. Its mesh, bark, cut
    /// face and growth rings are owned by object/stump.palm; it never borrows a
    /// different object's bundle or replaces missing art with a cylinder.
    /// </summary>
    public static class StumpFactory
    {
        public const float Height = 0.30f;         // how far the stump sticks up
        public const float BaseLift = Height * 0.5f; // lift so its base sits on the ground

        public static GameObject? Build(float hexRadius)
        {
            _ = hexRadius; // authored at the canonical 1:1 world scale
            var prefab = WorldPropResources.Load("stump.palm");
            if (prefab == null || !ObjectFit.HasRenderableGeometry(prefab))
            {
                return null;
            }

            var stump = Object.Instantiate(prefab);
            stump.name = "Stump";
            if (!ObjectFit.HasRenderableGeometry(stump))
            {
                Object.Destroy(stump);
                return null;
            }

            RemoveColliders(stump);
            return stump;
        }

        private static void RemoveColliders(GameObject stump)
        {
            foreach (var col in stump.GetComponentsInChildren<Collider>()) Object.Destroy(col);
        }
    }
}
