using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>§133: twelve authored hanger sockets on the wardrobe rail.</summary>
    public static class WardrobeHangers
    {
        public const int SlotCount = 12;
        private const int ShoeShelfSlotCount = 4;
        // Fallback only: normal rendering reads the named ClothingSlot_00…11
        // transforms directly from furniture.wardrobe.fbx through
        // WardrobeAssembly. These values retain the last verified export basis
        // for a missing/corrupt asset rather than becoming a second layout.
        private const float FirstAlong = -0.3575f;
        private const float StepAlong = 0.065f;
        private const float RailY = 1.32f;
        private const float RoomSideX = -0.015f;

        /// Centre of the garment on its hanger. Long garments are grounded by
        /// the renderer after their actual bounds are measured.
        public static Vector3 Slot(int index)
        {
            var wrapped = ((index % SlotCount) + SlotCount) % SlotCount;
            // The exported wardrobe uses the same footprint basis as bed.basic:
            // Blender +Y (Unity local Z) spans the occupied junctions.
            return new Vector3(RoomSideX, RailY, FirstAlong + wrapped * StepAlong);
        }

        /// <summary>
        /// Four places on the wardrobe's lower board. Footwear is stored here
        /// upright, never on a rail hanger. This shares the wardrobe's local
        /// axis and is rotated only by the furniture root's six-way yaw. The Y
        /// coordinate is the shelf surface; <see cref="GroundFootwearOnShelf"/>
        /// lifts each differently sized mesh by its own bounds.
        /// </summary>
        public static Vector3 ShoeShelfSlot(int index)
        {
            var wrapped = ((index % ShoeShelfSlotCount) + ShoeShelfSlotCount) % ShoeShelfSlotCount;
            return new Vector3(RoomSideX, 0.19f, -0.24f + wrapped * 0.16f);
        }

        /// <summary>
        /// Seats footwear by its measured bottom on the authored shelf surface.
        /// Shoe meshes have different shaft heights, so the shelf Y is a
        /// surface, never a universal centre coordinate.
        /// </summary>
        public static Vector3 GroundFootwearOnShelf(Transform footwear, int index)
        {
            var shelf = ShoeShelfSlot(index);
            var renderers = footwear.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return shelf;

            var bottom = renderers[0].bounds.min.y;
            for (var rendererIndex = 1; rendererIndex < renderers.Length; rendererIndex++)
                bottom = Mathf.Min(bottom, renderers[rendererIndex].bounds.min.y);

            return new Vector3(
                shelf.x,
                shelf.y + footwear.position.y - bottom,
                shelf.z);
        }
    }
}
