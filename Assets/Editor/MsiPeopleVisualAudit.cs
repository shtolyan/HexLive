#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// Read-only capture through the same BodyBones/Wear binding as the prepared people.
// Native Unity CLI: -batchmode -executeMethod MsiPeopleVisualAudit.Capture -quit
public static class MsiPeopleVisualAudit
{
    private const string Root = "Assets/HexLiveContent/People";
    private const string Output = "Build/MSIVisualAudit/before";

    public static void Capture()
    {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            throw new InvalidOperationException("Visual audit requires a graphics device; omit -nographics.");
        Directory.CreateDirectory(Output);
        CaptureNormalComparison();
        var rows = new JArray();
        var manifest = JObject.Parse(File.ReadAllText(Root + "/Source/source-manifest.json"));
        foreach (var row in manifest["models"])
        {
            if ((string)row["kind"] != "hair" || !((string)row["id"]).EndsWith("_LOD0", StringComparison.Ordinal)) continue;
            var name = ((string)row["id"]).Replace("_LOD0", "");
            var actorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Actors/Marta.prefab");
            var hairPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Hair/" + name + ".prefab");
            if (actorPrefab == null || hairPrefab == null) throw new InvalidOperationException("Missing prefab: " + name);
            var actor = Object.Instantiate(actorPrefab);
            try
            {
                var bones = actor.GetComponent<BodyBones>();
                bones.Construct(ActorName.Marta, 123);
                bones.SetHair(hairPrefab.GetComponent<Wear>());
                var head = actor.GetComponentsInChildren<Transform>(true).First(t => t.name == "head");
                var bounds = new Bounds(head.position + Vector3.up * .06f, new Vector3(.5f, .65f, .5f));
                foreach (var lod in actor.GetComponentsInChildren<LODGroup>(true)) lod.enabled = false;
                for (var level = 0; level <= 1; level++)
                {
                    var materials = new JArray();
                    foreach (var renderer in actor.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        var isHair = renderer.sharedMesh != null && renderer.sharedMesh.blendShapeCount == 0;
                        if (isHair && renderer.name.Contains("_LOD"))
                            renderer.enabled = renderer.name.EndsWith("_LOD" + level, StringComparison.Ordinal);
                        if (!renderer.enabled) continue;
                        foreach (var mat in renderer.sharedMaterials)
                            if (mat != null) materials.Add(new JObject {
                                ["renderer"] = renderer.name, ["material"] = AssetDatabase.GetAssetPath(mat),
                                ["shader"] = mat.shader.name,
                                ["baseMap"] = mat.HasProperty("_BaseMap") ? AssetDatabase.GetAssetPath(mat.GetTexture("_BaseMap")) : "",
                                ["normalMap"] = mat.HasProperty("_BumpMap") ? AssetDatabase.GetAssetPath(mat.GetTexture("_BumpMap")) : "",
                                ["cull"] = mat.HasProperty("_Cull") ? mat.GetFloat("_Cull") : -1,
                                ["alphaClip"] = mat.HasProperty("_AlphaClip") ? mat.GetFloat("_AlphaClip") : -1
                            });
                    }
                    foreach (var angle in new[] { 0f, 90f, 180f })
                    {
                        var file = name + "_LOD" + level + "_" + angle + ".png";
                        Render(actor, bounds, angle, Path.Combine(Output, file));
                        rows.Add(new JObject { ["hair"] = name, ["lod"] = level, ["angle"] = angle, ["image"] = file });
                    }
                    File.WriteAllText(Path.Combine(Output, name + "_LOD" + level + ".json"), materials.ToString());
                }
            }
            finally { Object.DestroyImmediate(actor); }
        }
        File.WriteAllText(Path.Combine(Output, "capture.json"), new JObject {
            ["unity"] = Application.unityVersion, ["graphics"] = SystemInfo.graphicsDeviceType.ToString(),
            ["renders"] = rows, ["liveWebGLVerified"] = false
        }.ToString());
        Debug.Log("[MSIVisualAudit] Captured " + rows.Count + " hair views in " + Output);
    }

