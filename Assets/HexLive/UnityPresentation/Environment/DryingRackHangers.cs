using UnityEngine;
using HexLive.Simulation.Content;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// §35.5B: the drying rack's 8 invisible hanger slots — local offsets from
    /// the identity furniture wrapper. Four garments drape over the top rail,
    /// four over the lower one. DryingSlot_00…07 from the same native model
    /// own these positions; the renderer applies the rack's six-way yaw.
    /// </summary>
    public static class DryingRackHangers
    {
        private static GameObject _resolvedPrefab;
        private static readonly Vector3[] AuthoredSlots = new Vector3[8];
        private static bool _hasAuthoredSlots;
        /// Legacy fallback only, for previously published content without sockets.
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
            ResolveAuthoredSlots();
            if (_hasAuthoredSlots)
                return AuthoredSlots[((index % AuthoredSlots.Length) + AuthoredSlots.Length) % AuthoredSlots.Length];
            var slots = Slots;
            return slots[((index % slots.Length) + slots.Length) % slots.Length];
        }

        private static void ResolveAuthoredSlots()
        {
            var prefab = WorldPropResources.Load(ContentIds.DryingRack);
            if (prefab == null || prefab == _resolvedPrefab) return;
            _resolvedPrefab = prefab;
            _hasAuthoredSlots = false;
            var wrapper = new GameObject("Drying rack socket probe")
                { hideFlags = HideFlags.HideAndDontSave };
            var model = Object.Instantiate(prefab, wrapper.transform, false);
            var transforms = model.GetComponentsInChildren<Transform>(true);
            var found = 0;
            for (var index = 0; index < AuthoredSlots.Length; index++)
            {
                var name = $"DryingSlot_{index:D2}";
                foreach (var socket in transforms)
                {
                    if (socket.name != name) continue;
                    AuthoredSlots[index] = wrapper.transform.InverseTransformPoint(socket.position);
                    found++;
                    break;
                }
            }
            _hasAuthoredSlots = found == AuthoredSlots.Length;
            if (Application.isPlaying) Object.Destroy(wrapper);
            else Object.DestroyImmediate(wrapper);
        }
    }
}
