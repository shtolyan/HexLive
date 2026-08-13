using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>§133: twelve authored hanger sockets on the wardrobe rail.</summary>
    public static class WardrobeHangers
    {
        public const int SlotCount = 12;
        // These are the Unity-local coordinates of the named ClothingSlot_00
        // exported by export_wardrobe_module.py.  Blender's forward axis is
        // exported as Unity -Z: slot 00 therefore lands at +Z and the slot
        // sequence proceeds toward -Z.  Do not mirror these values to make a
        // particular house yaw look right — the common furniture root owns
        // all six orientations.
        private const float FirstAlong = 0.3575f;
        private const float StepAlong = -0.065f;
        private const float RailY = 1.32f;
        private const float RoomSideX = 0.015f;

        /// Centre of the garment on its hanger. Long garments are grounded by
        /// the renderer after their actual bounds are measured.
        public static Vector3 Slot(int index)
        {
            var wrapped = ((index % SlotCount) + SlotCount) % SlotCount;
            // The exported wardrobe uses the same footprint basis as bed.basic:
            // Blender +Y (Unity local Z) spans the occupied junctions.
            return new Vector3(RoomSideX, RailY, FirstAlong + wrapped * StepAlong);
        }
    }
}
