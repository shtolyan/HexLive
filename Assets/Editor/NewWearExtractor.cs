#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using UnityEditor;
using UnityEngine;

// ---------------------------------------------------------------------------
//  New-wear extractor (spec §31B.4, "new wear" drop of 2026-07 and the
//  Sweet Jane / Fitness Idol drop of 2026-08).
//
//  Assets/Temp holds one DAZ FBX export per girl per drop, each wearing the
//  SAME garments fitted to her body. This tool pulls the garment meshes out of
//  every FBX and assembles the standard wear assets:
//
//    Assets/ImportedActors/Wear/<Folder>/Meshes/<Actor>.mesh   (per girl)
//    Assets/ImportedActors/Wear/<Folder>/Materials/<mat>.mat   (URP Lit)
//    Assets/ImportedActors/Wear/<Folder>/Textures/*            (copied earlier)
//    Assets/Resources/HexLive/Wear/<simId>/<Name>.prefab       (Wear prefab)
//
//  The prefab follows the molly_copy wear contract (spec §31B.2): root Wear
//  component + its own bone subtree under "hip" + one SkinnedMeshRenderer;
//  per-actor meshes live in WearConfig. Dirt / blood / wetness / tear need no
//  per-item work — NpcActorView drives them through Wear.SetGrime/SetWetness/
//  SetErosion on every equipped garment.
//
//  Runs automatically once after compile (only while some target prefab is
//  missing and the Temp FBXes exist), then rebuilds the GarmentCatalog and
//  re-exports SimData. Re-runs are idempotent; use the Force menu item to
//  stamp everything again after changing specs below.
//
//  Menu: HexLive/Wear/Extract New Wear (Temp FBX)
//        HexLive/Wear/Extract New Wear (Force Re-Extract)
// ---------------------------------------------------------------------------
public static class NewWearExtractor
{
    private const string ImportRoot = "Assets/ImportedActors/Wear";
    private const string WearRoot = "Assets/Resources/HexLive/Wear";

    // Where the automated pipeline drops its manifests — now the ONLY source of
    // garments. tools/wardrobe writes the JSON and the extractor picks it up, so
    // new clothing needs no C# at all.
    private const string DropRoot = "Assets/Editor/WearDrops";

    // FBX file -> which girl the garments are fitted to. One file per girl per
    // drop; a girl may appear more than once (different drops carry different
    // garments), so the renderer index is keyed by (actor, garment), not by file.
    // Empty on purpose. Every drop — including the 2026-07 one that used to be
    // hardcoded here — now lives in Assets/Editor/WearDrops/*.json. Keeping a
    // copy in C# was actively harmful: LoadDrop skips a garment whose simId is
    // already described, and the built-ins were seeded FIRST, so the JSON entry
    // lost. Those eight pieces kept being built from the old `* new wear.fbx`
    // exports, which is how Jana's re-fit silently missed them.
    private static readonly (string path, ActorName actor)[] BuiltInSources = { };

    // Built-ins + every JSON drop, resolved once per domain reload.
    private static (string path, ActorName actor)[] _sources;
    private static GarmentSpec[] _garments;

    private static (string path, ActorName actor)[] Sources
    {
        get { EnsureLoaded(); return _sources; }
    }

    private static GarmentSpec[] Garments
    {
        get { EnsureLoaded(); return _garments; }
    }

    // Every girl the drops cover, each listed once.
    private static ActorName[] Actors => Sources.Select(s => s.actor).Distinct().ToArray();

    private sealed class MatSpec
    {
        public string Source;       // material name inside the FBX
        // Other FBX material names that should land on this same material. A
        // DAZ prop splits one texture atlas across dozens of named surfaces —
        // the headdress has eighteen for the helmet alone — and shipping a
        // submesh per surface would be eighteen draw calls for one hat.
        public string[] Aliases = { };
        public string Texture;      // file under <Folder>/Textures (null = plain color)
        public Color Color = Color.white;
        public float Smoothness = 0.3f;
        public float Metallic;
        public bool DoubleSided = true;
        public bool AlphaClip;
    }

    private sealed class GarmentSpec
    {
        public string SourceKey;    // mesh name inside the FBX
        // Set when the garment arrives as an ASSEMBLY of meshes rather than
        // one: every renderer whose name starts with one of these is welded
        // into a single skinned mesh under SourceKey. See MergeParts.
        public string[] SourceKeys;
        public string Folder;       // ImportedActors/Wear/<Folder>
        public string Name;         // prefab + root GameObject name
        public string SimId;        // Resources/HexLive/Wear/<SimId>/
        public VisualWearLayer Layer;
        public VisualWearSlot[] Slots;
        public VisualWearSlot[] NoHide = { };
        public MatSpec[] Materials;
        public HeelPose Heel;       // default = flat, which is almost everything
    }

