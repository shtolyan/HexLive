#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using HexLive.UnityPresentation.Wearing.Garments;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{
/// <summary>§169.10: inspect retained authoring inputs without changing assets or building bundles.</summary>
public static partial class LegacyWebGLWardrobePreparation
{
    // The canonical native publisher ResolveWear resolves this authoring root.
    // RuntimeSource/Wear is an older copy and omits retained helmets/prototypes.
    public const string SourceRoot = "Assets/HexLiveContent/Wear";
    public const string OutputRoot = "Build/Diagnostics/bug-wave-20261110/wardrobe";
    public static void ProbeAndExport()
    {
        AuditBootsGeometry();
        ExportSimData.Export();
    }
    public static void PrepareAllAndExport()
    {
        ExportSimData.Export();
        PrepareAll();
    }

    public static void Audit()
    {
        var rows = new JArray();
        var errors = new JArray();
        foreach (var path in AssetDatabase.FindAssets("t:GameObject", new[] { SourceRoot })
                     .Select(AssetDatabase.GUIDToAssetPath).Where(p => p.EndsWith(".prefab", StringComparison.Ordinal))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var wear = prefab != null ? prefab.GetComponent<Wear>() : null;
            if (wear == null) { errors.Add(path + ": missing root Wear"); continue; }
            var data = new SerializedObject(wear);
            var configs = data.FindProperty("configs");
            var fits = new JArray();
            for (var i = 0; i < configs.arraySize; i++)
            {
                var fit = configs.GetArrayElementAtIndex(i);
                var mesh = fit.FindPropertyRelative("mesh").objectReferenceValue as Mesh;
                var actor = (ActorName)fit.FindPropertyRelative("actorName").intValue;
                fits.Add(new JObject {
                    ["actor"] = actor.ToString(), ["scale"] = fit.FindPropertyRelative("scale").floatValue,
                    ["heightOffset"] = fit.FindPropertyRelative("heightOffset").floatValue,
                    ["mesh"] = mesh == null ? "" : AssetDatabase.GetAssetPath(mesh),
                    ["meshName"] = mesh == null ? "" : mesh.name,
                    ["vertices"] = mesh == null ? 0 : mesh.vertexCount,
                    ["triangles"] = mesh == null ? 0 : (long)Enumerable.Range(0, mesh.subMeshCount).Sum(s => (double)mesh.GetIndexCount(s)) / 3,
                    ["readable"] = mesh != null && mesh.isReadable
                });
            }
            var renderers = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            rows.Add(new JObject {
                ["id"] = Path.GetFileName(Path.GetDirectoryName(path)), ["prefab"] = path,
                ["settings"] = Settings(wear), ["fits"] = fits,
                ["renderers"] = new JArray(renderers.Select(r => new JObject {
                    ["name"] = r.name, ["mesh"] = r.sharedMesh == null ? "" : AssetDatabase.GetAssetPath(r.sharedMesh),
                    ["vertices"] = r.sharedMesh == null ? 0 : r.sharedMesh.vertexCount,
                    ["bones"] = new JArray(r.bones.Select(b => b == null ? "" : b.name)),
                    ["materials"] = new JArray(r.sharedMaterials.Select(m => new JObject {
                        ["path"] = m == null ? "" : AssetDatabase.GetAssetPath(m),
                        ["shader"] = m == null || m.shader == null ? "" : m.shader.name
                    }))
                }))
            });
        }
        var definitions = new JArray();
        foreach (var guid in AssetDatabase.FindAssets("t:GarmentDefinition"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.StartsWith("Assets/HexLiveContent/People/", StringComparison.Ordinal)) continue;
            var definition = AssetDatabase.LoadAssetAtPath<GarmentDefinition>(path);
            if (definition == null) continue;
            definitions.Add(new JObject {
                ["id"] = definition.id, ["artId"] = definition.ArtId, ["path"] = path,
                ["retired"] = definition.retired, ["layer"] = definition.layer.ToString(),
                ["materials"] = new JArray(definition.variantMaterials.Select(m => m == null ? "" : AssetDatabase.GetAssetPath(m)))
            });
        }
        Directory.CreateDirectory(OutputRoot);
        File.WriteAllText(OutputRoot + "/legacy-authoring-audit.json", new JObject {
            ["schemaVersion"] = 1, ["passed"] = errors.Count == 0,
            ["sourceRoot"] = SourceRoot, ["prototypes"] = rows,
            ["definitions"] = definitions, ["errors"] = errors,
            ["assetsChanged"] = false, ["bundlesBuilt"] = false
        }.ToString());
        Debug.Log($"LEGACY_WARDROBE_AUDIT: {rows.Count} prototypes, {definitions.Count} definitions, {errors.Count} errors; no assets changed");
    }

    public static JObject Settings(Wear wear)
    {
        var serialized = new SerializedObject(wear);
        var result = new JObject();
        var configs = serialized.FindProperty("configs"); var fits = new JArray();
        for (int i = 0; i < configs.arraySize; i++)
        {
            var fit = configs.GetArrayElementAtIndex(i); var mesh = fit.FindPropertyRelative("mesh").objectReferenceValue;
            fits.Add(new JObject { ["actor"] = ((ActorName)fit.FindPropertyRelative("actorName").intValue).ToString(),
                ["scale"] = fit.FindPropertyRelative("scale").floatValue,
                ["heightOffset"] = fit.FindPropertyRelative("heightOffset").floatValue,
                ["mesh"] = mesh == null ? "" : AssetDatabase.GetAssetPath(mesh) });
        }
        result["configs"] = fits;
        foreach (var field in new[] { "slots", "noHideUnderwearSlots", "hideWearSlots" })
        {
            var values = serialized.FindProperty(field);
            var names = new JArray();
            for (var i = 0; i < values.arraySize; i++) names.Add(((VisualWearSlot)values.GetArrayElementAtIndex(i).intValue).ToString());
            result[field] = names;
        }
        result["layer"] = wear.Layer.ToString();
        result["gender"] = wear.Gender.ToString();
        result["hidesHair"] = wear.HidesHair;
        result["heel"] = JObject.Parse(JsonUtility.ToJson(wear.Heel));
        result["headwearFit"] = JObject.Parse(JsonUtility.ToJson(serialized.FindProperty("headwearFit").boxedValue));
        return result;
    }
}
}
#endif
