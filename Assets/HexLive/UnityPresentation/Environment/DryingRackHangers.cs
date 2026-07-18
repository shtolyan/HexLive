using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// §35.5B: the drying rack's 8 invisible hanger slots — local offsets from
    /// the rack's anchor (= its junction anchor; the assembled prefab renders
    /// yaw-0 at that same anchor, so these offsets are world-stable). Four
    /// garments drape over the top rail, four over the lower one. SINGLE SOURCE
    /// OF TRUTH: the prefab assembly (drying_rack_final) and the renderer's
    /// hung-garment placement both read these numbers.
    /// </summary>
    public static class DryingRackHangers
    {
        /// Rail heights in the prefab (world units, ground = 0).
        public const float TopRailY = 0.78f;
        public const float LowRailY = 0.50f;

        /// How far the hung garment's TOP edge pokes above its rail (draped over).
        public const float DrapeOverlap = 0.06f;

        /// A long garment on the low rail must not clip into the ground.
        public const float GroundClearance = 0.04f;

        /// One rail-attach point per hanging garment: 4 on the top rail
        /// (front), 4 on the lower rail (back) — staggered in Z so the rows
        /// read separately. Y is the RAIL height; the renderer drops each
        /// garment by its own half-height so it hangs from the rail top.
        public static readonly Vector3[] Slots =
        {
            new(-0.33f, TopRailY, 0.10f),
            new(-0.11f, TopRailY, 0.10f),
            new(0.11f, TopRailY, 0.10f),
            new(0.33f, TopRailY, 0.10f),
            new(-0.33f, LowRailY, -0.08f),
            new(-0.11f, LowRailY, -0.08f),
            new(0.11f, LowRailY, -0.08f),
            new(0.33f, LowRailY, -0.08f)
        };

        /// Slot for the i-th garment (ranked by object id) — wraps past 8 so an
        /// over-capacity glitch never throws, it just doubles up a hanger.
        public static Vector3 Slot(int index)
        {
            var slots = Slots;
            return slots[((index % slots.Length) + slots.Length) % slots.Length];
        }
    }
}