    // Empty for the same reason as BuiltInSources: every garment is described in
    // a drop manifest. Kept as a seam rather than deleted so EnsureLoaded still
    // reads as "built-ins plus drops" if something ever has to be hardcoded again.
    private static readonly GarmentSpec[] BuiltInGarments = { };

    // --- JSON drops ---------------------------------------------------------
    //  `tools/wardrobe` writes one of these per batch of new clothing, so the
    //  pipeline never has to edit this file. Shape (see wardrobe/manifest.py):
    //
    //    { "drop": "sweetjane",
    //      "sources":  [ { "fbx": "Assets/Temp/jana sweetjane.fbx", "actor": "Jana" } ],
    //      "garments": [ { "sourceKey": "...", "folder": "...", "name": "...",
    //                      "simId": "clothing.x", "layer": "Wear",
    //                      "slots": ["Chest"], "noHide": [],
    //                      "materials": [ { "source": "tank", "texture": "a.jpg",
    //                                       "smoothness": 0.3, "alphaClip": false } ] } ] }
    //
    //  Unknown enum names and malformed files are reported and skipped rather
    //  than throwing — one bad drop must not take the whole wardrobe down.

    [System.Serializable] private sealed class DropFile
    {
        public string drop;
        public DropSource[] sources;
        public DropGarment[] garments;
    }

    [System.Serializable] private sealed class DropSource
    {
        public string fbx;
        public string actor;
    }

    [System.Serializable] private sealed class DropGarment
    {
        public string sourceKey, folder, name, simId, layer;
        // An accessory can arrive as an ASSEMBLY rather than one mesh: the
        // Jaguar Headdress exports as 29 (a rigid helmet plus 28 feathers and
        // cords). Listing them here merges them into the one skinned mesh the
        // wardrobe contract allows — see MergeParts.
        public string[] sourceKeys;
        public string[] slots, noHide;
        public DropMaterial[] materials;
        public DropHeel heelPose;   // spec §31B.4C — heeled shoes only
    }

    // Straight out of the DAZ foot-pose preset that ships with a heeled shoe.
    // Absent on every other garment, and absent means "flat".
    [System.Serializable] private sealed class DropHeel
    {
        public float foot, toe, lift;
        public float[] axis;        // empty = the bone's X, which is DAZ's axis
    }

    [System.Serializable] private sealed class DropMaterial
    {
        public string source, texture, color;
        public string[] alsoSources;    // more FBX surfaces sharing this material
        public float smoothness = 0.3f, metallic;
        public bool doubleSided = true, alphaClip;
    }

    private static void EnsureLoaded()
    {
        if (_garments != null)
        {
            return;
        }

        var sources = new List<(string, ActorName)>(BuiltInSources);
        var garments = new List<GarmentSpec>(BuiltInGarments);

        if (Directory.Exists(DropRoot))
        {
            foreach (var file in Directory.GetFiles(DropRoot, "*.json").OrderBy(f => f))
            {
                try
                {
                    LoadDrop(file, sources, garments);
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[NewWear] поставка {Path.GetFileName(file)} не прочиталась: {e.Message}");
                }
            }
        }

        _sources = sources.ToArray();
        _garments = garments.ToArray();
    }

