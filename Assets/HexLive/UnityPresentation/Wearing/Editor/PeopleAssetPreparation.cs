#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace HexLive.UnityPresentation.Wearing.Editor
{
// Preparation only: never invokes a player or AssetBundle build.
public static class PeopleAssetPreparation
{
    public const string Root = "Assets/HexLiveContent/People";
    public static void ImportAndAudit()
    {
        var manifest = JObject.Parse(File.ReadAllText(Root + "/Source/source-manifest.json"));
        var normalPaths = new HashSet<string>(manifest["materials"].Children<JProperty>()
            .SelectMany(p => p.Value["images"] ?? new JArray())
            .Where(i => (i["links"] ?? new JArray()).Any(l => (string)l["toSocket"] == "Color" && ((string)l["toNode"] ?? "").Contains("Normal")))
            .Select(i => Root + "/Source/" + (string)i["image"]));
        foreach (var path in AssetDatabase.FindAssets("t:Texture2D", new[] { Root + "/Source/Textures" }).Select(AssetDatabase.GUIDToAssetPath))
        {
            var t = (TextureImporter)AssetImporter.GetAtPath(path);
            t.maxTextureSize = 1024;
            t.isReadable = false;
            t.mipmapEnabled = true;
            t.textureType = normalPaths.Contains(path) ? TextureImporterType.NormalMap : TextureImporterType.Default;
            t.sRGBTexture = !normalPaths.Contains(path);
            t.alphaSource = TextureImporterAlphaSource.FromInput;
            t.textureCompression = TextureImporterCompression.Compressed;
            t.SaveAndReimport();
        }
        foreach (var m in manifest["models"])
        {
            var path = Root + "/Source/" + (string)m["path"];
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            if (importer == null) throw new InvalidOperationException("Missing model " + path);
            importer.importAnimation = false;
            importer.importBlendShapes = true;
            importer.importCameras = false;
            importer.importLights = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.isReadable = true; // Existing skin/garment paint and amputation paths read geometry.
            importer.optimizeGameObjects = false;
            importer.optimizeBones = true;
            importer.skinWeights = ModelImporterSkinWeights.Custom;
            importer.maxBonesPerVertex = 32; // Preserve authored weights until deformation comparison.
            importer.minBoneWeight = 0f;
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            if ((string)m["kind"] == "actor")
            {
                importer.importNormals = ModelImporterNormals.Calculate;
                importer.normalSmoothingSource = ModelImporterNormalSmoothingSource.FromAngle;
                importer.normalSmoothingAngle = 180f;
                importer.importTangents = ModelImporterTangents.None;
                importer.importBlendShapeNormals = ModelImporterNormals.None;
                importer.weldVertices = true;
                ConfigureHumanoidImport(importer, path);
            }
            importer.SaveAndReimport();
        }
        Audit();
    }

    public static void FinishPreparation()
    {
        foreach (var actor in new[] { "Marta", "Kshishtof" })
        {
            var path = Root + "/Source/Models/" + actor + ".fbx";
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            importer.normalSmoothingSource = ModelImporterNormalSmoothingSource.FromAngle;
            importer.normalSmoothingAngle = 180f;
            importer.importTangents = ModelImporterTangents.None;
            importer.SaveAndReimport();
        }
        Audit();
        PreparePrefabs();
        ValidateWearAndRun();
        GeneratePaintMaps();
        PeopleCatalogPreparation.Prepare();
        PeoplePreviewScene.Prepare();
        if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null) RenderReview();
    }

    public static void ValidatePrepared()
    {
        ValidateWearAndRun();
        GeneratePaintMaps();
        PeopleCatalogPreparation.Prepare();
        PeoplePreviewScene.Prepare();
        if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null) RenderReview();
    }

    public static void GeneratePaintMaps()
    {
        var baker = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("HexLive.UnityDebug.Editor.PaintPointMapGenerator")).First(t => t != null);
        baker.GetMethod("GenerateWebGLPeople").Invoke(null, null);
    }

    public static void PrepareAll()
    {
        ImportAndAudit();
        PreparePrefabs();
        ValidatePrepared();
        AuditPaintMaps();
        if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null) RenderFaces();
    }

    public static void ValidateWearAndRun()
    {
        var preparation = JObject.Parse(File.ReadAllText(Root + "/Validation/preparation.json"));
        var results = new JArray();
        var clip = AssetDatabase.LoadAllAssetsAtPath("Assets/ImportedActors/AnimLibrary/Male@Run.fbx").OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal));
        if (clip == null) throw new InvalidOperationException("Run animation missing");
        foreach (string actor in new[] { "Marta", "Kshishtof" })
        {
            var go = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Actors/" + actor + ".prefab"));
            var graph = PlayableGraph.Create("PeopleRunValidation");
            try
            {
                var bones = go.GetComponent<BodyBones>();
                var actorName = (ActorName)Enum.Parse(typeof(ActorName), actor);
                bones.Construct(actorName, 123);
                foreach (var item in preparation["prefabs"].Where(p => (string)p["actor"] == actor && (string)p["kind"] != "actor"))
                {
                    var part = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>((string)item["path"]), bones.WearTransform);
                    var before = CaptureVertices(part);
                    part.GetComponent<Wear>().Construct(actorName, bones, "people-validation");
                    var after = CaptureVertices(part);
                    float max = 0;
                    foreach (var entry in before)
                    {
                        var next = after[entry.Key];
                        for (int i = 0; i < next.Length; i++) max = Mathf.Max(max, Vector3.Distance(entry.Value[i], next[i]));
                    }
                    results.Add(new JObject { ["actor"] = actor, ["item"] = (string)item["id"], ["restBindingMaxErrorMetres"] = max });
                    if (max > .003f) throw new InvalidOperationException(actor + "/" + item["id"] + ": rest binding moved vertices " + max + " m");
                }
                if (actor == "Kshishtof")
                {
                    var genitals = go.GetComponentsInChildren<Transform>(true).Single(t => t.name.EndsWith("_Genitals", StringComparison.Ordinal)).gameObject;
                    if (!genitals.activeSelf) throw new InvalidOperationException("Genitals hidden before underwear");
                    var briefs = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Wear/Kshishtof_PrimalBriefs.prefab").GetComponent<Wear>();
                    bones.Equip("people-validation-briefs", briefs, null);
                    if (genitals.activeSelf) throw new InvalidOperationException("Genitals visible through underwear");
                    results.Add(new JObject { ["actor"] = actor, ["genitalsHiddenByBriefs"] = true });
                }
                var animator = go.GetComponent<Animator>();
                animator.runtimeAnimatorController = null;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Rebind();
                var playable = AnimationClipPlayable.Create(graph, clip);
                var output = AnimationPlayableOutput.Create(graph, "run", animator);
                output.SetSourcePlayable(playable);
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                graph.Play();
                float maxSpan = 0, footMotion = 0;
                Vector3 firstFoot = Vector3.zero;
                for (int frame = 0; frame <= 18; frame++)
                {
                    playable.SetTime(clip.length * frame / 18d);
                    graph.Evaluate(0);
                    var foot = bones.GetBone("lFoot").position;
                    if (frame == 0) firstFoot = foot;
                    else footMotion = Mathf.Max(footMotion, Vector3.Distance(firstFoot, foot));
                    foreach (var points in CaptureVertices(go).Values)
                    {
                        var bounds = new Bounds(points[0], Vector3.zero);
                        foreach (var p in points)
                        {
                            if (float.IsNaN(p.x) || float.IsInfinity(p.x) || float.IsNaN(p.y) || float.IsInfinity(p.y) || float.IsNaN(p.z) || float.IsInfinity(p.z)) throw new InvalidOperationException("Non-finite run vertex");
                            bounds.Encapsulate(p);
                        }
                        maxSpan = Mathf.Max(maxSpan, bounds.size.magnitude);
                    }
                }
                if (footMotion < .01f) throw new InvalidOperationException(actor + ": run clip did not move feet");
                if (maxSpan > 5f || maxSpan < .5f) throw new InvalidOperationException(actor + ": incorrect run scale/bounds " + maxSpan);
                results.Add(new JObject { ["actor"] = actor, ["runSampleFrames"] = 19, ["footMotionMetres"] = footMotion, ["maxRendererSpanMetres"] = maxSpan });
            }
            finally { if (graph.IsValid()) graph.Destroy(); UnityEngine.Object.DestroyImmediate(go); }
        }
        File.WriteAllText(Root + "/Validation/wear-run.json", new JObject { ["passed"] = true, ["visualClippingReviewed"] = false, ["results"] = results }.ToString());
    }

    private static void ApplyLowPolyHairMaterial(Material material)
    {
        // Thousands of tiny packed face islands bleed at 512px/mip distances.
        // Solid matte hair keeps the low-poly silhouette readable without that atlas noise.
        material.SetTexture("_BaseMap", null);
        material.SetColor("_BaseColor", new Color(.23f, .12f, .055f, 1f));
        material.SetFloat("_Smoothness", .08f);
    }

    public static void FinishVisualReview()
    {
        foreach (var path in AssetDatabase.FindAssets("t:Material", new[] { Root + "/Materials" }).Select(AssetDatabase.GUIDToAssetPath))
            if (path.EndsWith("_WebGL__Base.mat", StringComparison.Ordinal))
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                ApplyLowPolyHairMaterial(material); EditorUtility.SetDirty(material);
            }
        AssetDatabase.SaveAssets();
        PeopleCatalogPreparation.Prepare();
        RenderReview();
        RenderFaces();
        AuditPaintMaps();
        var builder = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("AtomicContentBatchBuild")).First(t => t != null);
        builder.GetMethod("AuditPeopleCatalog").Invoke(null, null);
        AssetDatabase.Refresh();
    }

    public static void AuditPaintMaps()
    {
        var rows = new JArray();
        foreach (string actor in new[] { "Marta", "Kshishtof" })
        {
            var map = AssetDatabase.LoadAssetAtPath<PaintPointMap>(Root + "/PaintMaps/skin_" + actor + ".asset");
            if (map == null || !map.HasProjectedFrames) throw new InvalidOperationException("Missing projected skin map " + actor);
            foreach (var zone in map.Zones)
            {
                int valid = zone.Points.Count(p => p.Valid);
                rows.Add(new JObject { ["actor"] = actor, ["zone"] = zone.Zone, ["valid"] = valid, ["total"] = zone.Points.Length });
                if (valid == 0) throw new InvalidOperationException("Empty skin paint zone " + actor + "/" + zone.Zone);
            }
            var positions = AssetDatabase.LoadAssetAtPath<SkinPositionMapSet>(Root + "/PaintMaps/skinpos_" + actor + ".asset");
            if (positions == null || positions.GroupOf(0) < 0 || positions.GroupOf(1) != -1 || positions.GroupOf(2) != -1 || positions.GroupPositionMaps.Any(t => t == null))
                throw new InvalidOperationException("Incorrect skin/eye/mouth map slots " + actor);
        }
        File.WriteAllText(Root + "/Validation/paint-map-coverage.json", new JObject { ["passed"] = true, ["zones"] = rows }.ToString());
    }

    public static void RenderFaces()
    {
        foreach (string actor in new[] { "Marta", "Kshishtof" })
        {
            var go = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Actors/" + actor + ".prefab"));
            try
            {
                var skin = go.GetComponentsInChildren<SkinnedMeshRenderer>().Single(r => r.sharedMesh.blendShapeCount == 109);
                foreach (string expression in new[] { "Neutral", "eCTRLEyesClosed", "eCTRLMouthSmileSimple", "eCTRLvAA", "eCTRLvM" })
                {
                    for (int i = 0; i < skin.sharedMesh.blendShapeCount; i++)
                        skin.SetBlendShapeWeight(i, skin.sharedMesh.GetBlendShapeName(i).EndsWith("__" + expression, StringComparison.Ordinal) ? 100f : 0f);
                    RenderCharacter(go, actor + "_face_" + expression, false, true);
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
    }

    public static void RenderReview()
    {
        Directory.CreateDirectory(Root + "/Validation/Review");
        var clip = AssetDatabase.LoadAllAssetsAtPath("Assets/ImportedActors/AnimLibrary/Male@Run.fbx").OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal));
        foreach (string actor in new[] { "Marta", "Kshishtof" })
        foreach (string outfit in new[] { "base", "fur" })
        {
            var go = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Actors/" + actor + ".prefab"));
            var graph = PlayableGraph.Create("PeopleReview");
            try
            {
                var bones = go.GetComponent<BodyBones>();
                bones.Construct((ActorName)Enum.Parse(typeof(ActorName), actor), 123);
                var items = outfit == "base"
                    ? new[] { "PrimalBriefs", "PrimalSkirt", "PrimalArmWraps", "PrimalWrapBoots", "PrimalHeadband", "PrimalGathererPack" }
                    : new[] { "PrimalBriefs", "PrimalDress", "PrimalCollar", "PrimalWrapBoots", "PrimalHunterPack" };
                foreach (var id in items)
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Wear/" + actor + "_" + id + ".prefab");
                    bones.Equip("review_" + id, prefab.GetComponent<Wear>(), null);
                }
                if (actor == "Marta")
                {
                    if (outfit == "base") bones.Equip("review_top", AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Wear/Marta_PrimalTop.prefab").GetComponent<Wear>(), null);
                    bones.SetHair(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Hair/LoonaHair.prefab").GetComponent<Wear>());
                }
                var animator = go.GetComponent<Animator>();
                animator.runtimeAnimatorController = null;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Rebind();
                var playable = AnimationClipPlayable.Create(graph, clip);
                AnimationPlayableOutput.Create(graph, "run", animator).SetSourcePlayable(playable);
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                graph.Play();
                playable.SetTime(clip.length * .2);
                graph.Evaluate(0);
                RenderCharacter(go, actor + "_" + outfit + "_front", false);
                RenderCharacter(go, actor + "_" + outfit + "_back", true);
            }
            finally { if (graph.IsValid()) graph.Destroy(); UnityEngine.Object.DestroyImmediate(go); }
        }
    }

    private static void RenderCharacter(GameObject go, string name, bool back, bool face = false)
    {
        bool oldAsync = ShaderUtil.allowAsyncCompilation;
        ShaderUtil.allowAsyncCompilation = false;
        var preview = new PreviewRenderUtility();
        var baked = new List<(Mesh mesh, Matrix4x4 matrix, Material[] materials)>();
        try
        {
            var bounds = new Bounds(); bool first = true;
            foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (!r.enabled || r.name.EndsWith("_LOD1", StringComparison.Ordinal)) continue;
                var mesh = new Mesh(); r.BakeMesh(mesh, true);
                baked.Add((mesh, r.transform.localToWorldMatrix, r.sharedMaterials));
                foreach (var p in mesh.vertices)
                {
                    var world = r.transform.TransformPoint(p);
                    if (first) { bounds = new Bounds(world, Vector3.zero); first = false; }
                    else bounds.Encapsulate(world);
                }
            }
            if (face)
            {
                var head = go.GetComponentsInChildren<Transform>(true).First(t => t.name == "head");
                bounds = new Bounds(head.position + Vector3.up * .075f, new Vector3(.3f, .34f, .25f));
            }
            var camera = preview.camera;
            camera.fieldOfView = 30f;
            float distance = bounds.extents.magnitude / Mathf.Sin(15f * Mathf.Deg2Rad) * 1.05f;
            camera.transform.position = bounds.center + new Vector3(back ? -.25f : .25f, .07f, back ? -1f : 1f).normalized * distance;
            camera.transform.LookAt(bounds.center);
            camera.nearClipPlane = .01f; camera.farClipPlane = distance * 4f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.13f, .15f, .17f);
            preview.lights[0].intensity = 1.15f;
            preview.lights[0].transform.rotation = Quaternion.Euler(35f, back ? 0f : 180f, 0);
            preview.lights[1].intensity = .55f;
            preview.lights[1].transform.rotation = Quaternion.Euler(-15f, back ? 160f : -30f, 0);
            // Warm up URP's material bindings, then submit a fresh draw queue
            // for readback (Camera.Render consumes the previous queue).
            for (int pass = 0; pass < 2; pass++)
            {
                preview.BeginStaticPreview(new Rect(0, 0, 768, 768));
                foreach (var part in baked)
                    for (int i = 0; i < part.mesh.subMeshCount; i++) preview.DrawMesh(part.mesh, part.matrix, part.materials[i], i);
                camera.Render();
                var image = preview.EndStaticPreview();
                if (pass == 1) File.WriteAllBytes(Root + "/Validation/Review/" + name + ".png", image.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(image);
            }
        }
        finally
        {
            foreach (var part in baked) UnityEngine.Object.DestroyImmediate(part.mesh);
            preview.Cleanup();
            ShaderUtil.allowAsyncCompilation = oldAsync;
        }
    }

    private static Dictionary<SkinnedMeshRenderer, Vector3[]> CaptureVertices(GameObject go)
    {
        var result = new Dictionary<SkinnedMeshRenderer, Vector3[]>();
        foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var baked = new Mesh();
            r.BakeMesh(baked, true);
            result[r] = baked.vertices.Select(r.transform.TransformPoint).ToArray();
            UnityEngine.Object.DestroyImmediate(baked);
        }
        return result;
    }

    public static void PreparePrefabs()
    {
        var manifest = JObject.Parse(File.ReadAllText(Root + "/Source/source-manifest.json"));
        var wardrobe = JArray.Parse(File.ReadAllText(Root + "/Source/wardrobe-authoring.json"));
        Directory.CreateDirectory(Root + "/Materials");
        Directory.CreateDirectory(Root + "/Textures");
        Directory.CreateDirectory(Root + "/Prefabs/Actors");
        Directory.CreateDirectory(Root + "/Prefabs/Wear");
        Directory.CreateDirectory(Root + "/Prefabs/Hair");
        AssetDatabase.Refresh();
        var hairMaterials = new HashSet<string>(manifest["models"].Where(m => (string)m["kind"] == "hair")
            .SelectMany(m => m["meshes"]).SelectMany(m => m["materials"].Values<string>()));
        var materials = new Dictionary<string, Material>();
        var variants = new JObject();
        foreach (var property in manifest["materials"].Children<JProperty>())
        {
            var row = property.Value;
            var names = (row["variants"] ?? new JArray("Base")).Values<string>().ToArray();
            var paths = new JObject();
            foreach (var variant in names)
            {
                var key = property.Name + "__" + variant;
                var filename = string.Concat(key.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_'));
                var path = Root + "/Materials/" + filename + ".mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                    AssetDatabase.CreateAsset(mat, path);
                }
                mat.name = (string)row["sourceSlot"];
                var images = row["images"] ?? new JArray();
                var image = images.FirstOrDefault(i => (string)i["node"] == variant)
                    ?? images.FirstOrDefault(i => (i["links"] ?? new JArray()).Any(l => (string)l["toSocket"] == "Base Color"));
                var normal = images.FirstOrDefault(i => (i["links"] ?? new JArray()).Any(l => (string)l["toSocket"] == "Color" && ((string)l["toNode"] ?? "").Contains("Normal")));
                mat.SetTexture("_BaseMap", ResolveBaseTexture(row, image));
                var rgba = row["baseColor"].Values<float>().ToArray();
                mat.SetColor("_BaseColor", image == null ? new Color(rgba[0], rgba[1], rgba[2], rgba[3]) : Color.white);
                mat.SetFloat("_Smoothness", 1f - (float)row["roughness"]);
                mat.SetFloat("_Metallic", (float)row["metallic"]);
                mat.SetFloat("_Cull", (bool)row["doubleSided"] ? 0f : 2f);
                bool clip = (string)row["alphaMode"] == "CLIP";
                mat.SetFloat("_AlphaClip", clip ? 1f : 0f);
                mat.SetFloat("_Cutoff", (float)row["alphaCutoff"]);
                if (clip) mat.EnableKeyword("_ALPHATEST_ON"); else mat.DisableKeyword("_ALPHATEST_ON");
                mat.renderQueue = clip ? 2450 : 2000;
                mat.SetOverrideTag("RenderType", clip ? "TransparentCutout" : "Opaque");
                if (normal != null)
                {
                    mat.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/Source/" + (string)normal["image"]));
                    mat.SetFloat("_BumpScale", .6f);
                    mat.EnableKeyword("_NORMALMAP");
                }
                if (hairMaterials.Contains(property.Name)) ApplyLowPolyHairMaterial(mat);
                EditorUtility.SetDirty(mat);
                if (variant == "Base") materials[property.Name] = mat;
                paths[variant] = path;
            }
            variants[property.Name] = paths;
        }
        var prefabs = new JArray();
        foreach (var row in manifest["models"])
        {
            string kind = (string)row["kind"], id = (string)row["id"], actor = (string)row["actor"];
            if (kind == "hair" && id.EndsWith("_LOD1", StringComparison.Ordinal)) continue;
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Source/" + (string)row["path"]);
            var go = UnityEngine.Object.Instantiate(model);
            go.name = kind == "hair" ? id.Replace("_LOD0", "") : kind == "actor" ? id : actor + "_" + id;
            try
            {
                AssignMaterials(go, row, materials);
                if (kind == "hair") AddHairLod(go, row, manifest, materials);
                var hip = go.GetComponentsInChildren<Transform>(true).First(t => t.name == "hip");
                if (kind == "actor")
                {
                    AssignNonSkinMaterials(go, actor);
                    ConfigureActorAnimator(go, actor);
                    ConfigureActorIK(go);
                    ConfigureAppearance(go, actor);
                    var bones = go.AddComponent<BodyBones>();
                    var wearRoot = new GameObject("Wear");
                    wearRoot.transform.SetParent(go.transform, false);
                    var so = new SerializedObject(bones);
                    so.FindProperty("hip").objectReferenceValue = hip;
                    so.FindProperty("wearTransform").objectReferenceValue = wearRoot.transform;
                    var genitals = go.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name.EndsWith("_Genitals", StringComparison.Ordinal));
                    so.FindProperty("genitals").objectReferenceValue = genitals == null ? null : genitals.gameObject;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    if (!ActorBodyResolver.TryResolve(go, out _, out var error)) throw new InvalidOperationException(error);
                }
                else
                {
                    foreach (var animator in go.GetComponentsInChildren<Animator>(true)) UnityEngine.Object.DestroyImmediate(animator);
                    var wear = go.AddComponent<Wear>();
                    var so = new SerializedObject(wear);
                    so.FindProperty("gender").intValue = (int)(actor == "Kshishtof" ? VisualGender.Male : VisualGender.Female);
                    var definition = wardrobe.FirstOrDefault(w => id.Contains((string)w["name"]));
                    if (definition != null && kind == "wear")
                    {
                        so.FindProperty("layer").intValue = (int)Enum.Parse(typeof(VisualWearLayer), (string)definition["layer"]);
                        SetSlots(so.FindProperty("slots"), definition["slots"].Values<string>());
                        SetSlots(so.FindProperty("noHideUnderwearSlots"), definition["noHide"].Values<string>());
                    }
                    else if (kind == "backpack")
                    {
                        so.FindProperty("layer").intValue = (int)VisualWearLayer.Bags;
                        SetSlots(so.FindProperty("slots"), new[] { "Chest" });
                    }
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                var folder = kind == "actor" ? "Actors" : kind == "hair" ? "Hair" : "Wear";
                var path = Root + "/Prefabs/" + folder + "/" + go.name + ".prefab";
                PrefabUtility.SaveAsPrefabAsset(go, path);
                prefabs.Add(new JObject { ["kind"] = kind, ["id"] = id, ["actor"] = actor, ["path"] = path });
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
        AssetDatabase.SaveAssets();
        File.WriteAllText(Root + "/Validation/preparation.json", new JObject {
            ["gameReady"] = false, ["bundleBuildAuthorized"] = false,
            ["prefabs"] = prefabs, ["materialVariants"] = variants }.ToString());
        Debug.Log("PEOPLE_PREFABS: " + prefabs.Count);
    }

    // Blender colour variants retain the base fur alpha, even when the colour image is opaque.
    private static Texture2D ResolveBaseTexture(JToken row, JToken image)
    {
        if (image == null) return null;
        string colourPath = Root + "/Source/" + (string)image["image"];
        var alpha = row["images"].FirstOrDefault(i => (i["links"] ?? new JArray()).Any(l => (string)l["output"] == "Alpha" && (string)l["toSocket"] == "Alpha"));
        if ((string)row["alphaMode"] != "CLIP" || alpha == null || (string)alpha["image"] == (string)image["image"])
            return AssetDatabase.LoadAssetAtPath<Texture2D>(colourPath);
        string alphaPath = Root + "/Source/" + (string)alpha["image"];
        string path = Root + "/Textures/fur_" + Hash128.Compute(colourPath + "|" + alphaPath) + ".png";
        if (!File.Exists(path))
        {
            var colour = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var mask = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Texture2D combined = null;
            try
            {
                if (!ImageConversion.LoadImage(colour, File.ReadAllBytes(colourPath)) || !ImageConversion.LoadImage(mask, File.ReadAllBytes(alphaPath))) throw new InvalidOperationException("Cannot decode fur maps");
                int width = Mathf.Min(1024, colour.width), height = Mathf.Min(1024, colour.height);
                combined = new Texture2D(width, height, TextureFormat.RGBA32, false);
                var pixels = new Color[width * height];
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    float u = (x + .5f) / width, v = (y + .5f) / height;
                    var c = colour.GetPixelBilinear(u, v); c.a = mask.GetPixelBilinear(u, v).a;
                    pixels[y * width + x] = c;
                }
                combined.SetPixels(pixels); combined.Apply();
                File.WriteAllBytes(path, combined.EncodeToPNG());
            }
            finally { UnityEngine.Object.DestroyImmediate(colour); UnityEngine.Object.DestroyImmediate(mask); if (combined != null) UnityEngine.Object.DestroyImmediate(combined); }
        }
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.maxTextureSize = 1024; importer.isReadable = false; importer.mipmapEnabled = true;
        importer.alphaSource = TextureImporterAlphaSource.FromInput;
        importer.alphaIsTransparency = true;
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    // Runs inside the FBX postprocessor, so geometry remains an imported FBX subasset.
    public static void SplitImportedBodySurfaces(GameObject go)
    {
        foreach (var part in go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh.blendShapeCount == 0))
        {
            var mesh = part.sharedMesh;
            var partCounts = mesh.GetBonesPerVertex();
            var partWeights = mesh.GetAllBoneWeights();
            var used = partWeights.Select(w => w.boneIndex).Distinct().OrderBy(i => i).ToArray();
            var remap = used.Select((old, index) => (old, index)).ToDictionary(p => p.old, p => p.index);
            var remapped = partWeights.ToArray();
            for (int i = 0; i < remapped.Length; i++) remapped[i].boneIndex = remap[remapped[i].boneIndex];
            using var newCounts = new Unity.Collections.NativeArray<byte>(partCounts.ToArray(), Unity.Collections.Allocator.Temp);
            using var newWeights = new Unity.Collections.NativeArray<BoneWeight1>(remapped, Unity.Collections.Allocator.Temp);
            var bindposes = mesh.bindposes;
            mesh.bindposes = used.Select(i => bindposes[i]).ToArray();
            mesh.SetBoneWeights(newCounts, newWeights);
            part.bones = used.Select(i => part.bones[i]).ToArray();
        }
        var skin = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.sharedMesh.blendShapeCount == 109);
        var source = skin.sharedMesh;
        var counts = source.GetBonesPerVertex();
        var weights = source.GetAllBoneWeights();
        var surface = new int[source.vertexCount];
        int offset = 0;
        for (int v = 0; v < surface.Length; v++)
        {
            float eye = 0, mouth = 0;
            for (int w = 0; w < counts[v]; w++)
            {
                var weight = weights[offset++];
                var name = skin.bones[weight.boneIndex].name;
                if (name == "lEye" || name == "rEye") eye += weight.weight;
                if (name == "upperTeeth" || name == "lowerTeeth" || name.StartsWith("tongue", StringComparison.Ordinal)) mouth += weight.weight;
            }
            surface[v] = eye > .95f ? 1 : mouth > .95f ? 2 : 0;
        }
        // Mesh owns these NativeArray views; do not dispose them.
        var groups = new[] { new List<int>(), new List<int>(), new List<int>() };
        var indices = source.triangles;
        for (int i = 0; i < indices.Length; i += 3)
        {
            int a = surface[indices[i]], b = surface[indices[i+1]], c = surface[indices[i+2]];
            int group = a == b && b == c ? a : 0;
            groups[group].Add(indices[i]); groups[group].Add(indices[i+1]); groups[group].Add(indices[i+2]);
        }
        if (groups[1].Count == 0 || groups[2].Count == 0) throw new InvalidOperationException(go.name + ": cannot isolate eyes/teeth from skin painting");
        source.subMeshCount = 3;
        for (int i = 0; i < 3; i++) source.SetTriangles(groups[i], i, false);
    }

    private static void AssignNonSkinMaterials(GameObject go, string actor)
    {
        var skin = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.sharedMesh.blendShapeCount == 109);
        if (skin.sharedMesh.subMeshCount != 3) throw new InvalidOperationException(actor + ": missing FBX skin/eye/mouth submeshes");
        var mats = new Material[3]; mats[0] = skin.sharedMaterial;
        for (int i = 1; i < 3; i++)
        {
            var matPath = Root + "/Materials/" + actor + (i == 1 ? "_Eyes.mat" : "_Teeth.mat");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null) { mat = new Material(mats[0]); AssetDatabase.CreateAsset(mat, matPath); }
            mat.name = actor + (i == 1 ? "_Eyes" : "_Teeth");
            EditorUtility.SetDirty(mat); mats[i] = mat;
        }
        skin.sharedMaterials = mats;
    }

    private static void ConfigureHumanoidImport(ModelImporter importer, string path)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        var mapping = JObject.Parse(File.ReadAllText(Root + "/Source/humanoid-bones.json"));
        if (Path.GetFileNameWithoutExtension(path) == "Marta")
        {
            foreach (var pair in mapping.Properties().Where(p => (string)p.Value == "LeftToes" || (string)p.Value == "RightToes").ToArray()) pair.Remove();
            mapping["lBigToe"] = "LeftToes"; mapping["rBigToe"] = "RightToes";
        }
        var present = new HashSet<string>(model.GetComponentsInChildren<Transform>(true).Select(t => t.name));
        importer.humanDescription = new HumanDescription
        {
            human = mapping.Properties().Where(p => present.Contains(p.Name)).Select(p => new HumanBone
            {
                boneName = p.Name, humanName = (string)p.Value,
                limit = new HumanLimit { useDefaultValues = true }
            }).ToArray(),
            skeleton = model.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone
            {
                name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale
            }).ToArray(),
            upperArmTwist = .5f, lowerArmTwist = .5f, upperLegTwist = .5f, lowerLegTwist = .5f,
            armStretch = .05f, legStretch = .05f, feetSpacing = 0f
        };
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
    }

    private static void ConfigureActorAnimator(GameObject go, string actor)
    {
        var animator = go.GetComponent<Animator>();
        if (animator == null || animator.avatar == null || !animator.avatar.isHuman || !animator.avatar.isValid)
            throw new InvalidOperationException(actor + ": invalid embedded FBX humanoid avatar");
        animator.applyRootMotion = false;
        animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/HexLive/UnityPresentation/Actors/HexNpcLocomotion.controller");
        if (animator.runtimeAnimatorController == null) throw new InvalidOperationException("Missing locomotion controller");
    }

    private static void ConfigureAppearance(GameObject go, string actor)
    {
        var skin = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.sharedMesh.blendShapeCount == 109);
        var ids = actor == "Marta" ? new[] { "Marta", "Molly", "Jana", "Jolly", "Masha", "Rita" } : new[] { "Kshishtof", "Tonny" };
        var tones = new[] { Color.white, new Color(.92f, .84f, .77f), new Color(.8f, .68f, .56f), new Color(1f, .94f, .89f) };
        var appearance = go.AddComponent<PeopleAppearance>();
        var so = new SerializedObject(appearance);
        so.FindProperty("bodyId").stringValue = actor;
        var entries = so.FindProperty("skins"); entries.arraySize = ids.Length;
        for (int i = 0; i < ids.Length; i++)
        {
            string path = Root + "/Materials/Skin_" + actor + "_" + ids[i] + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { mat = new Material(skin.sharedMaterial); AssetDatabase.CreateAsset(mat, path); }
            mat.name = skin.sharedMaterial.name;
            mat.SetColor("_BaseColor", actor == "Marta" ? tones[i % tones.Length] : Color.white);
            EditorUtility.SetDirty(mat);
            entries.GetArrayElementAtIndex(i).FindPropertyRelative("id").stringValue = ids[i];
            entries.GetArrayElementAtIndex(i).FindPropertyRelative("material").objectReferenceValue = mat;
        }
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void ConfigureActorIK(GameObject go)
    {
        var refs = new RootMotion.BipedReferences();
        if (!RootMotion.BipedReferences.AutoDetectReferences(ref refs, go.transform,
                new RootMotion.BipedReferences.AutoDetectParams(true, false)))
            throw new InvalidOperationException(go.name + ": cannot map interaction IK");
        var full = go.AddComponent<RootMotion.FinalIK.FullBodyBipedIK>();
        full.SetReferences(refs, null);
        string error = null;
        if (full.ReferencesError(ref error)) throw new InvalidOperationException(go.name + ": " + error);
        var all = go.GetComponentsInChildren<Transform>(true).ToDictionary(t => t.name);
        var look = go.AddComponent<RootMotion.FinalIK.LookAtIK>();
        if (!look.solver.SetChain(refs.spine, refs.head,
                new[] { all["lEye"], all["rEye"] }, go.transform))
            throw new InvalidOperationException(go.name + ": cannot map look-at IK");
        look.solver.IKPositionWeight = 0f;
    }

    private static void AddHairLod(GameObject go, JToken row, JObject manifest, Dictionary<string, Material> materials)
    {
        var lowRow = manifest["models"].First(m => (string)m["id"] == ((string)row["id"]).Replace("_LOD0", "_LOD1"));
        var lowModel = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Source/" + (string)lowRow["path"]));
        try
        {
            AssignMaterials(lowModel, lowRow, materials);
            var highRenderer = go.GetComponentInChildren<SkinnedMeshRenderer>();
            var lowRenderer = lowModel.GetComponentInChildren<SkinnedMeshRenderer>();
            var bones = go.GetComponentsInChildren<Transform>(true).ToDictionary(t => t.name, t => t);
            var part = UnityEngine.Object.Instantiate(lowRenderer.gameObject, highRenderer.transform.parent);
            part.transform.localPosition = highRenderer.transform.localPosition;
            part.transform.localRotation = highRenderer.transform.localRotation;
            part.transform.localScale = highRenderer.transform.localScale;
            var skin = part.GetComponent<SkinnedMeshRenderer>();
            skin.bones = lowRenderer.bones.Select(b => bones[b.name]).ToArray();
            skin.rootBone = bones[lowRenderer.rootBone.name];
            var lod = go.AddComponent<LODGroup>();
            lod.SetLODs(new[] { new LOD(.18f, new Renderer[] { highRenderer }), new LOD(.015f, new Renderer[] { skin }) });
            lod.RecalculateBounds();
        }
        finally { UnityEngine.Object.DestroyImmediate(lowModel); }
    }

    private static void SetSlots(SerializedProperty p, IEnumerable<string> slots)
    {
        var values = slots.ToArray();
        p.arraySize = values.Length;
        for (int i = 0; i < values.Length; i++) p.GetArrayElementAtIndex(i).intValue = (int)Enum.Parse(typeof(VisualWearSlot), values[i]);
    }

    private static void AssignMaterials(GameObject go, JToken row, Dictionary<string, Material> materials)
    {
        foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var mesh = row["meshes"].First(m => (string)m["name"] == r.name);
            r.sharedMaterials = mesh["materials"].Values<string>().Select(key => materials[key]).ToArray();
            r.updateWhenOffscreen = true;
        }
    }

    public static void Audit()
    {
        var manifest = JObject.Parse(File.ReadAllText(Root + "/Source/source-manifest.json"));
        var errors = new List<string>();
        var models = new JArray();
        foreach (var m in manifest["models"])
        {
            var path = Root + "/Source/" + (string)m["path"];
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) { errors.Add("Missing " + path); continue; }
            var renderers = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var rows = new JArray();
            foreach (var r in renderers)
            {
                var mesh = r.sharedMesh;
                var shapes = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToArray();
                var source = m["meshes"].FirstOrDefault(s => (string)s["name"] == r.name);
                if (source == null) errors.Add(path + ": unexpected renderer " + r.name);
                else
                {
                    var wanted = source["blendShapes"].Values<string>().ToArray();
                    if (!wanted.SequenceEqual(shapes)) errors.Add(path + ": morph name/order mismatch " + r.name);
                    if ((int)source["triangles"] != mesh.triangles.Length / 3) errors.Add(path + ": triangle mismatch " + r.name);
                }
                if (r.bones.Any(b => b == null)) errors.Add(path + ": null bones");
                var counts = mesh.GetBonesPerVertex();
                int maxWeights = counts.Length > 0 ? counts.Max(x => (int)x) : 0;
                // Mesh owns this NativeArray view.
                rows.Add(new JObject {
                    ["name"] = r.name, ["vertices"] = mesh.vertexCount,
                    ["triangles"] = mesh.triangles.Length / 3, ["subMeshes"] = mesh.subMeshCount,
                    ["blendShapes"] = new JArray(shapes), ["boneCount"] = r.bones.Length,
                    ["maxBoneWeights"] = maxWeights, ["runtimeMeshBytes"] = Profiler.GetRuntimeMemorySizeLong(mesh),
                    ["localBounds"] = mesh.bounds.ToString(), ["worldBounds"] = r.bounds.ToString(),
                    ["lossyScale"] = r.transform.lossyScale.ToString(),
                    ["bones"] = new JArray(r.bones.Select(b => b == null ? "<null>" : b.name)) });
            }
            if (renderers.Length != m["meshes"].Count()) errors.Add(path + ": renderer count mismatch");
            var names = new HashSet<string>(go.GetComponentsInChildren<Transform>(true).Select(t => t.name));
            foreach (string b in m["bones"].Values<string>()) if (!names.Contains(b)) errors.Add(path + ": missing bone " + b);
            models.Add(new JObject { ["path"] = path, ["meshes"] = rows });
        }
        Directory.CreateDirectory(Root + "/Validation");
        File.WriteAllText(Root + "/Validation/unity-import.json", new JObject {
            ["schemaVersion"] = 1, ["unityVersion"] = Application.unityVersion,
            ["target"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
            ["graphicsDevice"] = SystemInfo.graphicsDeviceType.ToString(),
            ["memoryMeasureScope"] = "Imported Editor Mesh, not total browser heap/GPU memory",
            ["importPassed"] = errors.Count == 0, ["gameReady"] = false,
            ["bundleBuildAuthorized"] = false, ["models"] = models,
            ["errors"] = new JArray(errors) }.ToString());
        AssetDatabase.Refresh();
        Debug.Log("PEOPLE_IMPORT_AUDIT: models=" + models.Count + " errors=" + errors.Count);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
    }
}
}
#endif
