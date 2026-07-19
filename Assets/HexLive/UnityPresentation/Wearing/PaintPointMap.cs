#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{
    /// <summary>
    /// Spec 40.8-G: editor-baked paint points. The wound/blood painters used
    /// to find "a UV spot on the mesh near this bone" at RUNTIME by baking
    /// the skinned pose (SkinnedMeshRenderer.BakeMesh) and scanning every
    /// triangle — the single biggest CPU cost of combat frames. The mapping
    /// is pose-independent (a skinned vertex rides its bone), so it is baked
    /// ONCE in the editor from the bind pose instead:
    ///  - skin maps ("skin_&lt;actor&gt;"): per zone, a TSamples×AzimuthSamples
    ///    grid of surface points around the zone's bone axis — the runtime
    ///    seed picks a cell exactly where it used to aim a ray;
    ///  - garment maps ("garment_&lt;mesh&gt;"): per zone, the nearest garment
    ///    points to the zone's anchor bone (blood soak placement).
    /// Regenerate via  HexLive ▸ Paint Maps ▸ Regenerate  after adding an
    /// actor or a garment. Loaded from Resources/HexLive/PaintMaps.
    /// </summary>
    public sealed class PaintPointMap : ScriptableObject
    {
        public const string ResourceFolder = "HexLive/PaintMaps";

        /// <summary>Zone name for zoneless mob-pelt maps (spec 40.8-G):
        /// one flat list of area-weighted surface points.</summary>
        public const string MobSurfaceZone = "MobSurface";

        [System.Serializable]
        public struct Point
        {
            public int Slot;
            public Vector2 Uv;       // wrapped into [0,1] (UDIM-safe)
            public float BindPerU;   // rig-scale metres per UV unit along U
            public float BindPerV;
            public bool Valid;       // false = no surface found for this cell
        }

        [System.Serializable]
        public sealed class ZonePoints
        {
            public string Zone = string.Empty;
            public Point[] Points = System.Array.Empty<Point>();
        }

        // Identity of the mesh the map was baked from — a stale map (mesh
        // re-exported with different topology) is refused loudly.
        public string MeshName = string.Empty;
        public int VertexCount;

        // Skin grids: Points is TSamples rows × AzimuthSamples columns,
        // row-major; rows span [TMin..TMax] along the zone's bone axis.
        // Garment anchor lists: TSamples = 1, AzimuthSamples = Points.Length.
        public int TSamples = 1;
        public int AzimuthSamples = 1;
        public float TMin;
        public float TMax = 1f;

        public ZonePoints[] Zones = System.Array.Empty<ZonePoints>();

        public Point[] PointsFor(string zone)
        {
            foreach (var z in Zones)
            {
                if (z.Zone == zone)
                {
                    return z.Points;
                }
            }

            return System.Array.Empty<Point>();
        }

        /// <summary>Grid cell lookup for skin maps: t along the bone axis,
        /// azimuth in radians — the same two seeded rolls the legacy raycast
        /// placement used.</summary>
        public Point PointAt(Point[] points, float t, float azimuth)
        {
            var rows = Mathf.Max(1, TSamples);
            var cols = Mathf.Max(1, AzimuthSamples);
            var tn = TMax > TMin ? Mathf.Clamp01((t - TMin) / (TMax - TMin)) : 0f;
            var ti = Mathf.Clamp(Mathf.RoundToInt(tn * (rows - 1)), 0, rows - 1);
            var ai = Mathf.FloorToInt(Mathf.Repeat(azimuth, Mathf.PI * 2f) /
                                      (Mathf.PI * 2f) * cols) % cols;
            var index = ti * cols + ai;
            return index >= 0 && index < points.Length ? points[index] : default;
        }

        // ---- loader (Resources, cached, misses cached too) ----

        private static readonly Dictionary<string, PaintPointMap?> Cache = new();

        /// <summary>Load "Resources/HexLive/PaintMaps/&lt;key&gt;". Returns null
        /// (with a one-time warning) when the map is missing or was baked
        /// from a different mesh — callers fall back to the legacy path.</summary>
        public static PaintPointMap? Load(string key, int expectedVertexCount)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var map = Resources.Load<PaintPointMap>($"{ResourceFolder}/{key}");
            if (map == null)
            {
                Debug.LogWarning(
                    $"[PaintPointMap] '{key}' not found in Resources/{ResourceFolder} — " +
                    "run  HexLive ▸ Paint Maps ▸ Regenerate  (falling back to runtime bake).");
            }
            else if (expectedVertexCount > 0 && map.VertexCount != expectedVertexCount)
            {
                Debug.LogWarning(
                    $"[PaintPointMap] '{key}' is stale (baked for {map.VertexCount} verts, " +
                    $"mesh has {expectedVertexCount}) — regenerate. Falling back to runtime bake.");
                map = null;
            }

            Cache[key] = map;
            return map;
        }

        // No-domain-reload runs keep statics between plays — a regenerated
        // asset must not be shadowed by a stale cache entry.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Cache.Clear();
    }
}
