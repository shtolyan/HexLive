#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>Player-safe loader for the authored, junction-sized wardrobe.</summary>
    public static class WardrobeAssembly
    {
        public const string ResourcePath = "HexLive/Objects/furniture.wardrobe";
        private static readonly Dictionary<int, Vector3> ClothingSlots = new();
        private static bool _clothingSlotsResolved;

        /// <summary>
        /// Returns the authored garment socket in the imported model's local
        /// basis.  The frame, its rail, the garment and the occupied hanger
        /// therefore share the exact same exported coordinate source.
        /// </summary>
        public static bool TryGetClothingSlotLocal(int index, out Vector3 localPosition)
        {
            localPosition = default;
            ResolveClothingSlots();

            var slotIndex = ((index % WardrobeHangers.SlotCount) + WardrobeHangers.SlotCount) %
                WardrobeHangers.SlotCount;
            return ClothingSlots.TryGetValue(slotIndex, out localPosition);
        }

        private static void ResolveClothingSlots()
        {
            if (_clothingSlotsResolved) return;
            _clothingSlotsResolved = true;

            var prefab = HexLive.UnityPresentation.Content.AtomicResources.Load<GameObject>(ResourcePath);
            if (prefab == null) return;

            // The FBX's imported root carries Unity's Blender-axis conversion.
            // A socket measured relative to prefab.transform is still in the
            // model's private basis (and puts Y along the rail). Build the same
            // identity wrapper used by BuildFinished, then measure its actual
            // Unity-local position once and cache it for every garment view.
            var wrapper = new GameObject("Wardrobe socket probe")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            var model = Object.Instantiate(prefab, wrapper.transform);
            model.hideFlags = HideFlags.HideAndDontSave;
            for (var i = 0; i < WardrobeHangers.SlotCount; i++)
            {
                var slot = FindDescendant(model.transform, $"ClothingSlot_{i:D2}");
                if (slot != null)
                    ClothingSlots[i] = wrapper.transform.InverseTransformPoint(slot.position);
            }

            if (Application.isPlaying) Object.Destroy(wrapper);
            else Object.DestroyImmediate(wrapper);
        }

        public static GameObject? BuildFinished()
        {
            var prefab = HexLive.UnityPresentation.Content.AtomicResources.Load<GameObject>(ResourcePath);
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

        private static Transform? FindDescendant(Transform root, string name)
        {
            if (root.name == name) return root;
            for (var i = 0; i < root.childCount; i++)
            {
                var found = FindDescendant(root.GetChild(i), name);
                if (found != null) return found;
            }

            return null;
        }
    }
}
