#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{
public static partial class LegacyWebGLWardrobePreparation
{
    const string ModernReleaseSelectionPath = PreparedRoot + "/release-selection.json";
    const string ModernReleaseCatalogPath = PreparedRoot + "/release-catalog.json";
    const string ModernReleaseFitPath = PreparedRoot + "/release-fit.json";

    static Dictionary<string, JObject> ReadModernReleaseExclusions(JObject preparation)
    {
        var policy = JObject.Parse(File.ReadAllText(ModernReleaseSelectionPath));
        if ((int?)policy["schemaVersion"] != 1 || !(policy["excludedModels"] is JArray entries))
            throw new InvalidDataException("Invalid Modern wardrobe exclusion policy");

        var exclusions = new Dictionary<string, JObject>(StringComparer.Ordinal);
        foreach (var entry in entries.OfType<JObject>())
        {
            string id = (string)entry["id"];
            string source = (string)entry["source"];
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(source) ||
                (string)preparation[id]?["source"] != source ||
                string.IsNullOrWhiteSpace((string)entry["reason"]) || exclusions.ContainsKey(id))
                throw new InvalidDataException("Unresolved or duplicate Modern wardrobe exclusion: " + id);
            exclusions.Add(id, entry);
        }
        if (exclusions.Count != entries.Count)
            throw new InvalidDataException("Every Modern wardrobe exclusion must be an object");
        return exclusions;
    }

    // Select the staged release inventory from completed authoring results.
    // This pass does not change the base or the per-model geometry fingerprints.
    public static void PrepareCuratedModernWardrobeRelease()
    {
        string cachePath = PreparedRoot + "/preparation.json";
        string basePath = PreparedRoot + "/base-catalog.json";
        string proofPath = OutputRoot + "/final-fit-input-closure.json";
        string ledgerPath = PreparedRoot + "/final-fit-cache.json";
        var preparation = JObject.Parse(File.ReadAllText(cachePath));
        string policySHA = FinalFileSHA(ModernReleaseSelectionPath);
        var exclusions = ReadModernReleaseExclusions(preparation);
        byte[] closureBytes = File.ReadAllBytes(proofPath);
        byte[] ledgerBytes = File.ReadAllBytes(ledgerPath);
        string closureSHA = FinalSHA(closureBytes);
        string ledgerSHA = FinalSHA(ledgerBytes);
        var closure = JObject.Parse(System.Text.Encoding.UTF8.GetString(closureBytes));
        var ledger = JObject.Parse(System.Text.Encoding.UTF8.GetString(ledgerBytes));
        var generation = (JObject)closure["generation"];
        var sources = (JArray)closure["processorSources"];
        if ((int?)ledger["schemaVersion"] != 1 || (string)ledger["stage"] != "final-fit-provisional" ||
            (bool?)ledger["immutableBatchVerified"] != true ||
            !(ledger["rows"] is JObject rows) || !(ledger["failures"] is JArray failureEntries) ||
            (bool?)ledger["invalidated"] != false || (string)generation["cachePath"] != cachePath ||
            (string)generation["stagedCatalogSnapshot"] != basePath ||
            (string)generation["cacheSHA"] != FinalFileSHA(cachePath) ||
            (string)generation["stagedCatalogSHA"] != FinalFileSHA(basePath))
            throw new InvalidDataException("A completed immutable fitting ledger is required");

        var pinned = generation["baseFiles"].Concat(sources).Select(entry =>
            new KeyValuePair<string, string>((string)entry["path"], (string)entry["sha"])).ToArray();
        VerifyFinalPinnedBytes(pinned);
        var failures = failureEntries.Select(f => (string)f["id"]).ToHashSet(StringComparer.Ordinal);
        var modelIds = preparation.Properties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        // A checkpoint I/O failure can appear beside a saved row. It still
        // requires an explicit exclusion and never enters selectedRows.
        if (modelIds.Count != 183 || failures.Count != failureEntries.Count ||
            !modelIds.SetEquals(rows.Properties().Select(r => r.Name).Concat(failures)) ||
            failures.Any(id => !exclusions.ContainsKey(id)))
            throw new InvalidDataException("Every included Modern model must have completed strict fitting");

        var baseCatalog = JObject.Parse(File.ReadAllText(basePath));
        var baseWearRows = ((JArray)baseCatalog["records"]).Where(r => (string)r["type"] == "wear").ToArray();
        if (baseWearRows.Length != 660 ||
            baseWearRows.Select(r => (string)r["id"]).Distinct(StringComparer.Ordinal).Count() != 660)
            throw new InvalidDataException("The complete immutable wardrobe variant inventory is required");
        var baseWear = baseWearRows.ToLookup(r => (string)r["main"], StringComparer.Ordinal);
        var selectedRows = new JObject();
        var selectedRecords = new JArray();
        var excluded = new JArray();
        var bodyInputs = FittingUnderlayerPaths.Concat(new[] {
            PeopleRoot + "/Prefabs/Actors/Marta.prefab", PeopleRoot + "/Prefabs/Actors/Kshishtof.prefab"
        }).Distinct(StringComparer.Ordinal).ToDictionary(path => path,
            path => AssetDatabase.GetAssetDependencyHash(path).ToString(), StringComparer.Ordinal);

        foreach (var model in preparation.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var metric = (JObject)model.Value;
            string prefab = (string)metric["prepared"];
            var variants = baseWear[prefab].ToArray();
            if (variants.Length == 0)
                throw new InvalidDataException("No logical IDs for Modern wardrobe source " + model.Name);
            var ids = variants.Select(r => (string)r["id"]).ToHashSet(StringComparer.Ordinal);
            if (exclusions.TryGetValue(model.Name, out var policy))
            {
                var entry = (JObject)policy.DeepClone();
                entry["logicalIds"] = new JArray(ids.OrderBy(id => id, StringComparer.Ordinal));
                excluded.Add(entry);
                continue;
            }

            var row = rows[model.Name] as JObject;
            var dependencies = new Dictionary<string, string>(bodyInputs, StringComparer.Ordinal);
            foreach (string path in new[] { (string)metric["source"], prefab })
                dependencies[path] = AssetDatabase.GetAssetDependencyHash(path).ToString();
            var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefab);
            var settings = FinalSettings(basePrefab.GetComponent<HexLive.UnityPresentation.Wearing.Wear>());
            string expected = FinalJsonSHA(FinalProcessorInput(FinalFitVersion, metric, settings, sources, dependencies));
            if (!FinalRowMatches(row, expected, settings) ||
                (string)row["base"]?["rowSHA"] != FinalJsonSHA(metric) ||
                !ids.SetEquals(row["final"]["logicalIds"].Values<string>()))
                throw new InvalidDataException("Staged fit changed or lacks full proof for " + model.Name);
            selectedRows.Add(model.Name, row.DeepClone());
            foreach (var original in variants)
            {
                var record = (JObject)original.DeepClone();
                record["main"] = row["final"]["prefab"].DeepClone();
                record["metadata"]["finalFit"] = true;
                record["metadata"]["contentOrigin"] = "modern";
                selectedRecords.Add(record);
            }
            foreach (var map in row["final"]["maps"])
            {
                string path = (string)map["path"], name = Path.GetFileNameWithoutExtension(path);
                selectedRecords.Add(new JObject
                {
                    ["type"] = "config",
                    ["id"] = ("PaintMaps." + name).ToLowerInvariant(),
                    ["main"] = path,
                    ["metadata"] = new JObject
                    {
                        ["peopleCatalog"] = baseCatalog["peopleCatalog"].DeepClone(),
                        ["contentOrigin"] = "modern",
                        ["legacyWardrobe"] = true,
                        ["finalFit"] = true,
                        ["legacyResourcePath"] = "HexLive/PaintMaps/" + name,
                        ["legacyResourceFolder"] = "HexLive/PaintMaps"
                    }
                });
            }
        }

        int items = selectedRecords.Count(r => (string)r["type"] == "wear");
        int maps = selectedRecords.Count - items;
        if (items == 0 || maps != selectedRows.Count * 2 ||
            selectedRecords.Select(r => (string)r["type"] + "/" + (string)r["id"]).Distinct(StringComparer.Ordinal).Count() != selectedRecords.Count)
            throw new InvalidDataException("Invalid curated Modern wardrobe inventory");
        var catalog = (JObject)baseCatalog.DeepClone();
        catalog["records"] = selectedRecords;
        catalog["contentOrigin"] = "modern";
        catalog["stage"] = "prepared-only";
        catalog["gameReady"] = false;
        catalog["bundleBuildAuthorized"] = false;
        catalog["finalFit"] = true;
        catalog["finalFitManifest"] = ModernReleaseFitPath;
        catalog["releaseSelectionSHA256"] = policySHA;
        string catalogText = catalog.ToString();
        var manifest = new JObject
        {
            ["schemaVersion"] = 1,
            ["stage"] = "final-fit-prepared-only",
            ["passed"] = true,
            ["catalogPromoted"] = true,
            ["releaseSelectionSHA256"] = policySHA,
            ["excludedModels"] = excluded,
            ["completedLedgerSHA256"] = ledgerSHA,
            ["fitInputClosureSHA256"] = closureSHA,
            ["processor"] = new JObject { ["version"] = FinalFitVersion, ["sources"] = sources.DeepClone() },
            ["baseGeneration"] = generation.DeepClone(),
            ["editorValidationContext"] = new JObject
            {
                ["unityVersion"] = Application.unityVersion,
                ["activeBuildTarget"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
                ["dependencyHashesPlatformScoped"] = true,
                ["futureWebGLRequiresExplicitPlatformScopedValidation"] = true
            },
            ["rows"] = selectedRows,
            ["finalCatalog"] = new JObject
            {
                ["path"] = ModernReleaseCatalogPath,
                ["sha"] = FinalSHA(System.Text.Encoding.UTF8.GetBytes(catalogText))
            },
            ["counts"] = new JObject { ["models"] = selectedRows.Count, ["logicalIds"] = items, ["meshes"] = maps, ["maps"] = maps },
            ["bundlesBuilt"] = false,
            ["productionChanged"] = false
        };
        VerifyFinalPinnedBytes(pinned);
        if (FinalFileSHA(ModernReleaseSelectionPath) != policySHA)
            throw new InvalidDataException("Modern wardrobe exclusions changed during curation");
        if (FinalFileSHA(ledgerPath) != ledgerSHA || FinalFileSHA(proofPath) != closureSHA)
            throw new InvalidDataException("The completed geometry inputs changed during curation");
        // The build accepts only a matching pair. An interruption between writes
        // leaves neither an incomplete package nor a publishable catalog.
        AtomicFinalText(ModernReleaseFitPath, manifest.ToString());
        AtomicFinalText(ModernReleaseCatalogPath, catalogText);
        Debug.Log($"MODERN_RELEASE_SELECTED: {selectedRows.Count} models, {items} wear, {maps} maps, {excluded.Count} excluded");
    }
}
}
#endif
