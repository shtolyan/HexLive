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

                var geo = MeshGeometry.From(body.sharedMesh, body, skinSlotsOnly: true);
                if (geo == null)
                {
                    Debug.LogWarning($"[PaintPointMap] {prefab.name}: mesh " +
                                     $"'{body.sharedMesh.name}' unreadable — skipped");
                    continue;
                }

                var map = BuildSkinMap(prefab.name, body, geo);
                if (map != null)
                {
                    SaveMap(map, $"skin_{prefab.name}");
                    written++;
                }

                // Spec 40.8-J: the texel→body-point maps that let a decal
                // cross a UV seam. Same geometry, same run.
                if (BuildPositionMapSet(prefab.name, body, geo))
                {
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

        private static PaintPointMap BuildSkinMap(string actorName, SkinnedMeshRenderer body,
            MeshGeometry geo)
        {
            var mesh = body.sharedMesh;
            var map = ScriptableObject.CreateInstance<PaintPointMap>();
            map.MeshName = mesh.name;
            map.VertexCount = mesh.vertexCount;
            map.TSamples = TSamples;
            map.AzimuthSamples = AzimuthSamples;
            map.TMin = SkinTexturePainter.MapTMin;
            map.TMax = SkinTexturePainter.MapTMax;
            map.Version = PaintPointMap.ProjectedVersion;

            var height = Mathf.Max(0.5f, geo.Bounds.size.y);
            // Spec 40.8-J: world-metre stamp sizes are authored against a
            // 1.7 m rig; the projected path needs them in MESH units.
            map.MeshHeight = height;
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

                zones.Add(new PaintPointMap.ZonePoints
                {
                    Zone = zone,
                    Points = points,
                    BindAxisDir = axisDir
                });
            }

            map.Zones = zones.ToArray();
            return map;
        }

        // ---- spec 40.8-J: texel → body-point maps (seam-free decals) ----

        // Sampled point cloud per material slot: the "does this decal reach
        // that slot?" oracle that gates RenderTexture allocation at runtime.
        private const int SlotSampleCount = 1200;
        // How far dilation carries valid positions past an island's edge, in
        // texels. Bilinear filtering of the SKIN texture reaches ~1 texel past
        // the island, and the decal must still be defined there or a hairline
        // of unpainted skin shows along every island border.
        private const int PositionDilationTexels = 6;

        /// <summary>Bakes one SkinPositionMapSet per actor: for every texel of
        /// every skin texture, the bind-pose body point it covers. Returns
        /// true when an asset was written.</summary>
        private static bool BuildPositionMapSet(string actorName, SkinnedMeshRenderer body,
            MeshGeometry geo)
        {
            var mesh = body.sharedMesh;
            var materials = body.sharedMaterials;

            // Group skin slots by the texture they paint into. Slots sharing a
            // texture share a UV layout, so one map serves them all — and the
            // seams BETWEEN them (face/ears/lips) close for free.
            var groupOfTexture = new Dictionary<Texture, int>();
            var slotToGroup = new int[materials.Length];
            var groupCount = 0;
            for (var slot = 0; slot < slotToGroup.Length; slot++)
            {
                slotToGroup[slot] = -1;
                var material = materials[slot];
                if (material == null || !NpcActorView.IsSkinMaterialName(material.name) ||
                    !material.HasProperty("_BaseMap"))
                {
                    continue;
                }

                var tex = material.GetTexture("_BaseMap");
                if (tex == null)
                {
                    continue; // nothing to paint into — RepaintSlot bails too
                }

                if (!groupOfTexture.TryGetValue(tex, out var group))
                {
                    group = groupCount++;
                    groupOfTexture[tex] = group;
                }

                slotToGroup[slot] = group;
            }

            if (groupCount == 0)
            {
                Debug.LogWarning($"[SkinPositionMap] {actorName}: no skin slots with a base map — skipped");
                return false;
            }

            var set = ScriptableObject.CreateInstance<SkinPositionMapSet>();
            set.MeshName = mesh.name;
            set.VertexCount = mesh.vertexCount;
            set.SlotToGroup = slotToGroup;
            set.GroupPositionMaps = new Texture2D[groupCount];
            set.GroupNormalMaps = new Texture2D[groupCount];
            set.GroupCells = new SkinPositionMapSet.CellGrid[groupCount];

            for (var group = 0; group < groupCount; group++)
            {
                RasterizeGroup(geo, slotToGroup, group,
                    out var position, out var normal, out var cells);
                // Written as IMAGE FILES, not sub-assets: the project
                // serializes assets as text, so a 2 MB texture embedded in a
                // .asset becomes a multi-megabyte YAML hex dump per actor.
                set.GroupPositionMaps[group] =
                    WriteExr(position, $"{OutputFolder}/skinpos_{actorName}_g{group}.exr");
                set.GroupNormalMaps[group] =
                    WritePng(normal, $"{OutputFolder}/skinnrm_{actorName}_g{group}.png");
                set.GroupCells[group] = cells;
            }

            set.SlotSamples = BuildSlotSamples(geo, slotToGroup.Length, out var spacing);
            set.SampleSpacing = spacing;

            SaveMapSet(set, $"skinpos_{actorName}");
            return true;
        }

        // Half-float positions survive only in an HDR format — EXR keeps them
        // exactly and stays compact on disk.
        private static Texture2D WriteExr(Texture2D source, string path)
        {
            System.IO.File.WriteAllBytes(path, source.EncodeToEXR(Texture2D.EXRFlags.None));
            Object.DestroyImmediate(source);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = false;          // positions are DATA
                importer.mipmapEnabled = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Bilinear;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.maxTextureSize = SkinPositionMapSet.PositionMapSize;
                // Block compression would quantize the positions into visible
                // steps along every decal edge.
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static Texture2D WritePng(Texture2D source, string path)
        {
            System.IO.File.WriteAllBytes(path, source.EncodeToPNG());
            Object.DestroyImmediate(source);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = false;          // encoded normal, not colour
                importer.mipmapEnabled = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Bilinear;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.maxTextureSize = SkinPositionMapSet.NormalMapSize;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // Rasterizes every triangle of one texture group into UV space,
        // barycentric-interpolating the bind-pose position and normal, then
        // dilates outward so island borders stay defined.
        private static void RasterizeGroup(MeshGeometry geo, int[] slotToGroup, int group,
            out Texture2D positionMap, out Texture2D normalMap,
            out SkinPositionMapSet.CellGrid cells)
        {
            const int size = SkinPositionMapSet.PositionMapSize;
            var position = new Vector3[size * size];
            var normal = new Vector3[size * size];
            var covered = new bool[size * size];

            var triangles = geo.Triangles;
            var vertices = geo.Vertices;
            var uvs = geo.Uvs;
            var triangleSlot = geo.TriangleSlot;
            var vertexNormals = geo.Normals != null && geo.Normals.Length == vertices.Length
                ? geo.Normals
                : null;

            for (var tri = 0; tri < triangleSlot.Length; tri++)
            {
                var slot = triangleSlot[tri];
                if (slot < 0 || slot >= slotToGroup.Length || slotToGroup[slot] != group)
                {
                    continue;
                }

                var i0 = triangles[tri * 3];
                var i1 = triangles[tri * 3 + 1];
                var i2 = triangles[tri * 3 + 2];

                // Genesis 3 packs each body part on its own UDIM tile (torso
                // U∈[1,2], legs U∈[2,3]…) and the sampler wraps, so the map is
                // baked wrapped the same way. The whole triangle shifts by ONE
                // offset — taken from its centroid — or it would tear apart.
                var uv0 = uvs[i0];
                var uv1 = uvs[i1];
                var uv2 = uvs[i2];
                var centroid = (uv0 + uv1 + uv2) / 3f;
                var shift = new Vector2(Mathf.Floor(centroid.x), Mathf.Floor(centroid.y));
                uv0 -= shift;
                uv1 -= shift;
                uv2 -= shift;

                var face = geo.TriangleNormal(i0, i1, i2);
                RasterizeTriangle(size, position, normal, covered,
                    uv0, uv1, uv2,
                    vertices[i0], vertices[i1], vertices[i2],
                    vertexNormals != null ? vertexNormals[i0] : face,
                    vertexNormals != null ? vertexNormals[i1] : face,
                    vertexNormals != null ? vertexNormals[i2] : face);
            }

            Dilate(size, position, normal, covered, PositionDilationTexels);
            // After dilation, so the grid also covers the padding ring the
            // runtime rect has to include.
            cells = BuildCellGrid(size, position, covered);

            positionMap = new Texture2D(size, size, TextureFormat.RGBAHalf, false, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 0
            };
            var posPixels = new Color[size * size];
            for (var i = 0; i < posPixels.Length; i++)
            {
                var p = position[i];
                posPixels[i] = new Color(p.x, p.y, p.z, covered[i] ? 1f : 0f);
            }

            positionMap.SetPixels(posPixels);
            positionMap.Apply(false, false);

            const int nsize = SkinPositionMapSet.NormalMapSize;
            normalMap = new Texture2D(nsize, nsize, TextureFormat.RGBA32, false, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 0
            };
            var nrmPixels = new Color[nsize * nsize];
            var step = size / nsize;
            for (var y = 0; y < nsize; y++)
            {
                for (var x = 0; x < nsize; x++)
                {
                    var n = normal[(y * step + step / 2) * size + (x * step + step / 2)];
                    if (n.sqrMagnitude < 1e-8f)
                    {
                        n = Vector3.up;
                    }
                    else
                    {
                        n = n.normalized;
                    }

                    nrmPixels[y * nsize + x] =
                        new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
                }
            }

            normalMap.SetPixels(nrmPixels);
            normalMap.Apply(false, false);
        }

        // The 3D box each coarse UV cell covers — the runtime's "which part of
        // this texture can the decal reach?" query (SkinPositionMapSet.CellGrid).
        private static SkinPositionMapSet.CellGrid BuildCellGrid(int size, Vector3[] position,
            bool[] covered)
        {
            const int cells = SkinPositionMapSet.CellsPerSide;
            var grid = new SkinPositionMapSet.CellGrid
            {
                Min = new Vector3[cells * cells],
                Max = new Vector3[cells * cells],
                Valid = new bool[cells * cells]
            };

            var step = size / cells;
            for (var cy = 0; cy < cells; cy++)
            {
                for (var cx = 0; cx < cells; cx++)
                {
                    var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                    var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                    var any = false;
                    for (var y = cy * step; y < (cy + 1) * step; y++)
                    {
                        for (var x = cx * step; x < (cx + 1) * step; x++)
                        {
                            var index = y * size + x;
                            if (!covered[index])
                            {
                                continue;
                            }

                            min = Vector3.Min(min, position[index]);
                            max = Vector3.Max(max, position[index]);
                            any = true;
                        }
                    }

                    var cell = cy * cells + cx;
                    grid.Valid[cell] = any;
                    if (any)
                    {
                        grid.Min[cell] = min;
                        grid.Max[cell] = max;
                    }
                }
            }

            return grid;
        }

        private static void RasterizeTriangle(int size, Vector3[] position, Vector3[] normal,
            bool[] covered, Vector2 uv0, Vector2 uv1, Vector2 uv2,
            Vector3 p0, Vector3 p1, Vector3 p2, Vector3 n0, Vector3 n1, Vector3 n2)
        {
            var a = uv0 * size;
            var b = uv1 * size;
            var c = uv2 * size;

            var minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))) - 1, 0, size - 1);
            var maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))) + 1, 0, size - 1);
            var minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))) - 1, 0, size - 1);
            var maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))) + 1, 0, size - 1);

            var det = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
            if (Mathf.Abs(det) < 1e-9f)
            {
                return; // degenerate in UV space — dilation covers the texels
            }

            var inv = 1f / det;
            // A half-texel slack closes the pinholes between adjacent
            // triangles that exact edge tests leave along shared edges.
            const float slack = -0.35f;
            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var px = x + 0.5f;
                    var py = y + 0.5f;
                    var w0 = ((b.y - c.y) * (px - c.x) + (c.x - b.x) * (py - c.y)) * inv;
                    var w1 = ((c.y - a.y) * (px - c.x) + (a.x - c.x) * (py - c.y)) * inv;
                    var w2 = 1f - w0 - w1;
                    if (w0 < slack || w1 < slack || w2 < slack)
                    {
                        continue;
                    }

                    var index = y * size + x;
                    if (covered[index])
                    {
                        continue; // first triangle wins — overlaps are rare
                    }

                    covered[index] = true;
                    position[index] = p0 * w0 + p1 * w1 + p2 * w2;
                    normal[index] = (n0 * w0 + n1 * w1 + n2 * w2).normalized;
                }
            }
        }

        // Flood the nearest valid value outward so a decal stays defined in
        // the padding around every island (see PositionDilationTexels).
        private static void Dilate(int size, Vector3[] position, Vector3[] normal,
            bool[] covered, int passes)
        {
            var frontier = new List<int>();
            for (var pass = 0; pass < passes; pass++)
            {
                frontier.Clear();
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        var index = y * size + x;
                        if (covered[index])
                        {
                            continue;
                        }

                        var sumP = Vector3.zero;
                        var sumN = Vector3.zero;
                        var count = 0;
                        for (var dy = -1; dy <= 1; dy++)
                        {
                            var ny = y + dy;
                            if (ny < 0 || ny >= size)
                            {
                                continue;
                            }

                            for (var dx = -1; dx <= 1; dx++)
                            {
                                var nx = x + dx;
                                if (nx < 0 || nx >= size)
                                {
                                    continue;
                                }

                                var neighbour = ny * size + nx;
                                if (!covered[neighbour])
                                {
                                    continue;
                                }

                                sumP += position[neighbour];
                                sumN += normal[neighbour];
                                count++;
                            }
                        }

                        if (count == 0)
                        {
                            continue;
                        }

                        position[index] = sumP / count;
                        normal[index] = sumN.sqrMagnitude > 1e-8f ? sumN.normalized : Vector3.up;
                        frontier.Add(index);
                    }
                }

                if (frontier.Count == 0)
                {
                    return;
                }

                // Marked AFTER the sweep so one pass grows exactly one ring.
                foreach (var index in frontier)
                {
                    covered[index] = true;
                }
            }
        }

        // Area-weighted surface samples per material slot, from a fixed seed
        // so a rebake does not reshuffle which slots a decal claims.
        private static SkinPositionMapSet.SlotCloud[] BuildSlotSamples(MeshGeometry geo,
            int slotCount, out float spacing)
        {
            var clouds = new SkinPositionMapSet.SlotCloud[slotCount];
            for (var i = 0; i < slotCount; i++)
            {
                clouds[i] = new SkinPositionMapSet.SlotCloud();
            }

            var triangles = geo.Triangles;
            var vertices = geo.Vertices;
            var triangleSlot = geo.TriangleSlot;
            var perSlot = new Dictionary<int, List<(int tri, float cumulative)>>();
            var areaOf = new Dictionary<int, float>();

            for (var tri = 0; tri < triangleSlot.Length; tri++)
            {
                var slot = triangleSlot[tri];
                if (slot < 0 || slot >= slotCount)
                {
                    continue;
                }

                var p0 = vertices[triangles[tri * 3]];
                var p1 = vertices[triangles[tri * 3 + 1]];
                var p2 = vertices[triangles[tri * 3 + 2]];
                var area = Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5f;
                areaOf.TryGetValue(slot, out var total);
                total += area;
                areaOf[slot] = total;
                if (!perSlot.TryGetValue(slot, out var list))
                {
                    list = new List<(int, float)>();
                    perSlot[slot] = list;
                }

                list.Add((tri, total));
            }

            var maxArea = 0f;
            var rng = new System.Random(90210);
            foreach (var pair in perSlot)
            {
                var list = pair.Value;
                var total = areaOf[pair.Key];
                if (total <= 0f || list.Count == 0)
                {
                    continue;
                }

                maxArea = Mathf.Max(maxArea, total);
                var points = new Vector3[SlotSampleCount];
                for (var i = 0; i < points.Length; i++)
                {
                    var target = (float)rng.NextDouble() * total;
                    var lo = 0;
                    var hi = list.Count - 1;
                    while (lo < hi)
                    {
                        var mid = (lo + hi) / 2;
                        if (list[mid].cumulative < target) { lo = mid + 1; } else { hi = mid; }
                    }

                    var tri = list[lo].tri;
                    var r1 = Mathf.Sqrt((float)rng.NextDouble());
                    var r2 = (float)rng.NextDouble();
                    points[i] = vertices[triangles[tri * 3]] * (1f - r1) +
                                vertices[triangles[tri * 3 + 1]] * (r1 * (1f - r2)) +
                                vertices[triangles[tri * 3 + 2]] * (r1 * r2);
                }

                clouds[pair.Key].Points = points;
            }

            spacing = Mathf.Max(0.005f, Mathf.Sqrt(maxArea / SlotSampleCount));
            return clouds;
        }

        private static void SaveMapSet(SkinPositionMapSet set, string key)
        {
            var assetPath = $"{OutputFolder}/{key}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<SkinPositionMapSet>(assetPath);
            if (existing != null)
            {
                EditorUtility.CopySerialized(set, existing);
                EditorUtility.SetDirty(existing);
                Object.DestroyImmediate(set);
            }
            else
            {
                AssetDatabase.CreateAsset(set, assetPath);
            }
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
            private Vector3[] _normals;
            private Vector2[] _uvs;
            private int[] _triangles;
            private int[] _triangleSlot;
            private readonly Dictionary<string, Vector3> _bonePositions = new();

            public Vector3[] Vertices => _vertices;
            public Vector3[] Normals => _normals;
            public Vector2[] Uvs => _uvs;
            public int[] Triangles => _triangles;
            public int[] TriangleSlot => _triangleSlot;

            public static MeshGeometry From(Mesh mesh, SkinnedMeshRenderer smr, bool skinSlotsOnly)
            {
                try
                {
                    var geo = new MeshGeometry
                    {
                        Bounds = mesh.bounds,
                        _vertices = mesh.vertices,
                        _normals = mesh.normals,
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
                    Valid = true,
                    // Spec 40.8-J: the 3D anchor + outward normal a projected
                    // decal is built on (the UV above only says where the
                    // legacy rect goes).
                    BindPos = (_vertices[i0] + _vertices[i1] + _vertices[i2]) / 3f,
                    BindNormal = TriangleNormal(i0, i1, i2)
                };
            }

            public Vector3 TriangleNormal(int i0, int i1, int i2)
            {
                if (_normals != null && _normals.Length == _vertices.Length)
                {
                    var n = _normals[i0] + _normals[i1] + _normals[i2];
                    if (n.sqrMagnitude > 1e-10f)
                    {
                        return n.normalized;
                    }
                }

                var geometric = Vector3.Cross(_vertices[i1] - _vertices[i0],
                    _vertices[i2] - _vertices[i0]);
                return geometric.sqrMagnitude > 1e-12f ? geometric.normalized : Vector3.up;
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
