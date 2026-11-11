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
public static partial class LegacyWebGLWardrobePreparation
{
    public static void ReviewPlayerCuratedModernWardrobeRelease()
    {
        Directory.CreateDirectory(OutputRoot);
        string reportPath = OutputRoot + "/prepared-player-modern-validation.json";
        var report = new JObject
        {
            ["passed"] = false,
            ["validationStage"] = "final-fit",
            ["reviewScope"] = "player-reviewed-settings-on-previously-verified-meshes",
            ["errors"] = new JArray(),
        };
        try
        {
            var protectedPaths = new[] { PeopleRoot + "/catalog.json", ModernReleaseCatalogPath, ModernReleaseFitPath,
                ModernReleaseSelectionPath, PreparedRoot + "/base-catalog.json", PreparedRoot + "/preparation.json" };
            var hashes = protectedPaths.ToDictionary(path => path, FinalFileSHA, StringComparer.Ordinal);
            var catalog = JObject.Parse(File.ReadAllText(ModernReleaseCatalogPath));
            var manifest = JObject.Parse(File.ReadAllText(ModernReleaseFitPath));
            var selection = ReadModernReleaseExclusions(JObject.Parse(File.ReadAllText(PreparedRoot + "/preparation.json")));
            var rows = (JObject)manifest["rows"];
            var records = ((JArray)catalog["records"]).OfType<JObject>().ToArray();
            var wearRows = records.Where(row => (string)row["type"] == "wear").ToArray();
            var byPath = wearRows.ToLookup(row => (string)row["main"], StringComparer.Ordinal);
            if ((string)catalog["stage"] != "prepared-only" || (string)catalog["contentOrigin"] != "modern" ||
                (bool?)catalog["gameReady"] != false || (bool?)catalog["bundleBuildAuthorized"] != false ||
                (string)manifest["finalCatalog"]?["sha"] != hashes[ModernReleaseCatalogPath] ||
                (string)manifest["releaseSelectionSHA256"] != hashes[ModernReleaseSelectionPath] ||
                (string)catalog["releaseSelectionSHA256"] != hashes[ModernReleaseSelectionPath] ||
                rows.Count + selection.Count != 183 || rows.Count != byPath.Count ||
                manifest["playerReview"] is not JObject ||
                records.Length != wearRows.Length + rows.Count * 2)
                throw new InvalidDataException("Incomplete player-curated Modern inventory");

            var dependencies = new JArray();
            var prototypes = new JArray();
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows.Properties())
            {
                if (selection.ContainsKey(row.Name))
                    throw new InvalidDataException("Rejected model in Modern review: " + row.Name);
                var final = (JObject)row.Value["final"];
                string path = (string)final["prefab"];
                if (!paths.Add(path) || !FinalQualityAccepted(final["processorEvidence"]))
                    throw new InvalidDataException("Missing prior mesh verification: " + row.Name);
                ValidateFinalPrefab(path);
                foreach (var output in final["outputs"])
                {
                    string asset = (string)output["path"];
                    if (FinalFileSHA(asset) != (string)output["sha"] || FinalFileSHA(asset + ".meta") != (string)output["metaSha"] ||
                        AssetDatabase.AssetPathToGUID(asset) != (string)output["guid"])
                        throw new InvalidDataException("Unexpected player-reviewed asset bytes: " + asset);
                }

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var wear = prefab.GetComponent<Wear>();
                var settings = (JObject)final["settings"];
                if ((string)settings["layer"] != wear.Layer.ToString())
                    throw new InvalidDataException("Reviewed layer lost: " + row.Name);
                foreach (var actor in new[] { ActorName.Marta, ActorName.Kshishtof })
                {
                    var config = settings["configs"].Single(item => (string)item["actor"] == actor.ToString());
                    if (!wear.SupportsActor(actor) || (float)config["scale"] != wear.GetConfigScale(actor))
                        throw new InvalidDataException("Reviewed body or scale lost: " + row.Name + "/" + actor);
                }
                if (final["playerReview"] is JObject edit &&
                    (bool)edit["showGenitals"] != new SerializedObject(wear).FindProperty("showGenitals").boolValue)
                    throw new InvalidDataException("Reviewed visibility setting lost: " + row.Name);

                var ids = byPath[path].Select(record => (string)record["id"]).ToHashSet(StringComparer.Ordinal);
                if (!ids.SetEquals(final["logicalIds"].Values<string>()))
                    throw new InvalidDataException("Wrong reviewed variants: " + row.Name);
                foreach (var record in byPath[path])
                {
                    string id = (string)record["id"];
                    var definition = AssetDatabase.LoadAssetAtPath<GarmentDefinition>((string)record["definition"]);
                    if (definition == null || definition.id != id || definition.layer.ToString() != wear.Layer.ToString() ||
                        (string)record["metadata"]["simulation"]["layer"] != wear.Layer.ToString())
                        throw new InvalidDataException("Reviewed layer differs between prefab, variant and simulation: " + id);
                }
                prototypes.Add(new JObject { ["id"] = row.Name, ["prepared"] = path, ["logicalIds"] = new JArray(ids.OrderBy(id => id)) });
            }

            foreach (string path in records.Select(row => (string)row["main"])
                         .Concat(wearRows.Select(row => (string)row["definition"])).Distinct(StringComparer.Ordinal))
            {
                var deps = AssetDatabase.GetDependencies(path, true).OrderBy(value => value, StringComparer.Ordinal).ToArray();
                if (deps.Any(dep => dep.StartsWith(SourceRoot + "/", StringComparison.Ordinal) ||
                                    dep.StartsWith("Assets/ImportedActors/Wear/", StringComparison.Ordinal) ||
                                    dep.StartsWith("Assets/HexLiveContent/RuntimeSource/Wear/", StringComparison.Ordinal)))
                    throw new InvalidDataException("Native dependency in reviewed Modern content: " + path);
                dependencies.Add(new JObject { ["root"] = path, ["hash"] = AssetDatabase.GetAssetDependencyHash(path).ToString(),
                    ["paths"] = new JArray(deps) });
            }

            report["catalogSHA256"] = hashes[ModernReleaseCatalogPath];
            report["fitManifestSHA256"] = hashes[ModernReleaseFitPath];
            report["releaseSelectionSHA256"] = hashes[ModernReleaseSelectionPath];
            report["coverage"] = new JObject { ["definitions"] = wearRows.Length, ["uniquePrefabs"] = rows.Count,
                ["uniqueMeshes"] = 2 * rows.Count, ["paintMaps"] = 2 * rows.Count };
            report["dependencies"] = dependencies;
            report["prototypes"] = prototypes;
            report["gallery"] = RenderPreparedCollectionGallery(paths.OrderBy(path => path, StringComparer.Ordinal).ToArray(), wearRows);
            foreach (var pair in hashes)
                if (FinalFileSHA(pair.Key) != pair.Value)
                    throw new InvalidDataException("Review input changed during capture: " + pair.Key);
            report["passed"] = true;
            Debug.Log($"PLAYER_MODERN_REVIEW_VALID: {rows.Count} models, {wearRows.Length} variants");
        }
        catch (Exception error)
        {
            ((JArray)report["errors"]).Add(error.ToString());
            throw;
        }
        finally
        {
            File.WriteAllText(reportPath, report.ToString());
        }
    }
}
}
#endif
