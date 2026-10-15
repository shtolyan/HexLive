#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using HexLive.UnityPresentation.Wearing.Garments;

namespace HexLive.UnityPresentation.Wearing.Editor
{
// §169.8: changes three garment exports only; no bodies, material rebuild, bundles or server.
public static class PeopleWardrobeFitPreparation
{
    const string Root = PeopleAssetPreparation.Root;

    public static void Rebuild()
    {
        var paths = new List<string>();
        foreach (var key in new[] { "Marta_PrimalArmWraps", "Kshishtof_PrimalArmWraps", "Kshishtof_PrimalTop" })
        {
            string modelPath = Root + "/Source/Models/" + key + ".fbx";
            AssetDatabase.ImportAsset(modelPath, ImportAssetOptions.ForceSynchronousImport);
            var importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
            importer.importAnimation = false; importer.importBlendShapes = true;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.isReadable = true; importer.optimizeGameObjects = false;
            importer.optimizeBones = true; importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.skinWeights = ModelImporterSkinWeights.Custom;
            importer.maxBonesPerVertex = 32; importer.minBoneWeight = 0;
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.SaveAndReimport();
            string targetPath = Root + "/Prefabs/Wear/" + key + ".prefab";
            var template = AssetDatabase.LoadAssetAtPath<GameObject>(key == "Kshishtof_PrimalTop"
                ? Root + "/Prefabs/Wear/Marta_PrimalTop.prefab" : targetPath);
            var original = template.GetComponent<Wear>();
            var materials = template.GetComponentInChildren<SkinnedMeshRenderer>().sharedMaterials;
            var go = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(modelPath));
            try
            {
                go.name = key;
                foreach (var animator in go.GetComponentsInChildren<Animator>(true)) UnityEngine.Object.DestroyImmediate(animator);
                var renderer = go.GetComponentInChildren<SkinnedMeshRenderer>();
                if (renderer.sharedMesh.subMeshCount != materials.Length) throw new InvalidOperationException(key + ": material count changed");
                renderer.sharedMaterials = materials;
                var wear = go.AddComponent<Wear>(); EditorUtility.CopySerialized(original, wear);
                var serialized = new SerializedObject(wear);
                serialized.FindProperty("gender").intValue = (int)(key.StartsWith("Kshishtof_") ? VisualGender.Male : VisualGender.Female);
                serialized.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(go, targetPath); paths.Add(targetPath);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
        var catalog = JObject.Parse(File.ReadAllText(Root + "/catalog.json"));
        var records = (JArray)catalog["records"];
        foreach (var female in records.Where(r => (string)r["type"] == "wear" &&
                     (string)r["metadata"]?["artId"] == "clothing.top_primal").ToArray())
        {
            string id = "clothing.top_primal_male" + ((string)female["id"]).Substring("clothing.top_primal".Length);
            if (records.Any(r => (string)r["type"] == "wear" && (string)r["id"] == id)) continue;
            var male = (JObject)female.DeepClone(); male["id"] = id;
            male["main"] = Root + "/Prefabs/Wear/Kshishtof_PrimalTop.prefab";
            male["definition"] = Root + "/Definitions/" + id + ".asset";
            male["metadata"]["artId"] = "clothing.top_primal_male";
            male["metadata"]["sex"] = "Male";
            male["metadata"]["simulation"]["sex"] = "Male";
            male["metadata"]["simulation"]["prototypeId"] = "clothing.top_primal_male";
            var def = ScriptableObject.CreateInstance<GarmentDefinition>();
            EditorUtility.CopySerialized(AssetDatabase.LoadAssetAtPath<GarmentDefinition>((string)female["definition"]), def);
            def.id = id; def.prototypeId = "clothing.top_primal_male"; def.sexOverride = HexLive.Simulation.Content.GarmentSex.Male;
            AssetDatabase.CreateAsset(def, (string)male["definition"]); records.Add(male);
        }
        ApplyPairs(records);
        AddPaintMaps(records);
        File.WriteAllText(Root + "/catalog.json", catalog.ToString());
        var preparation = JObject.Parse(File.ReadAllText(Root + "/Validation/preparation.json"));
        var prefabs = (JArray)preparation["prefabs"];
        if (!prefabs.Any(r => (string)r["actor"] == "Kshishtof" && (string)r["id"] == "PrimalTop"))
            prefabs.Add(new JObject { ["kind"] = "wear", ["id"] = "PrimalTop", ["actor"] = "Kshishtof", ["path"] = paths.Last() });
        File.WriteAllText(Root + "/Validation/preparation.json", preparation.ToString());
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
        Debug.Log("PAIRED_WARDROBE: three prefabs, 82 reciprocal fits and paint maps prepared; no build/deploy");
    }

    public static void RefreshPaintMaps()
    {
        var catalog = JObject.Parse(File.ReadAllText(Root + "/catalog.json"));
        AddPaintMaps((JArray)catalog["records"]);
        File.WriteAllText(Root + "/catalog.json", catalog.ToString());
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
        Debug.Log("PAIRED_WARDROBE_MAPS: five garment maps prepared; body atlases unchanged; no build/deploy");
    }

    static void AddPaintMaps(JArray records)
    {
        // TrailPack straps were already remodeled; their old 2514-vertex maps
        // no longer match the shipped 3774-vertex meshes. Preserve old aliases.
        var paths = new[] { "Marta_PrimalArmWraps", "Kshishtof_PrimalArmWraps", "Kshishtof_PrimalTop",
            "Marta_PrimalTrailPack", "Kshishtof_PrimalTrailPack" }
            .Select(key => Root + "/Prefabs/Wear/" + key + ".prefab").ToArray();
        // The generator is in UnityDebug.Editor; preserve assembly direction.
        var baker = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("HexLive.UnityDebug.Editor.PaintPointMapGenerator")).First(t => t != null);
        var mapPaths = (string[])baker.GetMethod("GenerateWebGLGarmentMaps").Invoke(null, new object[] { paths });
        foreach (string path in mapPaths)
        {
            string name = Path.GetFileNameWithoutExtension(path), id = ("PaintMaps." + name).ToLowerInvariant();
            if (records.Any(r => (string)r["type"] == "config" && (string)r["id"] == id)) continue;
            records.Add(new JObject { ["type"] = "config", ["id"] = id, ["main"] = path,
                ["metadata"] = new JObject { ["peopleCatalog"] = PeopleIdMap.CatalogId,
                    ["legacyResourcePath"] = "HexLive/PaintMaps/" + name, ["legacyResourceFolder"] = "HexLive/PaintMaps" } });
        }
    }

    public static void ApplyPairs(JArray records)
    {
        var wear = records.Where(r => (string)r["type"] == "wear").ToDictionary(r => (string)r["id"]);
        foreach (var row in wear.Values.Where(r => (string)r["metadata"]?["sex"] == "Female"))
        {
            string id = (string)row["id"], art = (string)row["metadata"]["artId"];
            string otherId = art + "_male" + id.Substring(art.Length);
            if (!wear.TryGetValue(otherId, out var other)) throw new InvalidOperationException("Missing authored male fit: " + id);
            foreach (var pair in new[] { (row, otherId), (other, id) })
            {
                pair.Item1["metadata"]["simulation"]["alternateFitId"] = pair.Item2;
                var def = AssetDatabase.LoadAssetAtPath<GarmentDefinition>((string)pair.Item1["definition"]);
                def.alternateFitId = pair.Item2; EditorUtility.SetDirty(def);
            }
        }
    }
}
}
#endif
