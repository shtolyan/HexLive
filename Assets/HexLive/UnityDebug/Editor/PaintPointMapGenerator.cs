using System.Collections.Generic;
using HexLive.UnityPresentation.Wearing;
using UnityEditor;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{
    /// <summary>
    /// Spec 40.8-G: bakes the PaintPointMap assets the wound/blood painters
    /// consume at runtime, replacing their per-hit SkinnedMeshRenderer.BakeMesh
    /// + full triangle scans (the top combat-frame CPU cost).
    ///
    /// Everything is computed from BIND-POSE data straight off the assets —
    /// vertices live in mesh space, bone bind positions come from
    /// mesh.bindposes.inverse — so no instantiation, no baking, and the
    /// mapping matches what a skinned vertex does at runtime (it rides its
    /// bone; the zone→UV relation is pose-independent).
    ///
    ///  - skin_&lt;actor&gt;.asset: per zone, a TSamples×AzimuthSamples grid of
    ///    surface points around the zone bone axis (SkinTexturePainter's
    ///    seeded (t, azimuth) rolls index straight into it);
    ///  - garment_&lt;mesh&gt;.asset: per zone, the garment point nearest the
    ///    zone's anchor bone (GarmentWearPainter blood soak).
    ///
    /// Re-run after adding an actor, a garment, or re-exporting a mesh.
    /// Menu: <b>HexLive ▸ Paint Maps ▸ Regenerate</b>.
    /// </summary>
    public static class PaintPointMapGenerator
    {
        private const string OutputFolder = "Assets/Resources/HexLive/PaintMaps";
        private const string ActorsFolder = "Assets/Resources/HexLive/Actors";
        private const string WearResources = "HexLive/Wear";

        private const int TSamples = 8;
        private const int AzimuthSamples = 16;

        // The legacy blood-sphere search accepted matches within 0.3 world
        // metres on a ~0.35-scaled actor ≈ 0.86 rig metres. Same tolerance.
        private const float GarmentAnchorRadius = 0.9f;

        // Spec 40.8-G: the medkit gauze texture used to be baked procedurally
        // at RUNTIME on the first bandage (1024² per-pixel loop ≈ 1 s hitch).
        // SkinTexturePainter prefers Resources/HexLive/Decals/gauze_wrap.png
        // when it exists — bake it here once with the SAME sampler.
        [MenuItem("HexLive/Paint Maps/Bake Gauze PNG")]
        public static void BakeGauzePng()
        {
            const int size = 1024;
            var px = new Color[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var u = (x + 0.5f) / size - 0.5f;
                    var v = (y + 0.5f) / size - 0.5f;
                    px[y * size + x] = SkinTexturePainter.GauzeSample(u, v);
                }
            }

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.SetPixels(px);
            tex.Apply(false);
            var path = "Assets/Resources/HexLive/Decals/gauze_wrap.png";
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.mipmapEnabled = true;
                importer.anisoLevel = 8;
                importer.alphaIsTransparency = true;
                importer.SaveAndReimport();
            }

            Debug.Log($"[PaintPointMap] baked {path}");
        }

        [MenuItem("HexLive/Paint Maps/Regenerate")]
        public static void Regenerate()
        {
            System.IO.Directory.CreateDirectory(OutputFolder);

            var written = 0;
            written += GenerateSkinMaps();
            written += GenerateGarmentMaps();
            written += GenerateMobMaps();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[PaintPointMap] regenerated {written} maps into {OutputFolder}");
        }

        // ---- skin maps (one per actor prefab) ----

        private static int GenerateSkinMaps()
        {
            var written = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { ActorsFolder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                {
                    continue;
                }

                var body = FindBodyRenderer(prefab);
                if (body == null || body.sharedMesh == null)
                {
                    Debug.LogWarning($"[PaintPointMap] {prefab.name}: no body renderer/mesh — skipped");
                    continue;
                }

                var map = BuildSkinMap(prefab.name, body);
                if (map != null)
                {
                    SaveMap(map, $"skin_{prefab.name}");
                    written++;
                }
            }

            return written;
        }

        // Mirror of NpcActorView's body pick: first skinned renderer that is
        // not under a Wear and has at least one skin-named material slot.
        private static SkinnedMeshRenderer FindBodyRenderer(GameObject prefab)
        {
            foreach (var smr in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.GetComponentInParent<Wear>() != null)
                {
                    continue;
                }

                var materials = smr.sharedMaterials;
                for (var i = 0; i < materials.Length; i++)
                {
                    if (materials[i] != null && NpcActorView.IsSkinMaterialName(materials[i].name))
                    {
                        return smr;
                    }
                }
            }

            return null;
        }

        private static PaintPointMap BuildSkinMap(string actorName, SkinnedMeshRenderer body)
        {
            var mesh = body.sharedMesh;
            var geo = MeshGeometry.From(mesh, body, skinSlotsOnly: true);
            if (geo == null)
            {
                Debug.LogWarning($"[PaintPointMap] {actorName}: mesh '{mesh.name}' unreadable — skipped");
                return null;
            }

            var map = ScriptableObject.CreateInstance<PaintPointMap>();
            map.MeshName = mesh.name;
            map.VertexCount = mesh.vertexCount;
            map.TSamples = TSamples;
            map.AzimuthSamples = AzimuthSamples;
            map.TMin = SkinTexturePainter.MapTMin;
            map.TMax = SkinTexturePainter.MapTMax;

            var height = Mathf.Max(0.5f, geo.Bounds.size.y);
            var zones = new List<PaintPointMap.ZonePoints>();
            foreach (var (zone, boneAName, boneBName, radius) in SkinTexturePainter.ZoneDefinitions())
            {
                var points = new PaintPointMap.Point[TSamples * AzimuthSamples];
                if (!geo.TryGetBonePosition(boneAName, out var boneA))
                {
                    Debug.LogWarning($"[PaintPointMap] {actorName}/{zone}: bone '{boneAName}' not found — zone empty");
                    zones.Add(new PaintPointMap.ZonePoints { Zone = zone, Points = points });
                    continue;
                }

                var hasB = geo.TryGetBonePosition(boneBName, out var boneB);
                var axis = hasB ? boneB - boneA : Vector3.up * (height * 0.1f);
                var axisDir = axis.sqrMagnitude > 0.0001f ? axis.normalized : Vector3.up;
                var side = Vector3.Cross(axisDir, Vector3.forward).normalized;
                if (side.sqrMagnitude < 0.01f)
                {
                    side = Vector3.Cross(axisDir, Vector3.right).normalized;
                }

                for (var ti = 0; ti < TSamples; ti++)
                {
                    var t = Mathf.Lerp(map.TMin, map.TMax,
                        TSamples > 1 ? ti / (float)(TSamples - 1) : 0f);
                    for (var ai = 0; ai < AzimuthSamples; ai++)
                    {
                        var azimuth = (ai + 0.5f) / AzimuthSamples * Mathf.PI * 2f;
                        var radial = Quaternion.AngleAxis(azimuth * Mathf.Rad2Deg, axisDir) * side;
                        var sample = boneA + axis * t + radial * (radius * height);
                        points[ti * AzimuthSamples + ai] = geo.PointNearest(sample, float.MaxValue);
                    }
                }

                zones.Add(new PaintPointMap.ZonePoints { Zone = zone, Points = points });
            }

            map.Zones = zones.ToArray();
            return map;
        }

        // ---- garment maps (one per distinct worn mesh) ----

        private static int GenerateGarmentMaps()
        {
            var written = 0;
            var seenMeshes = new HashSet<Mesh>();
            // Per-actor variant meshes are named after the ACTOR ("Marta",
            // "Jana"…), so different garments collide on the bare mesh name —
            // the runtime key is name + vertex count (see GarmentWearPainter).
            var keyOwners = new Dictionary<string, Mesh>();
            foreach (var prefab in Resources.LoadAll<GameObject>(WearResources))
            {
                var wear = prefab.GetComponentInChildren<Wear>(true);
                var smr = prefab.GetComponentInChildren<SkinnedMeshRenderer>(true);
                if (wear == null || smr == null)
                {
                    continue;
                }

                // Default mesh + every per-actor variant the Wear can swap in.
                foreach (var mesh in EnumerateWearMeshes(wear, smr))
                {
                    if (mesh == null || !seenMeshes.Add(mesh))
                    {
                        continue;
                    }

                    var key = $"garment_{mesh.name}_{mesh.vertexCount}";
                    if (keyOwners.TryGetValue(key, out var owner))
                    {
                        Debug.LogError(
                            $"[PaintPointMap] key collision: '{key}' produced by both " +
                            $"'{owner.name}' and '{prefab.name}/{mesh.name}' — the second " +
                            "map overwrites the first; rename one mesh.");
                    }

                    keyOwners[key] = mesh;
                    var map = BuildGarmentMap(prefab.name, mesh, smr);
                    if (map != null)
                    {
                        SaveMap(map, key);
                        written++;
                    }
                }
            }

            return written;
        }

        private static IEnumerable<Mesh> EnumerateWearMeshes(Wear wear, SkinnedMeshRenderer smr)
        {
            yield return smr.sharedMesh;

            // configs is a private serialized list — read it the editor way.
            var so = new SerializedObject(wear);
            var configs = so.FindProperty("configs");
            if (configs == null)
            {
                yield break;
            }

            for (var i = 0; i < configs.arraySize; i++)
            {
                var meshProp = configs.GetArrayElementAtIndex(i).FindPropertyRelative("mesh");
                if (meshProp != null && meshProp.objectReferenceValue is Mesh variant)
                {
                    yield return variant;
                }
            }
        }

        private static PaintPointMap BuildGarmentMap(string prefabName, Mesh mesh,
            SkinnedMeshRenderer smr)
        {
            var geo = MeshGeometry.From(mesh, smr, skinSlotsOnly: false);
            if (geo == null)
            {
                Debug.LogWarning($"[PaintPointMap] {prefabName}: mesh '{mesh.name}' unreadable — skipped");
                return null;
            }

            var map = ScriptableObject.CreateInstance<PaintPointMap>();
            map.MeshName = mesh.name;
            map.VertexCount = mesh.vertexCount;
            map.TSamples = 1;
            map.AzimuthSamples = 1;

            var zones = new List<PaintPointMap.ZonePoints>();
            foreach (var pair in NpcActorView.GarmentZoneAnchors)
            {
                var points = System.Array.Empty<PaintPointMap.Point>();
                if (geo.TryGetBonePosition(pair.Value, out var anchor))
                {
                    var point = geo.PointNearest(anchor, GarmentAnchorRadius);
                    if (point.Valid)
                    {
                        points = new[] { point };
                    }
                }

                // Zones the garment does not cover (a shirt has no shin bone,
                // boots sit outside the torso radius) stay empty — the
                // runtime skips them silently.
                zones.Add(new PaintPointMap.ZonePoints { Zone = pair.Key, Points = points });
            }

            map.Zones = zones.ToArray();
            return map;
        }

        // ---- mob pelt maps (one flat point list per mob mesh) ----

        private const string MobResources = "HexLive/Mobs";
        private const int MobSurfacePoints = 64;

        // MobWoundPainter has no zones: bake a pool of area-weighted random
        // surface points (big flanks get more than ear tips — the same
        // distribution its legacy runtime sampler produced) and let the
        // seeded stamp roll pick from it. With a map the wolf mesh is never
        // read at runtime — wounds survive non-readable meshes in builds.
        private static int GenerateMobMaps()
        {
            var written = 0;
            var seenMeshes = new HashSet<Mesh>();
            foreach (var config in Resources.LoadAll<HexLive.UnityPresentation.Config.MobConfig>(
                         MobResources))
            {
                var prefab = config.prefab != null
                    ? config.prefab
                    : string.IsNullOrEmpty(config.prefabResourcePath)
                        ? null
                        : Resources.Load<GameObject>(config.prefabResourcePath);
                var smr = prefab != null
                    ? prefab.GetComponentInChildren<SkinnedMeshRenderer>(true)
                    : null;
                var mesh = smr != null ? smr.sharedMesh : null;
                if (mesh == null || !seenMeshes.Add(mesh))
                {
                    continue;
                }

                var points = SampleSurfacePoints(mesh, MobSurfacePoints);
                if (points == null)
                {
                    Debug.LogWarning($"[PaintPointMap] mob '{config.name}': mesh '{mesh.name}' unreadable — skipped");
                    continue;
                }

                var map = ScriptableObject.CreateInstance<PaintPointMap>();
                map.MeshName = mesh.name;
                map.VertexCount = mesh.vertexCount;
                map.TSamples = 1;
                map.AzimuthSamples = points.Length;
                map.Zones = new[]
                {
                    new PaintPointMap.ZonePoints
                    {
                        Zone = PaintPointMap.MobSurfaceZone,
                        Points = points
                    }
                };
                SaveMap(map, $"mob_{mesh.name}_{mesh.vertexCount}");
                written++;
            }

            return written;
        }

        private static PaintPointMap.Point[] SampleSurfacePoints(Mesh mesh, int count)
        {
            try
            {
                var vertices = mesh.vertices;
                var uvs = mesh.uv;
                var triangles = mesh.triangles;
                if (uvs.Length == 0 || triangles.Length < 3)
                {
                    return null;
                }

                var triCount = triangles.Length / 3;
                var cumulative = new float[triCount];
                var total = 0f;
                for (var t = 0; t < triCount; t++)
                {
                    var a = vertices[triangles[t * 3]];
                    var b = vertices[triangles[t * 3 + 1]];
                    var c = vertices[triangles[t * 3 + 2]];
                    total += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
                    cumulative[t] = total;
                }

                if (total <= 0f)
                {
                    return null;
                }

                // Fixed seed: the pool is stable across regenerations, so
                // saved mobs repaint the same wound pattern after a rebake.
                var rng = new System.Random(48611);
                var points = new PaintPointMap.Point[count];
                for (var i = 0; i < count; i++)
                {
                    var target = (float)rng.NextDouble() * total;
                    var lo = 0;
                    var hi = triCount - 1;
                    while (lo < hi)
                    {
                        var mid = (lo + hi) / 2;
                        if (cumulative[mid] < target) { lo = mid + 1; } else { hi = mid; }
                    }

                    var r1 = Mathf.Sqrt((float)rng.NextDouble());
                    var r2 = (float)rng.NextDouble();
                    var uv = uvs[triangles[lo * 3]] * (1f - r1) +
                             uvs[triangles[lo * 3 + 1]] * (r1 * (1f - r2)) +
                             uvs[triangles[lo * 3 + 2]] * (r1 * r2);
                    points[i] = new PaintPointMap.Point
                    {
                        Slot = 0,
                        Uv = new Vector2(Mathf.Repeat(uv.x, 1f), Mathf.Repeat(uv.y, 1f)),
                        Valid = true
                    };
                }

                return points;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[PaintPointMap] '{mesh.name}': {e.Message}");
                return null;
            }
        }

        // ---- shared bind-pose geometry ----

        private sealed class MeshGeometry
        {
            public Bounds Bounds;
            private Vector3[] _vertices;
            private Vector2[] _uvs;
            private int[] _triangles;
            private int[] _triangleSlot;
            private readonly Dictionary<string, Vector3> _bonePositions = new();

            public static MeshGeometry From(Mesh mesh, SkinnedMeshRenderer smr, bool skinSlotsOnly)
            {
                try
                {
                    var geo = new MeshGeometry
                    {
                        Bounds = mesh.bounds,
                        _vertices = mesh.vertices,
                        _uvs = mesh.uv
                    };

                    var triangles = new List<int>();
                    var slots = new List<int>();
                    var materials = smr.sharedMaterials;
                    for (var slot = 0; slot < mesh.subMeshCount; slot++)
                    {
                        if (skinSlotsOnly)
                        {
                            var material = slot < materials.Length ? materials[slot] : null;
                            if (material == null || !NpcActorView.IsSkinMaterialName(material.name))
                            {
                                continue;
                            }
                        }

                        var indices = mesh.GetTriangles(slot);
                        triangles.AddRange(indices);
                        for (var i = 0; i < indices.Length / 3; i++)
                        {
                            slots.Add(slot);
                        }
                    }

                    geo._triangles = triangles.ToArray();
                    geo._triangleSlot = slots.ToArray();
                    if (geo._triangles.Length == 0 || geo._uvs.Length == 0 ||
                        geo._vertices.Length == 0)
                    {
                        return null;
                    }

                    // Bone bind positions in MESH space: bindposes[i] maps
                    // mesh space → bone space at bind, so its inverse maps the
                    // bone origin back into mesh space. The variant meshes of
                    // a Wear share the prefab renderer's bone order.
                    var bones = smr.bones;
                    var bindposes = mesh.bindposes;
                    var count = Mathf.Min(bones.Length, bindposes.Length);
                    for (var i = 0; i < count; i++)
                    {
                        if (bones[i] == null)
                        {
                            continue;
                        }

                        var name = bones[i].name;
                        if (!geo._bonePositions.ContainsKey(name))
                        {
                            geo._bonePositions[name] =
                                bindposes[i].inverse.MultiplyPoint3x4(Vector3.zero);
                        }
                    }

                    return geo;
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[PaintPointMap] '{mesh.name}': {e.Message}");
                    return null;
                }
            }

            public bool TryGetBonePosition(string boneName, out Vector3 position)
            {
                position = default;
                return !string.IsNullOrEmpty(boneName) &&
                       _bonePositions.TryGetValue(boneName, out position);
            }

            /// <summary>Closest-by-centroid triangle → baked paint point.
            /// Returns Valid=false when nothing lies within maxDistance.</summary>
            public PaintPointMap.Point PointNearest(Vector3 sample, float maxDistance)
            {
                var best = -1;
                var bestSqr = maxDistance * maxDistance;
                for (var tri = 0; tri < _triangles.Length; tri += 3)
                {
                    var centroid = (_vertices[_triangles[tri]] +
                                    _vertices[_triangles[tri + 1]] +
                                    _vertices[_triangles[tri + 2]]) / 3f;
                    var sqr = (centroid - sample).sqrMagnitude;
                    if (sqr < bestSqr)
                    {
                        bestSqr = sqr;
                        best = tri / 3;
                    }
                }

                if (best < 0)
                {
                    return default;
                }

                var i0 = _triangles[best * 3];
                var i1 = _triangles[best * 3 + 1];
                var i2 = _triangles[best * 3 + 2];
                var uv = (_uvs[i0] + _uvs[i1] + _uvs[i2]) / 3f;
                uv.x = Mathf.Repeat(uv.x, 1f);
                uv.y = Mathf.Repeat(uv.y, 1f);

                // UV density (rig-scale metres per UV unit) — StampSizeFor's
                // math on bind-pose vertices; the runtime scales by height.
                var p1 = _vertices[i1] - _vertices[i0];
                var p2 = _vertices[i2] - _vertices[i0];
                var t1 = _uvs[i1] - _uvs[i0];
                var t2 = _uvs[i2] - _uvs[i0];
                var det = t1.x * t2.y - t1.y * t2.x;
                float bindPerU;
                float bindPerV;
                if (Mathf.Abs(det) < 1e-8f)
                {
                    // Degenerate UVs: the runtime fallback yielded size 0.25
                    // for a ~0.09 m stamp → equivalent density 0.36 m/UV.
                    bindPerU = bindPerV = 0.36f;
                }
                else
                {
                    var inv = 1f / det;
                    bindPerU = Mathf.Max(0.0001f, ((p1 * t2.y - p2 * t1.y) * inv).magnitude);
                    bindPerV = Mathf.Max(0.0001f, ((p2 * t1.x - p1 * t2.x) * inv).magnitude);
                }

                return new PaintPointMap.Point
                {
                    Slot = _triangleSlot[best],
                    Uv = uv,
                    BindPerU = bindPerU,
                    BindPerV = bindPerV,
                    Valid = true
                };
            }
        }

        private static void SaveMap(PaintPointMap map, string key)
        {
            var assetPath = $"{OutputFolder}/{key}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<PaintPointMap>(assetPath);
            if (existing != null)
            {
                EditorUtility.CopySerialized(map, existing);
                EditorUtility.SetDirty(existing);
                Object.DestroyImmediate(map);
            }
            else
            {
                AssetDatabase.CreateAsset(map, assetPath);
            }
        }
    }
}
