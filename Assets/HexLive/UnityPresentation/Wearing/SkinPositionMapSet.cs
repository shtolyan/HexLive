#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{
    /// <summary>
    /// Spec 40.8-J: the texture-space POSITION MAPS that let a decal cross a
    /// UV seam.
    ///
    /// A painted stamp used to be an axis-aligned rectangle in ONE material
    /// slot's [0,1] UV space, so a bandage at the hip was scissored at the
    /// edge of the Legs tile and could not continue onto the torso — the
    /// neighbouring skin is a DIFFERENT texture on a DIFFERENT slot. Here the
    /// relation is inverted: for every texel of every skin texture we bake the
    /// 3D point of the body it covers (bind pose, mesh space) plus its outward
    /// normal. Painting then asks each texel "is your point inside this
    /// decal?" instead of "are you inside this rectangle", which is continuous
    /// across islands, tiles and textures by construction.
    ///
    /// One map per TEXTURE GROUP, not per slot: Genesis 3 binds Face/Ears/Lips
    /// to one texture and Arms/Fingernails to another, and slots sharing a
    /// texture share a UV layout — so grouping also stitches the seams
    /// BETWEEN those slots for free. SlotToGroup[slot] is -1 for non-skin.
    ///
    /// SlotSamples is the cheap "does this decal reach that slot at all?"
    /// oracle: an area-weighted point cloud of each slot's bind-pose surface.
    /// It gates RenderTexture allocation, so it must not be dropped — painting
    /// every slot unconditionally would cost ~21 MB of RT per slot per NPC.
    ///
    /// Baked by  HexLive ▸ Paint Maps ▸ Regenerate  (PaintPointMapGenerator),
    /// loaded from Resources/HexLive/PaintMaps as "skinpos_&lt;Actor&gt;".
    /// </summary>
    public sealed class SkinPositionMapSet : ScriptableObject
    {
        // Position: RGB = bind-pose mesh-space point, A = coverage (1 where a
        // triangle rasterized, 0 in the void). Half float — a body spans ~2
        // units, so the resolution there is well under a millimetre.
        public const int PositionMapSize = 512;
        // The normal only drives a soft facing cutoff (it keeps a wrap on a
        // thin forearm from also printing on the far side), so it is cheap.
        public const int NormalMapSize = 256;

        // Coarse UV grid over each group's position map: per cell, the 3D box
        // its texels cover. It answers "which part of this texture can the
        // decal possibly touch?", which turns painting from a full-target
        // 2048² pass into a small rect — the difference between ~10 ms and a
        // fraction of a millisecond per repaint.
        public const int CellsPerSide = 32;

        [System.Serializable]
        public sealed class SlotCloud
        {
            public Vector3[] Points = System.Array.Empty<Vector3>();
        }

        [System.Serializable]
        public sealed class CellGrid
        {
            public Vector3[] Min = System.Array.Empty<Vector3>();
            public Vector3[] Max = System.Array.Empty<Vector3>();
            public bool[] Valid = System.Array.Empty<bool>();
        }

        public string MeshName = string.Empty;
        public int VertexCount;

        public int[] SlotToGroup = System.Array.Empty<int>();
        public Texture2D?[] GroupPositionMaps = System.Array.Empty<Texture2D?>();
        public Texture2D?[] GroupNormalMaps = System.Array.Empty<Texture2D?>();
        public CellGrid[] GroupCells = System.Array.Empty<CellGrid>();
        // Index-aligned with SlotToGroup; empty for non-skin slots.
        public SlotCloud[] SlotSamples = System.Array.Empty<SlotCloud>();
        // Sample spacing (mesh units) — the margin a reach test must add so a
        // decal that only clips the corner of a slot is not missed.
        public float SampleSpacing = 0.03f;

        public int GroupOf(int slot) =>
            slot >= 0 && slot < SlotToGroup.Length ? SlotToGroup[slot] : -1;

        public Texture2D? PositionMapOf(int slot)
        {
            var group = GroupOf(slot);
            return group >= 0 && group < GroupPositionMaps.Length
                ? GroupPositionMaps[group]
                : null;
        }

        public Texture2D? NormalMapOf(int slot)
        {
            var group = GroupOf(slot);
            return group >= 0 && group < GroupNormalMaps.Length
                ? GroupNormalMaps[group]
                : null;
        }

        /// <summary>The UV rectangle of `group` that a decal sphere can reach,
        /// or false when it reaches nothing here. Conservative: the union of
        /// every grid cell whose 3D box comes within `radius`, grown by one
        /// cell so bilinear taps at the edge stay inside.</summary>
        public bool TryGetUvBounds(int group, Vector3 center, float radius, out Rect uv)
        {
            uv = default;
            if (group < 0 || group >= GroupCells.Length)
            {
                return false;
            }

            var grid = GroupCells[group];
            if (grid.Valid.Length != CellsPerSide * CellsPerSide)
            {
                // No grid baked (older asset): paint the whole target rather
                // than nothing — correct, just slower.
                uv = new Rect(0f, 0f, 1f, 1f);
                return true;
            }

            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;
            var sqr = radius * radius;
            for (var y = 0; y < CellsPerSide; y++)
            {
                for (var x = 0; x < CellsPerSide; x++)
                {
                    var i = y * CellsPerSide + x;
                    if (!grid.Valid[i] || SqrDistanceToBox(center, grid.Min[i], grid.Max[i]) > sqr)
                    {
                        continue;
                    }

                    if (x < minX) { minX = x; }
                    if (x > maxX) { maxX = x; }
                    if (y < minY) { minY = y; }
                    if (y > maxY) { maxY = y; }
                }
            }

            if (maxX < minX)
            {
                return false;
            }

            const float cell = 1f / CellsPerSide;
            var u0 = Mathf.Max(0f, (minX - 1) * cell);
            var v0 = Mathf.Max(0f, (minY - 1) * cell);
            var u1 = Mathf.Min(1f, (maxX + 2) * cell);
            var v1 = Mathf.Min(1f, (maxY + 2) * cell);
            uv = new Rect(u0, v0, u1 - u0, v1 - v0);
            return uv.width > 0f && uv.height > 0f;
        }

        /// <summary>The UV rectangle of <paramref name="group"/> reached by an
        /// oriented projected-decal box. Unlike the sphere overload, this uses
        /// the same thin slab as the runtime shader, so a wound on the front of
        /// a limb does not make the GPU sweep cells on its back as well.
        ///
        /// Cell bounds are transformed conservatively: the transformed AABB
        /// may be a little wider than the real parallelepiped, but it can never
        /// reject a texel the shader would accept.</summary>
        public bool TryGetUvBounds(int group, in Matrix4x4 objectToDecal,
            in Vector3 halfExtents, out Rect uv)
        {
            uv = default;
            if (group < 0 || group >= GroupCells.Length)
            {
                return false;
            }

            var grid = GroupCells[group];
            var cellCount = CellsPerSide * CellsPerSide;
            if (grid.Valid.Length != cellCount || grid.Min.Length != cellCount ||
                grid.Max.Length != cellCount)
            {
                // No grid baked (older asset): paint the whole target rather
                // than nothing — correct, just slower.
                uv = new Rect(0f, 0f, 1f, 1f);
                return true;
            }

            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;
            for (var y = 0; y < CellsPerSide; y++)
            {
                for (var x = 0; x < CellsPerSide; x++)
                {
                    var i = y * CellsPerSide + x;
                    if (!grid.Valid[i] || !IntersectsDecalBox(
                            grid.Min[i], grid.Max[i], objectToDecal, halfExtents))
                    {
                        continue;
                    }

                    if (x < minX) { minX = x; }
                    if (x > maxX) { maxX = x; }
                    if (y < minY) { minY = y; }
                    if (y > maxY) { maxY = y; }
                }
            }

            if (maxX < minX)
            {
                return false;
            }

            const float cell = 1f / CellsPerSide;
            var u0 = Mathf.Max(0f, (minX - 1) * cell);
            var v0 = Mathf.Max(0f, (minY - 1) * cell);
            var u1 = Mathf.Min(1f, (maxX + 2) * cell);
            var v1 = Mathf.Min(1f, (maxY + 2) * cell);
            uv = new Rect(u0, v0, u1 - u0, v1 - v0);
            return uv.width > 0f && uv.height > 0f;
        }

        private static bool IntersectsDecalBox(Vector3 min, Vector3 max,
            in Matrix4x4 objectToDecal, in Vector3 halfExtents)
        {
            var center = (min + max) * 0.5f;
            var extent = (max - min) * 0.5f;
            var c = objectToDecal.MultiplyPoint3x4(center);

            // Exact AABB extents after an affine transform. The transformed
            // cell is generally a parallelepiped; its AABB is conservative and
            // much tighter than the old world-space bounding sphere.
            var ex = Mathf.Abs(objectToDecal.m00) * extent.x +
                     Mathf.Abs(objectToDecal.m01) * extent.y +
                     Mathf.Abs(objectToDecal.m02) * extent.z;
            var ey = Mathf.Abs(objectToDecal.m10) * extent.x +
                     Mathf.Abs(objectToDecal.m11) * extent.y +
                     Mathf.Abs(objectToDecal.m12) * extent.z;
            var ez = Mathf.Abs(objectToDecal.m20) * extent.x +
                     Mathf.Abs(objectToDecal.m21) * extent.y +
                     Mathf.Abs(objectToDecal.m22) * extent.z;

            return Mathf.Abs(c.x) <= halfExtents.x + ex &&
                   Mathf.Abs(c.y) <= halfExtents.y + ey &&
                   Mathf.Abs(c.z) <= halfExtents.z + ez;
        }

        private static float SqrDistanceToBox(Vector3 p, Vector3 min, Vector3 max)
        {
            var dx = Mathf.Max(0f, Mathf.Max(min.x - p.x, p.x - max.x));
            var dy = Mathf.Max(0f, Mathf.Max(min.y - p.y, p.y - max.y));
            var dz = Mathf.Max(0f, Mathf.Max(min.z - p.z, p.z - max.z));
            return dx * dx + dy * dy + dz * dz;
        }

        /// <summary>True when any of the slot's baked surface samples falls
        /// inside the decal's BOX — the same test the shader runs per texel
        /// (minus the facing term), not a bounding sphere. The difference is
        /// not academic: a decal is a thin slab, and a sphere around it claims
        /// everything curving away from it. On the head, where a 14 cm wrap is
        /// large next to the skull, the sphere claimed the face, both ears and
        /// the neck every single time — three extra ~21 MB render targets for
        /// texels the shader was going to reject anyway.
        ///
        /// `objectToDecal` maps bind space to decal space (xy normalized to
        /// ±0.5 across the art, z in mesh units); `halfExtents` is that box
        /// grown by the sample spacing so a decal clipping the corner of a
        /// slot still claims it.</summary>
        public bool SlotReaches(int slot, in Matrix4x4 objectToDecal, in Vector3 halfExtents)
        {
            if (slot < 0 || slot >= SlotSamples.Length)
            {
                return false;
            }

            var points = SlotSamples[slot].Points;
            for (var i = 0; i < points.Length; i++)
            {
                var d = objectToDecal.MultiplyPoint3x4(points[i]);
                if (Mathf.Abs(d.x) <= halfExtents.x && Mathf.Abs(d.y) <= halfExtents.y &&
                    Mathf.Abs(d.z) <= halfExtents.z)
                {
                    return true;
                }
            }

            return false;
        }

        // ---- loader (mirrors PaintPointMap.Load: Resources, cached, misses
        // cached too, reset per play so a regenerated asset is picked up) ----

        private static readonly Dictionary<string, SkinPositionMapSet?> Cache = new();

        public static SkinPositionMapSet? Load(string key, int expectedVertexCount)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var set = HexLive.UnityPresentation.Content.AtomicResources.Load<SkinPositionMapSet>(
                $"{PaintPointMap.ResourceFolder}/{key}");
            if (set == null)
            {
                Debug.LogWarning(
                    $"[SkinPositionMap] '{key}' not found in Resources/{PaintPointMap.ResourceFolder} — " +
                    "run  HexLive ▸ Paint Maps ▸ Regenerate  (decals stay per-slot, clipped at UV seams).");
            }
            else if (expectedVertexCount > 0 && set.VertexCount != expectedVertexCount)
            {
                Debug.LogWarning(
                    $"[SkinPositionMap] '{key}' is stale (baked for {set.VertexCount} verts, " +
                    $"mesh has {expectedVertexCount}) — regenerate. Decals stay clipped at UV seams.");
                set = null;
            }

            if (set != null)
            {
                Cache[key] = set;
            }
            return set;
        }

        /// <summary>Резидентность (ContentResidency): забыть набор актрисы,
        /// чей комплект вытеснен; хэндл ассета отпускает AtomicResources.</summary>
        public static void Evict(string key) => Cache.Remove(key);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Cache.Clear();
    }
}