    private static void CaptureNormalComparison()
    {
        var checkedMaterials = new JArray();
        foreach (var path in Directory.GetFiles(Root + "/Materials", "*.mat"))
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path.Replace('\\', '/'));
            if (material == null || !material.HasProperty("_BumpMap") || material.GetTexture("_BumpMap") == null) continue;
            var texturePath = AssetDatabase.GetAssetPath(material.GetTexture("_BumpMap"));
            var importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
            if (importer == null || importer.textureType != TextureImporterType.NormalMap ||
                !material.IsKeywordEnabled("_NORMALMAP") || !Mathf.Approximately(material.GetFloat("_BumpScale"), 1f))
                throw new InvalidOperationException("Normal map contract failed: " + path);
            checkedMaterials.Add(new JObject { ["path"] = path, ["normalTexture"] = texturePath, ["scale"] = material.GetFloat("_BumpScale") });
        }
        if (checkedMaterials.Count != 28) throw new InvalidOperationException("Expected 28 authored normal materials, got " + checkedMaterials.Count);
        foreach (var name in new[] { "Marta", "Kshishtof" })
        {
            var actor = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Actors/" + name + ".prefab"));
            var copies = new List<Material>();
            try
            {
                var bones = actor.GetComponent<BodyBones>();
                bones.Construct((ActorName)Enum.Parse(typeof(ActorName), name), 123);
                foreach (var item in new[] { "PrimalBriefs", "PrimalDress", "PrimalCollar" })
                    bones.Equip("audit_" + item, AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Wear/" + name + "_" + item + ".prefab").GetComponent<Wear>(), null);
                foreach (var renderer in actor.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var materials = renderer.sharedMaterials;
                    for (var i = 0; i < materials.Length; i++)
                    {
                        if (materials[i] == null) continue;
                        materials[i] = new Material(materials[i]);
                        copies.Add(materials[i]);
                    }
                    renderer.sharedMaterials = materials;
                }
                var bounds = new Bounds(actor.transform.position + Vector3.up * 1.1f, new Vector3(.75f, 1.15f, .6f));
                foreach (var strength in new[] { .6f, 1f })
                {
                    foreach (var material in copies)
                        if (material.HasProperty("_BumpMap") && material.GetTexture("_BumpMap") != null) material.SetFloat("_BumpScale", strength);
                    Render(actor, bounds, 45, Path.Combine(Output, "normal-" + name + (strength < 1 ? "-before.png" : "-restored.png")));
                }
            }
            finally
            {
                Object.DestroyImmediate(actor);
                foreach (var material in copies) Object.DestroyImmediate(material);
            }
        }
        File.WriteAllText(Path.Combine(Output, "normal-materials.json"), checkedMaterials.ToString());
    }

    public static void CaptureHairPrototype() => CaptureHairComparison(false);
    public static void CaptureHairReference() => CaptureHairComparison(true);

    private static void CaptureHairComparison(bool legacy)
    {
        const string output = "Build/MSIVisualAudit/HairProjectionWorld";
        var mask = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!mask.LoadImage(File.ReadAllBytes(output + "/AdellHair_CutoutPrototype.png")))
            throw new InvalidOperationException("Prototype mask could not be decoded.");
        var actor = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Actors/Marta.prefab"));
        var materials = new List<Material>();
        try
        {
            var bones = actor.GetComponent<BodyBones>();
            bones.Construct(ActorName.Marta, 123);
            var hairPath = legacy ? "Assets/ImportedActors/Hair/AdellHair/AdellHair.prefab" : Root + "/Prefabs/Hair/AdellHair.prefab";
            bones.SetHair(AssetDatabase.LoadAssetAtPath<GameObject>(hairPath).GetComponent<Wear>());
            foreach (var lod in actor.GetComponentsInChildren<LODGroup>(true)) lod.enabled = false;
            foreach (var renderer in actor.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh == null || renderer.sharedMesh.blendShapeCount != 0) continue;
                renderer.enabled = legacy || renderer.name.EndsWith("_LOD0", StringComparison.Ordinal);
                var clones = renderer.sharedMaterials.Select(m => new Material(m)).ToArray();
                foreach (var material in clones)
                {
                    materials.Add(material);
                    if (!legacy) material.SetTexture("_BaseMap", mask);
                    material.SetFloat("_AlphaClip", 1);
                    material.SetFloat("_Cutoff", .42f);
                    material.EnableKeyword("_ALPHATEST_ON");
                    material.SetOverrideTag("RenderType", "TransparentCutout");
                    material.renderQueue = 2450;
                }
                renderer.sharedMaterials = clones;
            }
            var head = actor.GetComponentsInChildren<Transform>(true).First(t => t.name == "head");
            var bounds = new Bounds(head.position + Vector3.up * .06f, new Vector3(.5f, .65f, .5f));
            foreach (var angle in new[] { 0f, 90f, 180f })
                Render(actor, bounds, angle, output + "/AdellHair-" + (legacy ? "source-reference-" : "cutout-") + angle + ".png");
            Debug.Log("[MSIVisualAudit] Prototype cutout captured; no saved material modified.");
        }
        finally
        {
            Object.DestroyImmediate(actor);
            foreach (var material in materials) Object.DestroyImmediate(material);
            Object.DestroyImmediate(mask);
        }
    }

    public static void CaptureBandagePrototype()
    {
        const string output = "Build/MSIVisualAudit/BandagePrototype";
        var overlay = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!overlay.LoadImage(File.ReadAllBytes(output + "/Marta_Torso.png")))
            throw new InvalidOperationException("Bandage prototype could not be decoded.");
        var actor = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Actors/Marta.prefab"));
        Material material = null;
        RenderTexture painted = null;
        var previousRt = RenderTexture.active;
        try
        {
            actor.GetComponent<BodyBones>().Construct(ActorName.Marta, 123);
            var skin = actor.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.sharedMesh.blendShapeCount == 109);
            material = new Material(skin.sharedMaterial);
            var original = material.GetTexture("_BaseMap");
            painted = new RenderTexture(1024, 1024, 0, RenderTextureFormat.ARGB32);
            painted.Create();
            skin.sharedMaterial = material;
            var legacy = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Resources/HexLive/Decals/bandage_wrap_Torso.png");
            foreach (var useNewAtlas in new[] { false, true })
            {
                Graphics.Blit(original, painted);
                RenderTexture.active = painted;
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, 1, 1, 0);
                var rect = useNewAtlas ? new Rect(0, 0, 1, 1) : new Rect(.5f - .71289f / 2, 1 - .53467f - .12402f / 2, .71289f, .12402f);
                Graphics.DrawTexture(rect, useNewAtlas ? overlay : legacy);
                GL.PopMatrix();
                RenderTexture.active = previousRt;
                material.SetTexture("_BaseMap", painted);
                foreach (var angle in new[] { 0f, 90f, 180f })
                    Render(actor, new Bounds(actor.transform.position + Vector3.up * 1.15f, new Vector3(.75f, 1.2f, .6f)), angle,
                        output + "/Marta-Torso-" + (useNewAtlas ? "projected-" : "legacy-rect-") + angle + ".png");
            }
            Debug.Log("[MSIVisualAudit] Bandage atlas prototype captured; normals and runtime integration remain unvalidated.");
        }
        finally
        {
            RenderTexture.active = previousRt;
            Object.DestroyImmediate(actor);
            if (material != null) Object.DestroyImmediate(material);
            if (painted != null) { painted.Release(); Object.DestroyImmediate(painted); }
            Object.DestroyImmediate(overlay);
        }
    }

    public static void InstallAndCaptureBandages()
    {
        InstallBandageAtlases();
        CaptureBandageRuntime();
    }

    public static void InstallBandageAtlases()
    {
        const string source = "Build/MSIVisualAudit/BandageFinal";
        const string destination = "Assets/Resources/HexLive/Decals/BodyAtlases";
        var zones = new[] { "Torso", "Pelvis", "ArmL", "ArmR", "LegL", "LegR" };
        foreach (var actor in new[] { "Marta", "Kshishtof" })
            foreach (var zone in zones)
                foreach (var suffix in new[] { "", "_n" })
                    if (!File.Exists(source + "/" + actor + "_" + zone + suffix + ".png"))
                        throw new InvalidOperationException("Incomplete bandage bake: " + actor + " " + zone + suffix);
        Directory.CreateDirectory(destination);
        foreach (var actor in new[] { "Marta", "Kshishtof" })
        {
            var entries = new List<SkinBandageAtlas.ZoneWrap>();
            foreach (var zone in zones)
            {
                var textures = new List<Texture2D>();
                foreach (var suffix in new[] { "", "_n" })
                {
                    var path = destination + "/" + actor + "_" + zone + suffix + ".png";
                    File.Copy(source + "/" + actor + "_" + zone + suffix + ".png", path, true);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                    var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                    importer.textureType = TextureImporterType.Default;
                    importer.sRGBTexture = suffix.Length == 0;
                    importer.alphaSource = TextureImporterAlphaSource.FromInput;
                    importer.alphaIsTransparency = suffix.Length == 0;
                    importer.mipmapEnabled = true;
                    importer.wrapMode = TextureWrapMode.Clamp;
                    importer.filterMode = FilterMode.Bilinear;
                    // Match the painter's 2048 color / 1024 normal targets;
                    // larger normal imports cannot add runtime detail.
                    importer.maxTextureSize = suffix.Length == 0 ? 2048 : 1024;
                    importer.textureCompression = TextureImporterCompression.CompressedHQ;
                    importer.SaveAndReimport();
                    textures.Add(AssetDatabase.LoadAssetAtPath<Texture2D>(path));
                }
                entries.Add(new SkinBandageAtlas.ZoneWrap { Zone = zone, Albedo = textures[0], Normal = textures[1] });
            }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Actors/" + actor + ".prefab");
            var mesh = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r.sharedMesh != null).OrderByDescending(r => r.sharedMesh.blendShapeCount).First().sharedMesh;
            var assetPath = destination + "/" + mesh.name + ".asset";
            var atlas = AssetDatabase.LoadAssetAtPath<SkinBandageAtlas>(assetPath);
            if (atlas == null) { atlas = ScriptableObject.CreateInstance<SkinBandageAtlas>(); AssetDatabase.CreateAsset(atlas, assetPath); }
            atlas.MeshName = mesh.name; atlas.VertexCount = mesh.vertexCount;
            atlas.SourceBlendSha256 = "568b871a7c839e140de973da1e670fe1ab7e00cceabab8083e0267b6bd7b5661";
            atlas.Zones = entries.ToArray(); EditorUtility.SetDirty(atlas);
            Debug.Log("[MSIVisualAudit] Installed atlas " + mesh.name + " vertices " + mesh.vertexCount);
        }
        AssetDatabase.SaveAssets();
    }

    public static void BeginBandageRuntimePlayMode()
    {
        SessionState.SetBool("MSI.BandageRuntimeCapture", true);
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene);
        ResumeBandageRuntimeCapture();
        EditorApplication.EnterPlaymode();
    }

    [InitializeOnLoadMethod]
    private static void ResumeBandageRuntimeCapture()
    {
        if (!SessionState.GetBool("MSI.BandageRuntimeCapture", false)) return;
        EditorApplication.playModeStateChanged -= OnBandagePlayMode;
        EditorApplication.playModeStateChanged += OnBandagePlayMode;
    }

    private static void OnBandagePlayMode(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool("MSI.BandageRuntimeCapture", false)) return;
        SessionState.EraseBool("MSI.BandageRuntimeCapture");
        EditorApplication.playModeStateChanged -= OnBandagePlayMode;
        try { CaptureBandageRuntime(); EditorApplication.Exit(0); }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }

    public static void CaptureBandageRuntime()
    {
        const string output = "Build/MSIVisualAudit/BandageRuntime";
        Directory.CreateDirectory(output);
        var caches = new List<(System.Collections.IDictionary cache, Dictionary<object, object> saved)>();
        try
        {
            // Same local-source fixture as PeoplePaintRuntimeTests: substitute
            // only transport, then call the real Construct/Sync/PaintCycle path.
            foreach (var type in new[] { typeof(PaintPointMap), typeof(SkinPositionMapSet) })
            {
                var cache = (System.Collections.IDictionary)type.GetField("Cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
                var saved = new Dictionary<object, object>();
                foreach (System.Collections.DictionaryEntry entry in cache) saved.Add(entry.Key, entry.Value);
                caches.Add((cache, saved)); cache.Clear();
                foreach (var guid in AssetDatabase.FindAssets("t:" + type.Name, new[] { Root + "/PaintMaps" }))
                {
                    var asset = AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(guid), type);
                    cache[asset.name] = asset;
                }
            }
            foreach (var name in new[] { ActorName.Marta, ActorName.Kshishtof })
            {
                var actor = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Actors/" + name + ".prefab"));
                try
                {
                    actor.GetComponent<Animator>().enabled = false;
                    var bones = actor.GetComponent<BodyBones>(); bones.Construct(name, 433);
                    if (!ActorBodyResolver.TryResolve(actor, out var body, out var error)) throw new InvalidOperationException(error);
                    var atlas = SkinBandageAtlas.Load(body.sharedMesh);
                    if (atlas == null || atlas.Zones.Length != 6) throw new InvalidOperationException("Missing body bandage atlas " + name);
                    var otherTextures = body.sharedMaterials.Skip(1).Select(m => m.GetTexture("_BaseMap")).ToArray();
                    var painter = actor.AddComponent<SkinTexturePainter>();
                    painter.Construct(body, new[] { 0 }, bones, actor.transform, 433, name.ToString());
                    foreach (var zone in new[] { "Torso", "Pelvis", "ArmL", "ArmR", "LegL", "LegR" })
                    {
                        painter.Sync(new List<(string zone, int seed, float heal)>(), new HashSet<string> { zone });
                        for (var pass = 0; pass < 4; pass++) painter.PaintCycle();
                        if (!painter.SlotHasAlbedoPaint(0) || !(body.sharedMaterials[0].GetTexture("_BumpMap") is RenderTexture))
                            throw new InvalidOperationException("Runtime color/normal did not paint " + name + " " + zone);
                        for (var slot = 1; slot < body.sharedMaterials.Length; slot++)
                            if (body.sharedMaterials[slot].GetTexture("_BaseMap") != otherTextures[slot - 1])
                                throw new InvalidOperationException("Bandage modified non-skin slot " + slot);
                        var bounds = new Bounds(actor.transform.position + Vector3.up * .95f, new Vector3(1.4f, 1.9f, .6f));
                        foreach (var angle in new[] { 0f, 90f, 180f })
                            Render(actor, bounds, angle, output + "/" + name + "-" + zone + "-" + angle + ".png");
                        var pose = actor.GetComponentsInChildren<Transform>(true)
                            .Where(t => t.name == "lShin" || t.name == "rShin" || t.name == "lForearmBend" || t.name == "rForearmBend").ToArray();
                        if (pose.Length != 4) throw new InvalidOperationException("Missing pose bones " + name);
                        var rotations = pose.Select(t => t.localRotation).ToArray();
                        try
                        {
                            for (var i = 0; i < pose.Length; i++) pose[i].localRotation = rotations[i] * Quaternion.Euler(35, 0, 0);
                            Render(actor, bounds, 0, output + "/" + name + "-" + zone + "-posed.png");
                        }
                        finally { for (var i = 0; i < pose.Length; i++) pose[i].localRotation = rotations[i]; }
                        Debug.Log("[MSIVisualAudit] Runtime bandage passed " + name + " " + zone);
                    }
                }
                finally { Object.DestroyImmediate(actor); }
            }
        }
        finally
        {
            foreach (var (cache, saved) in caches)
            {
                cache.Clear(); foreach (var entry in saved) cache.Add(entry.Key, entry.Value);
            }
        }
        Debug.Log("[MSIVisualAudit] 12 runtime bandages captured in 48 views; published-game QA remains.");
    }

    public static void CaptureLegacyBandageReference()
    {
        const string output = "Build/MSIVisualAudit/BandageMatrix";
        foreach (var name in new[] { "Marta", "Kshishtof" })
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(name == "Marta"
                ? "Assets/ImportedActors/Daz3D/Martanaked/Martanaked.fbx"
                : "Assets/ImportedActors/Daz3D/Kshishtof/Kshishtof.fbx");
            if (prefab == null) throw new InvalidOperationException("Missing reference actor " + name);
            var actor = Object.Instantiate(prefab);
            var mats = new List<Material>();
            RenderTexture painted = null;
            var previous = RenderTexture.active;
            try
            {
                var skin = actor.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).OrderByDescending(r => r.sharedMesh.vertexCount).First();
                var materials = skin.sharedMaterials;
                var slot = Array.FindIndex(materials, m => m != null && m.name.IndexOf("Torso", StringComparison.OrdinalIgnoreCase) >= 0);
                if (slot < 0) throw new InvalidOperationException("Reference torso missing: " + string.Join(",", materials.Select(m => m.name)));
                var material = new Material(materials[slot]); mats.Add(material); materials[slot] = material; skin.sharedMaterials = materials;
                painted = new RenderTexture(2048, 2048, 0, RenderTextureFormat.ARGB32) { wrapMode = TextureWrapMode.Repeat }; painted.Create();
                Graphics.Blit(material.GetTexture("_BaseMap"), painted); RenderTexture.active = painted;
                GL.PushMatrix(); GL.LoadPixelMatrix(0, 1, 1, 0);
                var wrap = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Resources/HexLive/Decals/bandage_wrap_Torso.png");
                Graphics.DrawTexture(new Rect(.5f-.71289f/2, 1-.53467f-.12402f/2, .71289f, .12402f), wrap);
                GL.PopMatrix(); RenderTexture.active = previous; material.SetTexture("_BaseMap", painted); material.DisableKeyword("_NORMALMAP");
                Debug.Log("[MSIVisualAudit] Legacy " + name + " body " + skin.sharedMesh.name + " bounds " + skin.bounds);
                foreach (var angle in new[] { 0f, 180f })
                    Render(actor, new Bounds(actor.transform.position + Vector3.up * (name == "Marta" ? 1.34f : 1.41f), new Vector3(.6f, .55f, .35f)), angle, output + "/" + name + "-legacy-torso-" + angle + ".png");
            }
            finally
            {
                RenderTexture.active = previous; Object.DestroyImmediate(actor);
                foreach (var mat in mats) Object.DestroyImmediate(mat);
                if (painted != null) { painted.Release(); Object.DestroyImmediate(painted); }
            }
        }
    }

    private static bool _bandageCloseup;
    public static void CaptureBandageCloseup()
    {
        _bandageCloseup = true;
        try { CaptureBandageMatrix(); }
        finally { _bandageCloseup = false; }
    }

    public static void CaptureBandageMatrix()
    {
        const string output = "Build/MSIVisualAudit/BandageMatrix";
        if (_bandageCloseup)
        {
            var source = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Resources/HexLive/Decals/bandage_wrap_Torso.png");
            var rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            Graphics.Blit(source, rt); RenderTexture.active = rt;
            var pixels = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            pixels.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0); pixels.Apply();
            File.WriteAllBytes(output + "/legacy-wrap-imported.png", pixels.EncodeToPNG());
            Debug.Log("[MSIVisualAudit] Imported wrap " + source.width + "x" + source.height + " " + source.format);
            Object.DestroyImmediate(pixels); RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt);
        }
        foreach (var name in new[] { ActorName.Marta, ActorName.Kshishtof })
        {
            var actor = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Actors/" + name + ".prefab"));
            Material material = null;
            RenderTexture painted = null;
            var previousRt = RenderTexture.active;
            try
            {
                actor.GetComponent<BodyBones>().Construct(name, 123);
                var skin = actor.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .OrderByDescending(r => r.sharedMesh.blendShapeCount).First();
                material = new Material(skin.sharedMaterial);
                var original = material.GetTexture("_BaseMap");
                painted = new RenderTexture(2048, 2048, 0, RenderTextureFormat.ARGB32);
                painted.Create(); skin.sharedMaterial = material;
                foreach (var zone in new[] { "Torso", "Pelvis", "ArmL", "ArmR", "LegL", "LegR" })
                {
                    if (_bandageCloseup && zone != "Torso") continue;
                    var bounds = _bandageCloseup
                        ? new Bounds(actor.transform.position + Vector3.up * (name == ActorName.Marta ? 1.34f : 1.41f), new Vector3(.6f, .55f, .35f))
                        : new Bounds(actor.transform.position + Vector3.up * .95f, new Vector3(1.4f, 1.9f, .6f));
                    var suffix = _bandageCloseup ? "-close" : "";
                    var overlay = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    var normal = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                    var normalRt = new RenderTexture(1024, 1024, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                    try
                    {
                        if (!overlay.LoadImage(File.ReadAllBytes(output + "/" + name + "_" + zone + ".png")))
                            throw new InvalidOperationException("Cannot decode " + name + " " + zone);
                        Graphics.Blit(original, painted); RenderTexture.active = painted;
                        GL.PushMatrix(); GL.LoadPixelMatrix(0, 1, 1, 0);
                        Graphics.DrawTexture(new Rect(0, 0, 1, 1), overlay);
                        GL.PopMatrix(); RenderTexture.active = previousRt;
                        material.SetTexture("_BaseMap", painted);
                        material.DisableKeyword("_NORMALMAP");
                        foreach (var angle in new[] { 0f, 90f, 180f })
                            Render(actor, bounds, angle,
                                output + "/" + name + "-" + zone + suffix + "-" + angle + ".png");
                        if (!normal.LoadImage(File.ReadAllBytes(output + "/" + name + "_" + zone + "_n.png")))
                            throw new InvalidOperationException("Cannot decode tangent normal " + name + " " + zone);
                        normalRt.Create(); RenderTexture.active = normalRt;
                        GL.Clear(false, true, new Color(.5f, .5f, 1f, 1f));
                        GL.PushMatrix(); GL.LoadPixelMatrix(0, 1, 1, 0);
                        Graphics.DrawTexture(new Rect(0, 0, 1, 1), normal, new Rect(0, 0, 1, 1), 0, 0, 0, 0, new Color(.5f, .5f, .5f, .175f));
                        GL.PopMatrix(); RenderTexture.active = previousRt;
                        if (_bandageCloseup)
                        {
                            RenderTexture.active = normalRt;
                            var readback = new Texture2D(1024, 1024, TextureFormat.RGBA32, false, true);
                            readback.ReadPixels(new Rect(0, 0, 1024, 1024), 0, 0); readback.Apply();
                            File.WriteAllBytes(output + "/" + name + "-normal-rt.png", readback.EncodeToPNG());
                            Debug.Log("[MSIVisualAudit] " + name + " normal RT minimum alpha " + readback.GetPixels().Min(c => c.a));
                            Object.DestroyImmediate(readback); RenderTexture.active = previousRt;
                        }
                        material.SetTexture("_BumpMap", normalRt); material.SetFloat("_BumpScale", 1f); material.EnableKeyword("_NORMALMAP");
                        foreach (var angle in new[] { 0f, 90f, 180f })
                            Render(actor, bounds, angle,
                                output + "/" + name + "-" + zone + suffix + "-normal-" + angle + ".png");
                    }
                    finally { Object.DestroyImmediate(overlay); Object.DestroyImmediate(normal); normalRt.Release(); Object.DestroyImmediate(normalRt); }
                }
            }
            finally
            {
                RenderTexture.active = previousRt; Object.DestroyImmediate(actor);
                if (material != null) Object.DestroyImmediate(material);
                if (painted != null) { painted.Release(); Object.DestroyImmediate(painted); }
            }
        }
        Debug.Log("[MSIVisualAudit] Captured 12 bandage projections in 72 native color/normal views; runtime integration still pending.");
    }

    private static void Render(GameObject actor, Bounds bounds, float angle, string path)
    {
        var previousAsync = ShaderUtil.allowAsyncCompilation;
        ShaderUtil.allowAsyncCompilation = false;
        var preview = new PreviewRenderUtility();
        var meshes = new List<(Mesh mesh, Matrix4x4 matrix, Material[] materials)>();
        try
        {
            foreach (var renderer in actor.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (!renderer.enabled) continue;
                var mesh = new Mesh();
                renderer.BakeMesh(mesh, true);
                meshes.Add((mesh, renderer.transform.localToWorldMatrix, renderer.sharedMaterials));
            }
            var camera = preview.camera;
            camera.fieldOfView = 30;
            var distance = bounds.extents.magnitude / Mathf.Sin(15 * Mathf.Deg2Rad);
            camera.transform.position = bounds.center + Quaternion.Euler(0, angle, 0) * new Vector3(0, .03f, distance);
            camera.transform.LookAt(bounds.center);
            camera.nearClipPlane = .01f;
            camera.farClipPlane = distance * 4;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.13f, .15f, .17f);
            preview.lights[0].intensity = 1.15f;
            preview.lights[0].transform.rotation = Quaternion.Euler(35, 180, 0);
            preview.lights[1].intensity = .55f;
            preview.lights[1].transform.rotation = Quaternion.Euler(-15, -30, 0);
            for (var pass = 0; pass < 2; pass++)
            {
                preview.BeginStaticPreview(new Rect(0, 0, 512, 512));
                foreach (var part in meshes)
                    for (var submesh = 0; submesh < part.mesh.subMeshCount; submesh++)
                        preview.DrawMesh(part.mesh, part.matrix, part.materials[submesh], submesh);
                camera.Render();
                var image = preview.EndStaticPreview();
                if (pass == 1) File.WriteAllBytes(path, image.EncodeToPNG());
                Object.DestroyImmediate(image);
            }
        }
        finally
        {
            foreach (var part in meshes) Object.DestroyImmediate(part.mesh);
            preview.Cleanup();
            ShaderUtil.allowAsyncCompilation = previousAsync;
        }
    }
}
#endif
