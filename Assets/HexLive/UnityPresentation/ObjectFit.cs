using UnityEngine;
using HexLive.UnityPresentation.Spatial;

namespace HexLive.UnityPresentation
{
    /// <summary>
    /// Single source of truth for how big a world object renders — used by BOTH
    /// the ground renderer (<c>HexWorldRenderer.FitObjectPrefab</c>) and the
    /// in-hand prop (<c>NpcActorView.SetHandProp</c>) so a tool / coconut is the
    /// SAME physical size in the hand and on the ground.
    ///
    /// Size = a per-category fraction of <see cref="SimulationUnityMapper.HexRadius"/>,
    /// applied to the object's measured max dimension. Change a number here and it
    /// moves everywhere (ground + hand) at once. `ItemAttachConfig.localScale` is a
    /// per-item fine MULTIPLIER on top of this (default 1), not the absolute size.
    /// </summary>
    public static class ObjectFit
    {
        /// Spec §54.2: one palm trunk SEGMENT length, in HexRadius units. A log
        /// renders this long; a stick is the same length but 4× thinner; the
        /// PalmTreeFactory stacks N of these for the standing palm — so a dropped
        /// log matches a trunk segment exactly.
        public const float PalmSegmentLength = 0.7f;

        /// World-space target for the object's measured dimension.
        public static float TargetWorldSize(string definitionId)
        {
            var r = SimulationUnityMapper.HexRadius;
            // §54.2: tree.palm is NOT sized here — the palm_final prefab is authored
            // 1:1 in Blender and instantiated as-is by PalmTreeFactory (no fit).
            if (definitionId.Contains("tree")) return r * 2.2f;
            if (definitionId.Contains("bed")) return r * 0.95f;
            if (definitionId.StartsWith("food.")) return r * 0.12f;
            // Spec §54.2: a log/stick is a full palm-trunk segment long (big, like
            // Stranded Deep) — measured by its long axis; the crown and leaf are
            // sized to sit with the palm.
            if (definitionId == "resource.log" || definitionId == "resource.stick") return r * PalmSegmentLength;
            if (definitionId == "resource.palm_crown") return r * 0.7f;
            if (definitionId == "resource.palm_leaf") return r * 0.55f;
            // The spear is a long two-handed weapon — much longer than a hand tool.
            if (definitionId == "tool.spear") return r * 0.9f;
            // Tools & resources: 0.216 = the standard hand/ground tool size
            // (was 0.18; +20% after in-hand testing, applied to BOTH paths).
            if (definitionId.StartsWith("tool.") || definitionId.StartsWith("resource.")) return r * 0.216f;
            if (definitionId == "campfire.spot") return r * 0.55f;
            if (definitionId == "grave.npc") return r * 0.35f;
            if (definitionId == "rock.boulder") return r * 0.45f;
            if (definitionId == "forest.deadfall" || definitionId == "station.drying_rack" ||
                definitionId == "construction.site") return r * 0.7f;
            return r * 0.6f;
        }

        /// Which bounds dimension is normalized for a given category.
        public static float MeasureCurrent(string definitionId, Bounds b)
        {
            if (definitionId.Contains("tree") || definitionId == "grave.npc") return b.size.y;
            if (definitionId.Contains("bed") || definitionId == "campfire.spot" ||
                definitionId == "rock.boulder" || definitionId == "forest.deadfall" ||
                definitionId == "station.drying_rack" || definitionId == "construction.site")
                return Mathf.Max(b.size.x, b.size.z);
            return Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)); // food / tool / resource / default
        }

        /// Combined world-space bounds of every renderer under <paramref name="go"/>.
        public static bool WorldBounds(GameObject go, out Bounds bounds)
        {
            bounds = default;
            var rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return false;
            bounds = rs[0].bounds;
            for (var i = 1; i < rs.Length; i++) bounds.Encapsulate(rs[i].bounds);
            return true;
        }

        /// Uniform factor to MULTIPLY the object's localScale by so its measured
        /// world dimension equals the category target. 1 if it has no renderers.
        /// Works both at world scale (ground) and under a scaled bone (hand) —
        /// renderer.bounds is world-space, so the result targets a world size.
        public static float FitScaleFactor(GameObject go, string definitionId)
        {
            if (!WorldBounds(go, out var b)) return 1f;
            var current = MeasureCurrent(definitionId, b);
            if (current <= 0.0001f) return 1f;
            return TargetWorldSize(definitionId) / current;
        }
    }
}
