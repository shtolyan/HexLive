#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Wearing;
using HexLive.UnityPresentation.Wearing.Garments;
using HexLive.UnityPresentation.Wearing.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HexLive.UnityDebug.Editor
{
public static partial class LegacyWebGLWardrobePreparation
{
    const string PreparedRoot = PeopleRoot + "/Wardrobe";
    const string Algorithm = "skin-surface-v8-adaptive-posed-1k";
    static JObject ModelCache;
    static JObject TextureCache;
    static readonly Dictionary<string, Material> PreparedMaterials = new();
    static readonly Dictionary<string, Texture> PreparedTextures = new();

    static string Key(string value) => Hash128.Compute(value).ToString();
    static string FileHash(string path)
    {
        using var stream = File.OpenRead(path); using var hash = SHA256.Create();
        return Convert.ToBase64String(hash.ComputeHash(stream));
    }
    static string PipelineFingerprint() => Key(string.Join("|", new[] {
        "LegacyWebGLWardrobeAssets.cs", "LegacyWebGLWardrobeGeometry.cs", "LegacyWardrobeTriangleTree.cs", "LegacyWebGLWardrobePreparation.cs", "LegacyWardrobeQuality.cs"
    }.Select(name => FileHash("Assets/HexLive/UnityDebug/Editor/" + name))));

    // ConstructFit loads these baselines dynamically; actor prefab dependencies
    // do not necessarily include them. Include every body/slot deterministically.
    static readonly string[] FittingUnderlayerPaths = {
        PeopleRoot + "/Prefabs/Wear/Marta_PrimalBriefs.prefab",
        PeopleRoot + "/Prefabs/Wear/Marta_PrimalTop.prefab",
        PeopleRoot + "/Prefabs/Wear/Kshishtof_PrimalBriefs.prefab",
        PeopleRoot + "/Prefabs/Wear/Kshishtof_PrimalTop.prefab"
    };
    static string DependencyFingerprint(IEnumerable<string> paths) => Key(string.Join("|", paths.Select(path =>
    {
        if (AssetDatabase.LoadMainAssetAtPath(path) == null)
            throw new InvalidOperationException("Missing fitting dependency " + path);
        return path + "|" + AssetDatabase.GetAssetDependencyHash(path);
    })));
    static string SimplifierInputFingerprint()
    {
        var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UnityMeshSimplifier.MeshSimplifier).Assembly);
        if (package == null || string.IsNullOrEmpty(package.resolvedPath))
            throw new InvalidOperationException("Cannot resolve pinned mesh simplifier package");
        string runtime = Path.Combine(package.resolvedPath, "Runtime");
        var files = Directory.GetFiles(runtime, "*.cs", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (files.Length == 0 || !files.Any(path => Path.GetFileName(path) == "MeshSimplifier.cs"))
            throw new InvalidOperationException("Cannot fingerprint mesh simplifier runtime");
        return Key(string.Join("|", new[] { package.name, package.version, package.source.ToString(), package.git?.hash ?? "",
            FileHash(Path.Combine(package.resolvedPath, "package.json")) }.Concat(files.Select(path =>
                path.Substring(package.resolvedPath.Length).Replace('\\', '/') + "|" + FileHash(path)))));
    }
    static string PrototypeInputFingerprint(string path) => Key(Algorithm + "|" + PipelineFingerprint() + "|" +
        DependencyFingerprint(new[] { path, PeopleRoot + "/Prefabs/Actors/Marta.prefab", PeopleRoot + "/Prefabs/Actors/Kshishtof.prefab" }) + "|" +
        DependencyFingerprint(FittingUnderlayerPaths) + "|" + SimplifierInputFingerprint());

    static bool CachedPrototypeValid(GameObject prefab)
    {
        if (prefab == null) return false;
        var wear = prefab.GetComponent<Wear>();
        if (wear == null || !wear.SupportsActor(ActorName.Marta) || !wear.SupportsActor(ActorName.Kshishtof)) return false;
        var data = new SerializedObject(wear).FindProperty("configs"); var meshes = new HashSet<Mesh>();
        for (int i = 0; i < data.arraySize; i++)
        {
            var mesh = data.GetArrayElementAtIndex(i).FindPropertyRelative("mesh").objectReferenceValue as Mesh;
            if (mesh == null || mesh.vertexCount == 0 || mesh.subMeshCount == 0 || !mesh.isReadable ||
                !AssetDatabase.GetAssetPath(mesh).StartsWith(PreparedRoot + "/Meshes/",StringComparison.Ordinal)) return false;
            meshes.Add(mesh);
        }
        if (meshes.Count != 2) return false;
        var skins = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if (skins.Length != 1 || skins[0].sharedMaterials.Any(m => m == null || !AssetDatabase.GetAssetPath(m).StartsWith(PreparedRoot + "/Materials/",StringComparison.Ordinal))) return false;
        foreach (string dependency in AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(prefab),true))
            if (dependency.StartsWith(SourceRoot + "/",StringComparison.Ordinal) || dependency.StartsWith("Assets/HexLiveContent/RuntimeSource/Wear/",StringComparison.Ordinal)) return false;
        return true;
    }
    static string AssetKey(Object value)
    {
        if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long localId))
            throw new InvalidOperationException("Non-asset garment dependency " + value.name);
        return guid + "_" + localId;
    }
    static T Save<T>(T value, string path) where T : Object
    {
        var existing = AssetDatabase.LoadAssetAtPath<T>(path);
        if (existing == null) { AssetDatabase.CreateAsset(value, path); return value; }
        EditorUtility.CopySerialized(value, existing); Object.DestroyImmediate(value);
        EditorUtility.SetDirty(existing); return existing;
    }

    static Texture PreparedTexture(Texture source)
    {
        if (source == null) return null;
        TextureCache ??= File.Exists(PreparedRoot + "/texture-fingerprints.json")
            ? JObject.Parse(File.ReadAllText(PreparedRoot + "/texture-fingerprints.json")) : new JObject();
        string original = AssetDatabase.GetAssetPath(source);
        if (string.IsNullOrEmpty(original)) throw new InvalidOperationException("Non-asset garment texture " + source.name);
        string identity = AssetKey(source);
        if (PreparedTextures.TryGetValue(identity, out var cached)) return cached;
        string path = PreparedRoot + "/Textures/" + identity + Path.GetExtension(original);
        string rawHash = FileHash(original);
        string sourceHash = Key(rawHash + "|" + AssetDatabase.GetAssetDependencyHash(original));
        bool refresh = !File.Exists(path) || FileHash(path) != rawHash || (string)TextureCache[identity] != sourceHash;
        if (!File.Exists(path))
        { if (!AssetDatabase.CopyAsset(original, path)) throw new IOException("Cannot copy " + original); }
        else if (refresh)
        {
            File.Copy(original,path,true); // Owned copy only; retain its GUID.
            AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        }
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) throw new InvalidOperationException("Not a texture import: " + original);
        // CopyAsset retains normal/alpha/colour-space settings, never edits native authoring.
        if (refresh || importer.maxTextureSize > 1024 || importer.isReadable || !importer.mipmapEnabled)
        {
            var sourceImporter = AssetImporter.GetAtPath(original) as TextureImporter;
            if (sourceImporter == null) throw new InvalidOperationException("Missing texture source importer " + original);
            var settings = new TextureImporterSettings(); sourceImporter.ReadTextureSettings(settings); importer.SetTextureSettings(settings);
            foreach (string platform in new[] { "Standalone", "WebGL", "Android", "iPhone" }) importer.ClearPlatformTextureSettings(platform);
            importer.maxTextureSize = Mathf.Min(1024,sourceImporter.maxTextureSize); importer.isReadable = false; importer.mipmapEnabled = true;
            importer.textureCompression = TextureImporterCompression.Compressed; importer.SaveAndReimport();
            TextureCache[identity] = sourceHash;
            File.WriteAllText(PreparedRoot + "/texture-fingerprints.json",TextureCache.ToString());
        }
        return PreparedTextures[identity] = AssetDatabase.LoadAssetAtPath<Texture>(path) ?? throw new InvalidOperationException("Copied texture failed to import " + path);
    }

    static Material PreparedMaterial(Material source)
    {
        if (source == null) throw new InvalidOperationException("Missing authored garment material");
        string original = AssetDatabase.GetAssetPath(source);
        string identity = AssetKey(source);
        if (PreparedMaterials.TryGetValue(identity, out var cached)) return cached;
        if (source.shader == null || source.shader.name != "Universal Render Pipeline/Lit")
            throw new InvalidOperationException("Unsupported authored shader: " + original);
        var value = new Material(source) { name = source.name };
        foreach (string property in value.GetTexturePropertyNames())
            if (value.GetTexture(property) != null) value.SetTexture(property, PreparedTexture(value.GetTexture(property)));
        return PreparedMaterials[identity] = Save(value, PreparedRoot + "/Materials/" + identity + ".mat");
    }

    static void RestoreStandalonePose(GameObject output, Fit reference)
    {
        // Construct reparents garment bones under body bones. Rebuild the SAME
        // world pose in the original hierarchy so drops/hangers and runtime use
        // one prefab, and the next Construct does not apply an extra rest offset.
        foreach (var transform in output.GetComponentsInChildren<Transform>(true))
        {
            string path = RelativePath(output.transform, transform);
            if (!reference.RestWorld.TryGetValue(path, out var matrix)) throw new InvalidOperationException("Missing rest transform " + path);
            transform.SetPositionAndRotation(matrix.GetColumn(3), matrix.rotation);
            var parent = transform.parent;
            var local = parent == null ? matrix : parent.worldToLocalMatrix * matrix;
            transform.localScale = local.lossyScale;
        }
        foreach (var transform in output.GetComponentsInChildren<Transform>(true))
        {
            var expected = reference.RestWorld[RelativePath(output.transform, transform)];
            var actual = transform.localToWorldMatrix;
            for (int i = 0; i < 16; i++)
                if (Mathf.Abs(actual[i] - expected[i]) > .0002f)
                    throw new InvalidOperationException("Rest transform decomposition changed " + transform.name);
        }
    }

    static bool HasMesh(Wear wear, ActorName actor)
    {
        var array = new SerializedObject(wear).FindProperty("configs");
        for (int i = 0; i < array.arraySize; i++)
        {
            var row = array.GetArrayElementAtIndex(i);
            if (row.FindPropertyRelative("actorName").intValue == (int)actor && row.FindPropertyRelative("mesh").objectReferenceValue != null) return true;
        }
        return false;
    }

    static string PreparePrototype(string path, JArray metrics)
    {
        string id = Path.GetFileName(Path.GetDirectoryName(path)), key = Key(id);
        string preparedPath = PreparedRoot + "/Prefabs/" + key + ".prefab";
        string fingerprint = PrototypeInputFingerprint(path);
        if ((string)ModelCache[id]?["fingerprint"] == fingerprint && CachedPrototypeValid(AssetDatabase.LoadAssetAtPath<GameObject>(preparedPath)))
        { metrics.Add(ModelCache[id].DeepClone()); return preparedPath; }
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path); var wear = prefab.GetComponent<Wear>();
        var settings = Settings(wear).ToString();
        bool nativeMale = !HasMesh(wear, ActorName.Marta) && HasMesh(wear, ActorName.Kshishtof);
        var primaryActor = nativeMale ? ActorName.Kshishtof : ActorName.Marta;
        var source = SourceMesh(wear, primaryActor, out float scale);
        if (source == null) throw new InvalidOperationException(id + ": no retained geometry");
        // The documented skirt tuning predates the RuntimeSource copy. The
        // native publisher still resolves WearRoot, whose configs lost that edit.
        if (id.StartsWith("clothing.skirt_", StringComparison.Ordinal) && Mathf.Approximately(scale, 1f)) scale = 1.03f;
        Mesh simplified = null, femaleMesh = null, maleMesh = null, posed = null;
        GameObject output = null, template = null;
        try
        {
            template = Object.Instantiate(prefab); StripPlayerIncompatibleComponents(template);
            var previous = AssetDatabase.LoadAssetAtPath<GameObject>(preparedPath);
            var previousWear = previous != null ? previous.GetComponent<Wear>() : null;
            if (previousWear != null) EditorUtility.CopySerialized(previousWear, template.GetComponent<Wear>());
            float femaleScale = previousWear != null ? previousWear.GetConfigScale(ActorName.Marta) : scale;
            float maleScale = previousWear != null ? previousWear.GetConfigScale(ActorName.Kshishtof) : scale;
            SetFits(template.GetComponent<Wear>(), source, source, femaleScale, maleScale, false);
            // The imported fit still deforms through its old bind pose. Reduce
            // the geometry the player actually sees, not the undeformed source
            // coordinates: otherwise a low-error collapse can open the boots
            // after the old bone transforms amplify its skinning-weight error.
            Matrix4x4 referenceMatrix;
            using (var native = ConstructFit(template, source, source, primaryActor))
            {posed = BindMesh(source, native, native.WorldVertices, native.WorldNormals, "Posed_" + key);referenceMatrix=native.Skin.transform.localToWorldMatrix;}
            posed.ClearBlendShapes(); // Body morphs are untouched; these are fixed fitted garments.
            simplified = Simplify(posed, key,referenceMatrix);
            using var female = ConstructFit(template, simplified, simplified, ActorName.Marta);
            using var male = ConstructFit(template, simplified, simplified, ActorName.Kshishtof);
            int conformed;
            if (nativeMale)
            {
                maleMesh = Object.Instantiate(simplified);
                femaleMesh = Retarget(simplified, male, female, out conformed);
            }
            else
            {
                femaleMesh = Object.Instantiate(simplified);
                maleMesh = Retarget(simplified, female, male, out conformed);
            }
            femaleMesh.name = "Legacy_" + key + "_Marta"; maleMesh.name = "Legacy_" + key + "_Kshishtof";
            femaleMesh = Save(femaleMesh, PreparedRoot + "/Meshes/" + key + "_Marta.asset");
            maleMesh = Save(maleMesh, PreparedRoot + "/Meshes/" + key + "_Kshishtof.asset");
            output = Object.Instantiate(template); output.name = "Legacy_" + id;
            if (PrefabUtility.IsPartOfPrefabInstance(output))
                PrefabUtility.UnpackPrefabInstance(output, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            StripPlayerIncompatibleComponents(output); RestoreStandalonePose(output, female);
            var outputWear = output.GetComponent<Wear>();
            SetFits(outputWear, femaleMesh, maleMesh, femaleScale, maleScale, false);
            var serialized = new SerializedObject(outputWear);
            serialized.FindProperty("preparedBodyFits").boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            var skin = output.GetComponentInChildren<SkinnedMeshRenderer>(true);
            skin.sharedMesh = femaleMesh; skin.sharedMaterials = skin.sharedMaterials.Select(PreparedMaterial).ToArray();
            if (output.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length != 1 || output.GetComponentsInChildren<MeshRenderer>(true).Length != 0)
                throw new InvalidOperationException(id + ": unexpected secondary garment renderer");
            PrefabUtility.SaveAsPrefabAsset(output, preparedPath);
            if (Settings(wear).ToString() != settings) throw new InvalidOperationException(id + ": native authoring mutated");
            var metric = new JObject { ["id"] = id, ["source"] = path, ["prepared"] = preparedPath, ["fingerprint"] = fingerprint,
                ["sourceVertices"] = source.vertexCount, ["verticesPerBody"] = simplified.vertexCount,
                ["trianglesPerBody"] = Enumerable.Range(0, simplified.subMeshCount).Sum(i => (long)simplified.GetIndexCount(i)) / 3,
                ["donor"] = primaryActor.ToString(), ["conformedVertices"] = conformed,
                ["scale"] = scale, ["settings"] = Settings(outputWear),["quality"]=MeshQuality[key].DeepClone(), ["sourceSettingsUnchanged"] = true };
            metrics.Add(metric); ModelCache[id] = metric.DeepClone();
            File.WriteAllText(PreparedRoot + "/preparation.json", ModelCache.ToString());
            return preparedPath;
        }
        finally
        {
            if (output != null) Object.DestroyImmediate(output);
            if (template != null) Object.DestroyImmediate(template);
            if (simplified != null) Object.DestroyImmediate(simplified);
            if (posed != null) Object.DestroyImmediate(posed);
            // Generated meshes are persistent assets, retained for the prefab.
            if (femaleMesh != null && !EditorUtility.IsPersistent(femaleMesh)) Object.DestroyImmediate(femaleMesh);
            if (maleMesh != null && !EditorUtility.IsPersistent(maleMesh)) Object.DestroyImmediate(maleMesh);
        }
    }

    public static void PrepareSample() => PrepareCollection(true);
    public static void PrepareAll() => PrepareCollection(false);
    static void PrepareCollection(bool sample)
    {
        RestorePrimalAuthoringDefaults();
        string activeHash=FileHash(PeopleRoot+"/catalog.json");
        if (AssetDatabase.IsValidFolder(PeopleRoot + "/Legacy") && !AssetDatabase.IsValidFolder(PreparedRoot))
        {
            string error = AssetDatabase.MoveAsset(PeopleRoot + "/Legacy", PreparedRoot);
            if (!string.IsNullOrEmpty(error)) throw new IOException(error);
            if(File.Exists(PreparedRoot+"/catalog.json"))
                File.WriteAllText(PreparedRoot+"/catalog.json",File.ReadAllText(PreparedRoot+"/catalog.json").Replace(PeopleRoot+"/Legacy/",PreparedRoot+"/"));
        }
        foreach (string folder in new[] { "Prefabs", "Meshes", "Materials", "Textures", "Definitions", "PaintMaps" }) Directory.CreateDirectory(PreparedRoot + "/" + folder);
        AssetDatabase.Refresh(); PreparedMaterials.Clear(); PreparedTextures.Clear();
        foreach(string guid in AssetDatabase.FindAssets("t:PaintPointMap",new[]{PeopleRoot+"/PaintMaps"}))
        {
            string path=AssetDatabase.GUIDToAssetPath(guid),file=Path.GetFileName(path);
            if(!file.StartsWith("garment_Legacy_",StringComparison.Ordinal))continue;
            string error=AssetDatabase.MoveAsset(path,PreparedRoot+"/PaintMaps/"+file);
            if(!string.IsNullOrEmpty(error))throw new IOException(error);
        }
        ModelCache = File.Exists(PreparedRoot + "/preparation.json") ? JObject.Parse(File.ReadAllText(PreparedRoot + "/preparation.json")) : new JObject();
        TextureCache = File.Exists(PreparedRoot + "/texture-fingerprints.json") ? JObject.Parse(File.ReadAllText(PreparedRoot + "/texture-fingerprints.json")) : new JObject();
        var activeCatalog = JObject.Parse(File.ReadAllText(PeopleRoot + "/catalog.json"));
        var catalog=File.Exists(PreparedRoot+"/catalog.json")?JObject.Parse(File.ReadAllText(PreparedRoot+"/catalog.json")):
            new JObject{["schemaVersion"]=1,["peopleCatalog"]=PeopleIdMap.CatalogId,["stage"]="prepared-only",["gameReady"]=false,["bundleBuildAuthorized"]=false,["records"]=new JArray()};
        if(!sample)catalog["records"]=new JArray();
        var records = (JArray)catalog["records"];
        var retainedIds = new HashSet<string>(activeCatalog["records"].Where(r => (string)r["type"] == "wear").Select(r => (string)r["id"]));
        var definitions = AssetDatabase.FindAssets("t:GarmentDefinition", new[] { "Assets/HexLive/UnityPresentation/Wearing/Garments/Assets" })
            .Select(g => AssetDatabase.LoadAssetAtPath<GarmentDefinition>(AssetDatabase.GUIDToAssetPath(g))).ToArray();
        var prototypes = AssetDatabase.FindAssets("t:Prefab", new[] { SourceRoot }).Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        if (sample) prototypes = prototypes.Where(p => new[] { "clothing.ankleboots_amy", "clothing.skirt_anarchy", "underwear.top_vapor", "FCO Pants Male" }.Contains(Path.GetFileName(Path.GetDirectoryName(p)))).ToArray();
        var rows = new JArray(); var missing = new JArray(); var prepared = new Dictionary<string, string>();
        foreach (string path in prototypes)
        {
            string id = Path.GetFileName(Path.GetDirectoryName(path));
            if (!definitions.Any(d => d.ArtId == id && !retainedIds.Contains(d.id))) continue;
            prepared[id] = PreparePrototype(path, rows);
            Debug.Log("LEGACY_WARDROBE_PREPARED " + id + " (" + prepared.Count + ")");
        }
        foreach (var definition in definitions)
        {
            if (retainedIds.Contains(definition.id)) continue;
            if (!prepared.TryGetValue(definition.ArtId, out string prefab)) { if (!sample) missing.Add(definition.id); continue; }
            string definitionPath = PreparedRoot + "/Definitions/" + Key(definition.id) + ".asset";
            var copy = Object.Instantiate(definition); copy.hasSexOverride = true; copy.sexOverride = GarmentSex.Any;
            copy.alternateFitId = ""; copy.variantMaterials = definition.variantMaterials.Select(m => m == null ? null : PreparedMaterial(m)).ToArray();
            copy = Save(copy, definitionPath);
            var wear = AssetDatabase.LoadAssetAtPath<GameObject>(prefab).GetComponent<Wear>();
            // Physical layer and authored hiding masks travel together.
            copy.layer = (WearLayer)Enum.Parse(typeof(WearLayer), wear.Layer.ToString()); EditorUtility.SetDirty(copy);
            foreach (var old in records.Where(r => (string)r["type"] == "wear" && (string)r["id"] == copy.id).ToArray()) old.Remove();
            records.Add(new JObject { ["type"] = "wear", ["id"] = copy.id, ["main"] = prefab, ["definition"] = definitionPath,
                ["metadata"] = new JObject { ["peopleCatalog"] = PeopleIdMap.CatalogId, ["legacyWardrobe"] = true,
                    ["artId"] = copy.ArtId, ["sex"] = "Any", ["slots"] = new JArray(wear.Slots.Select(s => s.ToString())),
                    ["simulation"] = new JObject { ["displayName"] = copy.displayName, ["prototypeId"] = copy.ArtId,
                        ["layer"] = copy.layer.ToString(), ["sex"] = "Any", ["warmth"] = copy.warmth, ["armor"] = copy.armor,
                        ["thermalDelta"] = copy.thermalDelta, ["dressDurationTicks"] = copy.dressDurationTicks, ["capacity"] = copy.capacity,
                        ["retired"] = copy.retired, ["covers"] = new JArray(copy.covers.Select(c => c.ToString())) } } });
        }
        var processedPrefixes=prepared.Keys.Select(id=>"garment_Legacy_"+Key(id)+"_").ToArray();
        foreach(var old in records.Where(r=>(string)r["type"]=="config" && processedPrefixes.Any(prefix=>Path.GetFileName((string)r["main"]).StartsWith(prefix,StringComparison.Ordinal))).ToArray())old.Remove();
        var mapPaths = PaintPointMapGenerator.GenerateWebGLGarmentMaps(prepared.Values.ToArray(),PreparedRoot+"/PaintMaps");
        foreach (string path in mapPaths)
        {
            string name = Path.GetFileNameWithoutExtension(path), id = ("PaintMaps." + name).ToLowerInvariant();
            foreach (var old in records.Where(r => (string)r["type"] == "config" && (string)r["id"] == id).ToArray()) old.Remove();
            records.Add(new JObject { ["type"] = "config", ["id"] = id, ["main"] = path,
                ["metadata"] = new JObject { ["peopleCatalog"] = PeopleIdMap.CatalogId, ["legacyWardrobe"] = true,
                    ["legacyResourcePath"] = "HexLive/PaintMaps/" + name, ["legacyResourceFolder"] = "HexLive/PaintMaps" } });
        }
        AssetDatabase.SaveAssets();
        File.WriteAllText(PreparedRoot + "/catalog.json", catalog.ToString());
        if(FileHash(PeopleRoot+"/catalog.json")!=activeHash)throw new InvalidOperationException("Preparation changed the active spawn catalog");
        if (sample) foreach (string prefab in prepared.Values) RenderPrepared(prefab);
        File.WriteAllText(OutputRoot + "/prepared" + (sample ? "-sample" : "-all") + ".json", new JObject {
            ["algorithm"] = Algorithm, ["prototypes"] = rows, ["unresolvedAuthoringDefinitions"] = missing,
            ["activeCatalogUnchanged"]=true,["stagedCatalog"]=PreparedRoot+"/catalog.json",["bundlesBuilt"] = false, ["productionChanged"] = false }.ToString());
        AssetDatabase.Refresh();
    }

    static void RestorePrimalAuthoringDefaults()
    {
        var preparation=JObject.Parse(File.ReadAllText(PeopleRoot+"/Validation/preparation.json"));
        foreach(var row in preparation["prefabs"].Where(r=>(string)r["kind"]=="wear" || (string)r["kind"]=="backpack"))
        {
            string path=(string)row["path"];var go=PrefabUtility.LoadPrefabContents(path);
            try
            {
                var wear=go.GetComponent<Wear>();
                WearPreparationSettings.Restore(wear,wear,(ActorName)Enum.Parse(typeof(ActorName),(string)row["actor"]),
                    (string)row["id"]=="PrimalSkirt"?1.03f:1f);
                PrefabUtility.SaveAsPrefabAsset(go,path);
            }
            finally{PrefabUtility.UnloadPrefabContents(go);}
        }
        AssetDatabase.SaveAssets();
    }
}
}
#endif