    private static void LoadDrop(string file, List<(string, ActorName)> sources, List<GarmentSpec> garments)
    {
        var parsed = JsonUtility.FromJson<DropFile>(File.ReadAllText(file));
        if (parsed == null)
        {
            throw new IOException("не разобрался JSON");
        }

        foreach (var s in parsed.sources ?? new DropSource[0])
        {
            if (!System.Enum.TryParse<ActorName>(s.actor, out var actor))
            {
                Debug.LogError($"[NewWear] {Path.GetFileName(file)}: неизвестная девушка '{s.actor}'");
                continue;
            }

            // A re-run of the same drop must not index the same FBX twice.
            if (!sources.Any(existing => existing.Item1 == s.fbx))
            {
                sources.Add((s.fbx, actor));
            }
        }

        foreach (var g in parsed.garments ?? new DropGarment[0])
        {
            if (garments.Any(existing => existing.SimId == g.simId))
            {
                Debug.LogWarning($"[NewWear] {Path.GetFileName(file)}: {g.simId} уже описан — пропущен");
                continue;
            }

            garments.Add(new GarmentSpec
            {
                SourceKey = g.sourceKey,
                SourceKeys = g.sourceKeys != null && g.sourceKeys.Length > 0 ? g.sourceKeys : null,
                Folder = g.folder,
                Name = g.name,
                SimId = g.simId,
                Layer = ParseEnum(g.layer, VisualWearLayer.Wear, file),
                Slots = ParseSlots(g.slots, file),
                NoHide = ParseSlots(g.noHide, file),
                Materials = (g.materials ?? new DropMaterial[0]).Select(m => new MatSpec
                {
                    Source = m.source,
                    Aliases = m.alsoSources ?? new string[0],
                    Texture = string.IsNullOrEmpty(m.texture) ? null : m.texture,
                    Color = ParseColor(m.color),
                    Smoothness = m.smoothness,
                    Metallic = m.metallic,
                    DoubleSided = m.doubleSided,
                    AlphaClip = m.alphaClip,
                }).ToArray(),
                Heel = ParseHeel(g.heelPose),
            });
        }
    }

    private static HeelPose ParseHeel(DropHeel heel)
    {
        if (heel == null)
        {
            return default;
        }

        return new HeelPose
        {
            footDegrees = heel.foot,
            toeDegrees = heel.toe,
            lift = heel.lift,
            axis = heel.axis != null && heel.axis.Length == 3
                ? new Vector3(heel.axis[0], heel.axis[1], heel.axis[2])
                : Vector3.zero,
        };
    }

    private static T ParseEnum<T>(string value, T fallback, string file) where T : struct
    {
        if (string.IsNullOrEmpty(value))
        {
            return fallback;
        }

        if (System.Enum.TryParse<T>(value, out var parsed))
        {
            return parsed;
        }

        Debug.LogError($"[NewWear] {Path.GetFileName(file)}: не знаю {typeof(T).Name} '{value}', беру {fallback}");
        return fallback;
    }

    private static VisualWearSlot[] ParseSlots(string[] names, string file)
    {
        if (names == null)
        {
            return new VisualWearSlot[0];
        }

        var slots = new List<VisualWearSlot>();
        foreach (var name in names)
        {
            if (System.Enum.TryParse<VisualWearSlot>(name, out var slot))
            {
                slots.Add(slot);
            }
            else
            {
                Debug.LogError($"[NewWear] {Path.GetFileName(file)}: нет такого слота '{name}'");
            }
        }

        return slots.ToArray();
    }

    // "RRGGBB" / "#RRGGBB"; empty means plain white.
    private static Color ParseColor(string hex)
    {
        if (string.IsNullOrEmpty(hex))
        {
            return Color.white;
        }

        return ColorUtility.TryParseHtmlString(hex.StartsWith("#") ? hex : "#" + hex, out var c)
            ? c
            : Color.white;
    }

    // One-shot auto-run: fires after every compile, does nothing once all nine
    // prefabs exist (or when the Temp FBX drop is gone from this machine).
    [InitializeOnLoadMethod]
    private static void AutoRun()
    {
        EditorApplication.delayCall += () =>
        {
            if (Application.productName != "HexLive") return;
            if (Sources.Any(s => !File.Exists(s.path))) return;
            if (Garments.All(g => File.Exists(PrefabPath(g)))) return;
            Run(force: false);
        };
    }

    [MenuItem("HexLive/Wear/Extract New Wear (Temp FBX)")]
    private static void RunMenu() => Run(force: false);

    [MenuItem("HexLive/Wear/Extract New Wear (Force Re-Extract)")]
    private static void RunForceMenu() => Run(force: true);

    private static string PrefabPath(GarmentSpec g) => $"{WearRoot}/{g.SimId}/{g.Name}.prefab";

    private static void Run(bool force)
    {
        // Editing a drop JSON re-imports the asset but does not reload the
        // domain, so the cached tables would be stale on a manual re-run.
        _sources = null;
        _garments = null;

        var missingFbx = Sources.Where(s => !File.Exists(s.path)).Select(s => s.path).ToList();
        if (missingFbx.Count > 0)
        {
            Debug.LogError("[NewWear] FBX not found: " + string.Join(", ", missingFbx));
            return;
        }

        // Instantiate the three FBX rigs once; index every garment renderer.
        var instances = new List<GameObject>();
        // One girl now has several rigs — one FBX per drop — so this is a list.
        // Keyed by girl alone, the second drop simply overwrote the first and a
        // welded garment went looking for its helmet in the Sweet Jane export.
        var rigs = new Dictionary<ActorName, List<GameObject>>();
        var renderers = new Dictionary<(ActorName actor, string key), SkinnedMeshRenderer>();
        try
        {
            foreach (var (path, actor) in Sources)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                {
                    Debug.LogError($"[NewWear] can't load {path}");
                    return;
                }

                var instance = Object.Instantiate(prefab);
                instance.hideFlags = HideFlags.HideAndDontSave;
                instances.Add(instance);
                if (!rigs.TryGetValue(actor, out var forActor))
                {
                    rigs[actor] = forActor = new List<GameObject>();
                }

                forActor.Add(instance);
                foreach (var r in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var key = Garments.FirstOrDefault(g =>
                        r.sharedMesh != null && r.sharedMesh.name.StartsWith(g.SourceKey) ||
                        r.name.StartsWith(g.SourceKey))?.SourceKey;
                    if (key != null)
                    {
                        renderers[(actor, key)] = r;
                    }
                }
            }

            var done = 0;
            var skipped = 0;
            var log = new System.Text.StringBuilder();
            foreach (var g in Garments)
            {
                if (!force && File.Exists(PrefabPath(g)))
                {
                    skipped++;
                    continue;
                }

                if (ExtractGarment(g, renderers, rigs, log))
                {
                    done++;
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[NewWear] extracted {done}, skipped (already built) {skipped}\n{log}");

            if (done > 0)
            {
                // Materialize the new GarmentLibrary rows as GarmentDefinition
                // assets + refresh the catalog, then re-export simdata.json
                // (spec §59.3 — headless probes refuse to run without it).
                EditorApplication.ExecuteMenuItem("HexLive/Garments/Rebuild Catalog From Defaults");
                EditorApplication.ExecuteMenuItem("HexLive/Export Sim Data (JSON)");
            }
        }
        finally
        {
            foreach (var instance in instances)
            {
                Object.DestroyImmediate(instance);
            }
        }
    }

    private static bool ExtractGarment(
        GarmentSpec g,
        Dictionary<(ActorName, string), SkinnedMeshRenderer> renderers,
        Dictionary<ActorName, List<GameObject>> rigs,
        System.Text.StringBuilder log)
    {
        if (g.SourceKeys != null)
        {
            foreach (var pair in rigs)
            {
                // Only one of a girl's rigs holds this kit; the rest belong to
                // other drops and are expected to come back empty.
                foreach (var rig in pair.Value)
                {
                    var welded = MergeParts(g, rig);
                    if (welded != null)
                    {
                        renderers[(pair.Key, g.SourceKey)] = welded;
                        break;
                    }
                }
            }
        }

        if (!renderers.TryGetValue((ActorName.Jana, g.SourceKey), out var reference))
        {
            Debug.LogError($"[NewWear] {g.SourceKey}: no renderer in the jana FBX — skipped");
            return false;
        }

        EnsureFolder($"{ImportRoot}/{g.Folder}/Meshes");
        EnsureFolder($"{ImportRoot}/{g.Folder}/Materials");
        EnsureFolder($"{WearRoot}/{g.SimId}");

        // --- meshes: one fitted copy per girl --------------------------------
        var meshes = new Dictionary<ActorName, Mesh>();
        foreach (var actor in Actors)
        {
            if (!renderers.TryGetValue((actor, g.SourceKey), out var r) || r.sharedMesh == null)
            {
                // Expected when a girl was not part of this garment's drop —
                // she simply cannot wear it until someone re-exports her.
                Debug.LogWarning($"[NewWear] {g.SourceKey}: no mesh for {actor}");
                continue;
            }

            var path = $"{ImportRoot}/{g.Folder}/Meshes/{actor}.mesh";
            var copy = Object.Instantiate(r.sharedMesh);
            copy.name = actor.ToString();
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                // Keep the GUID stable across re-runs — prefab refs survive.
                EditorUtility.CopySerialized(copy, existing);
                Object.DestroyImmediate(copy);
                meshes[actor] = existing;
            }
            else
            {
                AssetDatabase.CreateAsset(copy, path);
                meshes[actor] = copy;
            }
        }

        if (!meshes.ContainsKey(ActorName.Jana))
        {
            Debug.LogError($"[NewWear] {g.SourceKey}: no Jana mesh — skipped");
            return false;
        }

        // --- materials (URP Lit, flat albedo per the art style) --------------
        var mats = new Dictionary<string, Material>();
        foreach (var spec in g.Materials)
        {
            var built = BuildMaterial(g, spec);
            mats[spec.Source] = built;
            foreach (var alias in spec.Aliases)
            {
                mats[alias] = built;
            }
        }

        // --- prefab -----------------------------------------------------------
        var root = new GameObject(g.Name);
        try
        {
            BuildBonesAndRenderer(g, reference, meshes[ActorName.Jana], mats, root);
            FillWearComponent(g, meshes, root);
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath(g));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }

        log.AppendLine($"  {g.SourceKey} -> {PrefabPath(g)} ({meshes.Count} meshes, {mats.Count} mats)");
        return true;
    }

    // --- welding an assembly into one garment --------------------------------
    //
    //  Some DAZ products are not a garment but a kit. The Jaguar Headdress
    //  exports as twenty-nine renderers: a rigid helmet parented straight to
    //  the head bone, plus twenty-eight feathers and cords, each skinned to ONE
    //  private bone. The wardrobe takes exactly one SkinnedMeshRenderer and one
    //  Mesh per garment, so a kit cannot be worn at all until it is welded.
    //
    //  Those private bones are dead weight HERE: Wear.Construct binds a
    //  garment's skeleton to the body BY NAME, and no girl has a bone called
    //  "Bone". Nothing can ever drive them, so the feathers cannot sway
    //  whatever we do — they are rigid bodies hanging off the head. That makes
    //  this merge exact rather than a compromise: bake every part into the one
    //  bone they all really follow and the result matches DAZ, at one bone and
    //  one draw call per material.
    //
    //  The maths is just the skinning identity. A skinned vertex lands at
    //      world = bone.localToWorld * bindpose * v
    //  so a part's authored-space matrix is bone.localToWorld * bindpose⁻¹,
    //  read off the instance while it still stands in its bind pose. Every part
    //  is pushed through that into the rig's own space, and one bindpose
    //  (anchor.worldToLocal * rig.localToWorld) sends the whole thing back.
    private static SkinnedMeshRenderer MergeParts(GarmentSpec g, GameObject rig)
    {
        if (g.Materials == null || g.Materials.Length == 0)
        {
            Debug.LogError($"[NewWear] {g.SourceKey}: сборный предмет без материалов");
            return null;
        }

        // The body is simply the rig's richest skeleton — 172 bones against the
        // one bone every prop part carries.
        var body = rig.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .OrderByDescending(r => r.bones.Length)
            .FirstOrDefault();
        var bodyBones = new HashSet<Transform>(
            body != null ? body.bones.Where(b => b != null) : Enumerable.Empty<Transform>());
        if (bodyBones.Count == 0)
        {
            Debug.LogError($"[NewWear] {g.SourceKey}: в FBX нет скелета тела");
            return null;
        }

        var parts = new List<(Renderer renderer, Mesh mesh, Matrix4x4 toRig, Transform anchor)>();
        Transform shared = null;
        foreach (var r in rig.GetComponentsInChildren<Renderer>(true))
        {
            if (!g.SourceKeys.Any(k => r.name.StartsWith(k, System.StringComparison.Ordinal)))
            {
                continue;
            }

            Mesh mesh;
            Matrix4x4 authored;
            Transform from;
            if (r is SkinnedMeshRenderer skinned)
            {
                mesh = skinned.sharedMesh;
                var bind = mesh != null ? mesh.bindposes : null;
                var i = System.Array.FindIndex(skinned.bones, b => b != null);
                if (mesh == null || bind == null || i < 0 || i >= bind.Length)
                {
                    Debug.LogWarning($"[NewWear] {g.SourceKey}: {r.name} без пригодного скиннинга — пропущен");
                    continue;
                }

                authored = skinned.bones[i].localToWorldMatrix * bind[i].inverse;
                from = skinned.bones[i];
            }
            else
            {
                var filter = r.GetComponent<MeshFilter>();
                mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null)
                {
                    continue;
                }

                authored = r.transform.localToWorldMatrix;
                from = r.transform;
            }

            var anchor = NearestBone(from, bodyBones);
            if (anchor == null)
            {
                Debug.LogWarning($"[NewWear] {g.SourceKey}: {r.name} ни к чему не привязан — пропущен");
                continue;
            }

            shared = shared == null ? anchor : CommonAncestor(shared, anchor);
            parts.Add((r, mesh, rig.transform.worldToLocalMatrix * authored, anchor));
        }

        if (parts.Count == 0 || shared == null)
        {
            // Not an error: the caller offers every rig the girl has, and only
            // one of them was exported with this kit on her.
            return null;
        }

        // --- pour every part into one buffer ---------------------------------
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var tangents = new List<Vector4>();
        var uvs = new List<Vector2>();
        var order = new List<MatSpec>();
        var triangles = new Dictionary<MatSpec, List<int>>();
        var samples = new Dictionary<MatSpec, Material>();

        foreach (var (renderer, mesh, toRig, _) in parts)
        {
            var offset = vertices.Count;
            var src = mesh.vertices;
            var srcNormals = mesh.normals;
            var srcTangents = mesh.tangents;
            var srcUv = mesh.uv;
            for (var i = 0; i < src.Length; i++)
            {
                vertices.Add(toRig.MultiplyPoint3x4(src[i]));
                normals.Add(i < srcNormals.Length
                    ? toRig.MultiplyVector(srcNormals[i]).normalized
                    : Vector3.up);
                if (i < srcTangents.Length)
                {
                    var t = srcTangents[i];
                    var dir = toRig.MultiplyVector(new Vector3(t.x, t.y, t.z)).normalized;
                    tangents.Add(new Vector4(dir.x, dir.y, dir.z, t.w));
                }
                else
                {
                    tangents.Add(new Vector4(1f, 0f, 0f, -1f));
                }

                uvs.Add(i < srcUv.Length ? srcUv[i] : Vector2.zero);
            }

            var slots = renderer.sharedMaterials;
            for (var sub = 0; sub < mesh.subMeshCount; sub++)
            {
                var slot = sub < slots.Length ? slots[sub] : null;
                var spec = SpecFor(g, slot != null ? slot.name.Replace(" (Instance)", "") : "");
                if (!triangles.TryGetValue(spec, out var bucket))
                {
                    bucket = new List<int>();
                    triangles[spec] = bucket;
                    samples[spec] = slot;
                    order.Add(spec);
                }

                foreach (var index in mesh.GetTriangles(sub))
                {
                    bucket.Add(index + offset);
                }
            }
        }

        // Every vertex rides the one bone the whole kit really follows.
        var weights = new BoneWeight[vertices.Count];
        for (var i = 0; i < weights.Length; i++)
        {
            weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
        }

        var merged = new Mesh
        {
            name = g.SourceKey,
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
        };
        merged.SetVertices(vertices);
        merged.SetNormals(normals);
        merged.SetTangents(tangents);
        merged.SetUVs(0, uvs);
        merged.bindposes = new[] { shared.worldToLocalMatrix * rig.transform.localToWorldMatrix };
        merged.boneWeights = weights;
        merged.subMeshCount = order.Count;
        for (var i = 0; i < order.Count; i++)
        {
            merged.SetTriangles(triangles[order[i]], i);
        }

        merged.RecalculateBounds();

        var go = new GameObject(g.SourceKey + " (merged)");
        go.transform.SetParent(rig.transform, false);
        var smr = go.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = merged;
        smr.bones = new[] { shared };
        smr.rootBone = shared;
        smr.localBounds = merged.bounds;
        // Real FBX materials, not placeholders: their names go through the same
        // alias table BuildMaterial was keyed by, so the mapping stays honest.
        smr.sharedMaterials = order.Select(spec => samples[spec]).ToArray();

        Debug.Log($"[NewWear] {g.SourceKey}: собрано {parts.Count} част(и) → " +
                  $"{vertices.Count} вершин, {order.Count} материал(а), кость '{shared.name}'");
        return smr;
    }

    private static MatSpec SpecFor(GarmentSpec g, string materialName)
    {
        foreach (var spec in g.Materials)
        {
            if (spec.Source == materialName ||
                System.Array.IndexOf(spec.Aliases, materialName) >= 0)
            {
                return spec;
            }
        }

        return g.Materials[0];
    }

    private static Transform NearestBone(Transform from, HashSet<Transform> bones)
    {
        for (var t = from; t != null; t = t.parent)
        {
            if (bones.Contains(t))
            {
                return t;
            }
        }

        return null;
    }

    private static Transform CommonAncestor(Transform a, Transform b)
    {
        for (var x = a; x != null; x = x.parent)
        {
            for (var y = b; y != null; y = y.parent)
            {
                if (x == y)
                {
                    return x;
                }
            }
        }

        return a;
    }

    private static Material BuildMaterial(GarmentSpec g, MatSpec spec)
    {
        var path = $"{ImportRoot}/{g.Folder}/Materials/{Sanitize(spec.Source)}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(mat, path);
        }
        else
        {
            mat.shader = Shader.Find("Universal Render Pipeline/Lit");
        }

        mat.SetColor("_BaseColor", spec.Color);
        mat.SetFloat("_Smoothness", spec.Smoothness);
        mat.SetFloat("_Metallic", spec.Metallic);
        if (spec.DoubleSided)
        {
            mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
        }

        if (spec.Texture != null)
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(
                $"{ImportRoot}/{g.Folder}/Textures/{spec.Texture}");
            if (tex == null)
            {
                Debug.LogWarning($"[NewWear] {g.Folder}: texture {spec.Texture} not imported yet");
            }

            mat.SetTexture("_BaseMap", tex);
        }

        if (spec.AlphaClip)
        {
            mat.SetFloat("_AlphaClip", 1f);
            mat.SetFloat("_Cutoff", 0.3f);
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
        }

        EditorUtility.SetDirty(mat);
        return mat;
    }

    // Clone the reference skeleton subtree under "hip" (pruned to the bones
    // this garment skins to + their ancestors) and add the renderer child.
    private static void BuildBonesAndRenderer(
        GarmentSpec g, SkinnedMeshRenderer reference, Mesh defaultMesh,
        Dictionary<string, Material> mats, GameObject root)
    {
        var srcHip = FindGarmentHip(reference);
        if (srcHip == null)
        {
            throw new IOException($"{g.SourceKey}: no 'hip' bone in the source FBX");
        }

        var needed = new HashSet<string>();
        foreach (var bone in reference.bones)
        {
            if (bone == null)
            {
                continue;
            }

            // The bone itself plus every ancestor up to (and including) hip.
            for (var t = bone; t != null; t = t.parent)
            {
                needed.Add(t.name);
                if (t == srcHip)
                {
                    break;
                }
            }
        }

        if (reference.rootBone != null)
        {
            needed.Add(reference.rootBone.name);
        }

        var clones = new Dictionary<string, Transform>();
        var hipClone = CloneBoneSubtree(srcHip, root.transform, needed, clones);

        var meshGo = new GameObject(g.Name + " Mesh");
        meshGo.transform.SetParent(root.transform, false);
        var smr = meshGo.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = defaultMesh;
        // Quietly collapsing an unresolved bone onto the root is how the Sweet
        // Jane skirt shipped welded to the hip: its two thigh bones were absent
        // from the cloned skeleton, so the hem never followed the legs and it
        // read as a fitting problem rather than a broken bind.
        var missing = reference.bones
            .Where(b => b == null || !clones.ContainsKey(b.name))
            .Select(b => b != null ? b.name : "<null>")
            .Distinct()
            .ToArray();
        if (missing.Length > 0)
        {
            throw new IOException(
                $"{g.SourceKey}: {missing.Length} bone(s) missing from the cloned " +
                $"skeleton under '{srcHip.name}': {string.Join(", ", missing)}");
        }

        smr.bones = reference.bones.Select(b => clones[b.name]).ToArray();
        smr.rootBone = reference.rootBone != null &&
            clones.TryGetValue(reference.rootBone.name, out var rb)
            ? rb
            : hipClone;
        smr.localBounds = reference.localBounds;
        smr.sharedMaterials = reference.sharedMaterials
            .Select(m =>
            {
                var name = m != null ? m.name.Replace(" (Instance)", "") : "";
                return mats.TryGetValue(name, out var mapped) ? mapped : mats.Values.First();
            })
            .ToArray();
    }

    private static Transform CloneBoneSubtree(
        Transform src, Transform parent, HashSet<string> needed,
        Dictionary<string, Transform> clones)
    {
        var clone = new GameObject(src.name).transform;
        clone.SetParent(parent, false);
        clone.localPosition = src.localPosition;
        clone.localRotation = src.localRotation;
        clone.localScale = src.localScale;
        clones[src.name] = clone;
        foreach (Transform child in src)
        {
            if (SubtreeNeeded(child, needed))
            {
                CloneBoneSubtree(child, clone, needed, clones);
            }
        }

        return clone;
    }

    private static bool SubtreeNeeded(Transform t, HashSet<string> needed)
    {
        if (needed.Contains(t.name))
        {
            return true;
        }

        foreach (Transform child in t)
        {
            if (SubtreeNeeded(child, needed))
            {
                return true;
            }
        }

        return false;
    }

    private static void FillWearComponent(
        GarmentSpec g, Dictionary<ActorName, Mesh> meshes, GameObject root)
    {
        // Fit scales are tuned by hand in WardrobeTest and are NOT authored here
        // (every garment lands at 1.0). Carry the tuned values over, or a force
        // re-extract quietly throws away the fitting pass on every older piece.
        var tuned = ReadTunedFit(g);

        var wear = root.AddComponent<Wear>();
        var so = new SerializedObject(wear);

        var configs = so.FindProperty("configs");
        configs.arraySize = meshes.Count;
        var i = 0;
        foreach (var pair in meshes.OrderBy(p => (int)p.Key))
        {
            var e = configs.GetArrayElementAtIndex(i++);
            var fit = tuned.TryGetValue(pair.Key, out var f) ? f : (scale: 1f, heightOffset: 0f);
            e.FindPropertyRelative("actorName").enumValueIndex = (int)pair.Key;
            e.FindPropertyRelative("scale").floatValue = fit.scale;
            e.FindPropertyRelative("heightOffset").floatValue = fit.heightOffset;
            e.FindPropertyRelative("mesh").objectReferenceValue = pair.Value;
        }

        var heel = so.FindProperty("heel");
        heel.FindPropertyRelative("footDegrees").floatValue = g.Heel.footDegrees;
        heel.FindPropertyRelative("toeDegrees").floatValue = g.Heel.toeDegrees;
        heel.FindPropertyRelative("lift").floatValue = g.Heel.lift;
        heel.FindPropertyRelative("axis").vector3Value = g.Heel.axis;

        WriteSlotList(so.FindProperty("slots"), g.Slots);
        WriteSlotList(so.FindProperty("noHideUnderwearSlots"), g.NoHide);
        so.FindProperty("layer").enumValueIndex = (int)g.Layer;
        so.FindProperty("gender").enumValueIndex = (int)VisualGender.Female;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // Read the fit tuned into the prefab we are about to overwrite. Empty on a
    // first extract, and for any girl who was not part of the earlier drop.
    private static Dictionary<ActorName, (float scale, float heightOffset)> ReadTunedFit(
        GarmentSpec g)
    {
        var tuned = new Dictionary<ActorName, (float scale, float heightOffset)>();
        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath(g));
        if (existing == null || !existing.TryGetComponent<Wear>(out var wear))
        {
            return tuned;
        }

        var configs = new SerializedObject(wear).FindProperty("configs");
        for (var i = 0; i < configs.arraySize; i++)
        {
            var e = configs.GetArrayElementAtIndex(i);
            tuned[(ActorName)e.FindPropertyRelative("actorName").enumValueIndex] = (
                e.FindPropertyRelative("scale").floatValue,
                e.FindPropertyRelative("heightOffset").floatValue);
        }

        return tuned;
    }

    private static void WriteSlotList(SerializedProperty list, VisualWearSlot[] slots)
    {
        list.arraySize = slots.Length;
        for (var i = 0; i < slots.Length; i++)
        {
            list.GetArrayElementAtIndex(i).enumValueIndex = (int)slots[i];
        }
    }

    // A DAZ FBX of a dressed figure carries ONE skeleton copy PER FITTED GARMENT
    // on top of the figure's own, each rooted at its own node called "hip" and
    // pruned by DAZ to just the bones that garment is weighted to. Searching the
    // whole file for "hip" lands on whichever copy comes first — a foreign one,
    // whose pruning has nothing to do with this garment: the Sweet Jane FBX has
    // six of them, and the tank's copy (torso only) has no thigh bones at all.
    // Walk up from this garment's own bones instead; copies are siblings, never
    // nested, so the nearest "hip" ancestor is always the right one.
    private static Transform FindGarmentHip(SkinnedMeshRenderer reference)
    {
        foreach (var bone in reference.bones)
        {
            for (var t = bone; t != null; t = t.parent)
            {
                if (t.name == "hip")
                {
                    return t;
                }
            }
        }

        return null;
    }

    private static string Sanitize(string name)
    {
        return string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
        {
            return;
        }

        var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
        var leaf = Path.GetFileName(path);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }
}
#endif
