#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Wearing;
using HexLive.UnityPresentation.Wearing.Garments;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
namespace HexLive.UnityDebug.Editor
{
public static partial class LegacyWebGLWardrobePreparation
{
    static bool UsesFinalFitCatalog(JObject catalog)=>catalog["records"].Any(record=>
        (string)record["type"]=="wear"&&((string)record["main"]??"").Contains("/Prefabs/FinalFit/"));

    public static void ResumePreparedCollectionAndReview()
    {
        NormalizePreparedMeshReadability();
        var cache = JObject.Parse(File.ReadAllText(PreparedRoot + "/preparation.json"));
        var mismatches = new JArray();
        foreach (var entry in cache.Properties())
        {
            string source = (string)entry.Value["source"], prefab = (string)entry.Value["prepared"];
            bool inputMatches = (string)entry.Value["fingerprint"] == PrototypeInputFingerprint(source);
            bool artifactValid = CachedPrototypeValid(AssetDatabase.LoadAssetAtPath<GameObject>(prefab));
            if (!inputMatches || !artifactValid)
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(prefab);
                var wear = go != null ? go.GetComponent<Wear>() : null;
                var fits = new JArray();
                if (wear != null)
                {
                    var configs = new SerializedObject(wear).FindProperty("configs");
                    for (int i = 0; i < configs.arraySize; i++)
                    {
                        var fit = configs.GetArrayElementAtIndex(i);
                        var mesh = fit.FindPropertyRelative("mesh").objectReferenceValue as Mesh;
                        fits.Add(new JObject { ["actor"] = fit.FindPropertyRelative("actorName").intValue,
                            ["mesh"] = mesh == null ? "" : AssetDatabase.GetAssetPath(mesh),
                            ["readable"] = mesh != null && mesh.isReadable, ["vertices"] = mesh == null ? 0 : mesh.vertexCount,
                            ["submeshes"] = mesh == null ? 0 : mesh.subMeshCount });
                    }
                }
                var skins = go == null ? Array.Empty<SkinnedMeshRenderer>() : go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                var foreign = AssetDatabase.GetDependencies(prefab, true).Where(path =>
                    path.StartsWith(SourceRoot + "/", StringComparison.Ordinal) ||
                    path.StartsWith("Assets/HexLiveContent/RuntimeSource/Wear/", StringComparison.Ordinal) ||
                    path.StartsWith("Assets/ImportedActors/Wear/", StringComparison.Ordinal)).ToArray();
                mismatches.Add(new JObject { ["id"] = entry.Name, ["inputMatches"] = inputMatches, ["artifactValid"] = artifactValid,
                    ["prefab"] = prefab, ["wearPresent"] = wear != null, ["fits"] = fits,
                    ["supportsMarta"] = wear != null && wear.SupportsActor(ActorName.Marta),
                    ["supportsKshishtof"] = wear != null && wear.SupportsActor(ActorName.Kshishtof),
                    ["skinCount"] = skins.Length, ["meshRendererCount"] = go == null ? 0 : go.GetComponentsInChildren<MeshRenderer>(true).Length,
                    ["materials"] = new JArray(skins.SelectMany(s => s.sharedMaterials).Select(m => m == null ? "NULL" : AssetDatabase.GetAssetPath(m))),
                    ["foreignDependencies"] = new JArray(foreign) });
            }
        }
        Directory.CreateDirectory(OutputRoot);
        File.WriteAllText(OutputRoot + "/cache-reuse-preflight.json", new JObject {
            ["models"] = cache.Count, ["passed"] = cache.Count > 0 && mismatches.Count == 0,
            ["mismatches"] = mismatches, ["cacheFingerprintsModified"] = false }.ToString());
        if (cache.Count == 0 || mismatches.Count != 0)
            throw new InvalidOperationException("Prepared mesh inputs/artifacts need review before resuming: " + mismatches.Count);
        Debug.Log("LEGACY_CACHE_REUSE_VALID: " + cache.Count);
        FinalizeExistingPreparedCollectionAndReview();
    }

    public static void FinalizeExistingPreparedCollectionAndReview()
    {
        var gate = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("HexLive.Editor.ConstructionStageBuildGate", false))
            .FirstOrDefault(type => type != null)
            ?? throw new InvalidOperationException("Missing construction-stage acceptance gate");
        gate.GetMethod("ValidateAll", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).Invoke(null,null);
        PrepareFinalFitCollection();
        ValidateFinalPreparedCollectionAndReview();
    }

    public static void PrepareAllExportValidateAndReview()
    {
        // A final resume must retain its previous promoted catalog and owned
        // base generation. Re-running PrepareAll would reset records and active
        // Primal authoring assets before final rollback could protect them.
        if (File.Exists(PreparedRoot + "/catalog.json") && UsesFinalFitCatalog(JObject.Parse(File.ReadAllText(PreparedRoot + "/catalog.json"))))
        {
            FinalizeExistingPreparedCollectionAndReview();
            return;
        }
        ExportSimData.Export();
        PrepareAll();
        NormalizePreparedMeshReadability();
        FinalizeExistingPreparedCollectionAndReview();
    }

    // Offline acceptance only. Does not activate the staged collection, modify
    // imported assets, build bundles, contact a registry or publish content.
    // Set HEXLIVE_LEGACY_REVIEW_GALLERY=0 for validation without graphics.
    public static void ValidatePreparedCollectionAndReview()
    {
        if (UsesFinalFitCatalog(JObject.Parse(File.ReadAllText(PreparedRoot + "/catalog.json"))))
        {
            ValidateFinalPreparedCollectionAndReview();
            return;
        }
        Directory.CreateDirectory(OutputRoot);
        var errors = new JArray(); var dependencyRows = new JArray(); var prototypeRows = new JArray();
        var report = new JObject { ["passed"] = false, ["utc"] = DateTime.UtcNow.ToString("O"),
            ["errors"] = errors, ["dependencies"] = dependencyRows, ["prototypes"] = prototypeRows,
            ["bundlesBuilt"] = false, ["productionChanged"] = false, ["assetsModified"] = false };
        string reportPath = OutputRoot + "/prepared-collection-validation.json";
        string activeHash = FileHash(PeopleRoot + "/catalog.json");
        string stageHash = FileHash(PreparedRoot + "/catalog.json");
        var protectedHashes = new Dictionary<string,string>(StringComparer.Ordinal) {
            [PeopleRoot + "/catalog.json"] = activeHash, [PreparedRoot + "/catalog.json"] = stageHash,
            [PreparedRoot + "/preparation.json"] = FileHash(PreparedRoot + "/preparation.json") };
        void Check(bool condition, string message) { if (!condition) errors.Add(message); }
        try
        {
            var active = JObject.Parse(File.ReadAllText(PeopleRoot + "/catalog.json"));
            var stage = JObject.Parse(File.ReadAllText(PreparedRoot + "/catalog.json"));
            var completed = JObject.Parse(File.ReadAllText(OutputRoot + "/prepared-all.json"));
            var cache = JObject.Parse(File.ReadAllText(PreparedRoot + "/preparation.json"));
            var records = ((JArray)stage["records"]).ToArray();
            var wearRows = records.Where(r => (string)r["type"] == "wear").ToArray();
            var mapRows = records.Where(r => (string)r["type"] == "config").ToArray();
            var activeIds = new HashSet<string>(active["records"].Where(r => (string)r["type"] == "wear").Select(r => (string)r["id"]), StringComparer.Ordinal);
            var defaults = GarmentLibrary.Defaults.ToDictionary(g => g.Id, StringComparer.Ordinal);
            var expected = new HashSet<string>(defaults.Keys.Where(id => !activeIds.Contains(id)), StringComparer.Ordinal);
            var actual = new HashSet<string>(wearRows.Select(r => (string)r["id"]), StringComparer.Ordinal);
            Check(expected.Count == 660, "Expected logical baseline changed: " + expected.Count + " rather than660");
            Check(wearRows.Length == actual.Count, "Duplicate staged wear IDs");
            Check(records.Length == records.Select(r => (string)r["id"]).Distinct(StringComparer.Ordinal).Count(), "Duplicate staged record IDs");
            Check(actual.SetEquals(expected), "Staged logical IDs differ from Defaults minus active wear");
            Check((string)stage["stage"] == "prepared-only" && (bool?)stage["gameReady"] == false &&
                (bool?)stage["bundleBuildAuthorized"] == false, "Staged collection authorization flags changed");
            Check((string)stage["peopleCatalog"] == (string)active["peopleCatalog"], "People catalog identity differs");
            Check((bool?)completed["activeCatalogUnchanged"] == true && (bool?)completed["bundlesBuilt"] == false &&
                (bool?)completed["productionChanged"] == false && (string)completed["stagedCatalog"] == PreparedRoot + "/catalog.json", "Full preparation provenance is missing/incorrect");
            report["coverage"] = new JObject { ["activeWearIds"] = activeIds.Count, ["expectedLegacyIds"] = expected.Count,
                ["actualLegacyIds"] = actual.Count, ["missingIds"] = new JArray(expected.Except(actual).OrderBy(v => v,StringComparer.Ordinal)),
                ["unexpectedIds"] = new JArray(actual.Except(expected).OrderBy(v => v,StringComparer.Ordinal)),
                ["unresolvedAuthoringDefinitions"] = completed["unresolvedAuthoringDefinitions"]?.DeepClone() };
            Check((string)completed["algorithm"] == Algorithm, "Full report generation algorithm differs from current pipeline");
            report["sourceGenerationAlgorithm"] = completed["algorithm"]?.DeepClone();
            report["catalogSha256Base64"] = new JObject { ["active"] = activeHash, ["staged"] = stageHash };
            var roots = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in records)
            {
                string path = (string)row["main"];
                Check((bool?)row["metadata"]?["legacyWardrobe"] == true, "Missing legacy metadata " + row["id"]);
                Check((string)row["metadata"]?["peopleCatalog"] == (string)stage["peopleCatalog"], "Wrong people metadata " + row["id"]);
                Check(!string.IsNullOrEmpty(path) && path.StartsWith(PreparedRoot + "/",StringComparison.Ordinal), "Record outside staged root " + row["id"]);
                if (!string.IsNullOrEmpty(path)) roots.Add(path);
            }
            foreach (var row in wearRows)
            {
                string id = (string)row["id"], path = (string)row["definition"];
                Check(!string.IsNullOrEmpty(path) && path.StartsWith(PreparedRoot + "/Definitions/",StringComparison.Ordinal), "Unowned definition " + id);
                if (string.IsNullOrEmpty(path)) continue;
                roots.Add(path);
                var definition = AssetDatabase.LoadAssetAtPath<GarmentDefinition>(path);
                Check(definition != null && definition.id == id, "Missing/wrong definition " + id);
                if (definition != null && defaults.TryGetValue(id, out var original))
                    Check(definition.retired == original.Retired && (bool?)row["metadata"]?["simulation"]?["retired"] == original.Retired, "Retired state changed " + id);
                Check((string)row["metadata"]?["sex"] == "Any" && (string)row["metadata"]?["simulation"]?["sex"] == "Any", "Both-body metadata absent " + id);
            }
            var prefabPaths = wearRows.Select(r => (string)r["main"]).Distinct(StringComparer.Ordinal).OrderBy(v => v,StringComparer.Ordinal).ToArray();
            var fullRows = ((JArray)completed["prototypes"]).ToArray();
            Check(fullRows.Select(r => (string)r["prepared"]).Distinct(StringComparer.Ordinal).Count() == fullRows.Length, "Duplicate full-run prototype rows");
            Check(new HashSet<string>(fullRows.Select(r => (string)r["prepared"]),StringComparer.Ordinal).SetEquals(prefabPaths), "Full-run prototype coverage differs from staged prefabs");
            Check(prefabPaths.Length == 183, "Expected183 unique legacy prefabs, actual " + prefabPaths.Length);
            var meshes = new HashSet<Mesh>();
            var modes = new Dictionary<string,int>(StringComparer.Ordinal);
            foreach (string path in prefabPaths)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Check(CachedPrototypeValid(prefab), "Actual prepared asset validation failed " + path);
                var metric = fullRows.SingleOrDefault(r => (string)r["prepared"] == path);
                if (metric == null || prefab == null) continue;
                string id = (string)metric["id"];
                Check((bool?)metric["sourceSettingsUnchanged"] == true, "Source settings provenance missing " + id);
                var actualWear = prefab.GetComponent<Wear>();
                // SerializedProperty floats are Single, while persisted JSON reads as Double.
                // Compare their exact persisted JSON representation, without a tolerance.
                var actualSettings = actualWear == null ? null :
                    JObject.Parse(Settings(actualWear).ToString(Newtonsoft.Json.Formatting.None));
                bool settingsMatch = actualSettings != null && JToken.DeepEquals(actualSettings, metric["settings"]);
                Check(settingsMatch, "Actual prepared settings differ from reviewed receipt " + id +
                    "; review and explicitly renew the receipt before accepting manual changes");
                string sourcePath = (string)metric["source"];
                bool sourceExists = File.Exists(sourcePath);
                Check(sourceExists, "Native source path missing " + id);
                string currentSourceFingerprint = sourceExists ? PrototypeInputFingerprint(sourcePath) : null;
                bool sourceFingerprintMatches = currentSourceFingerprint != null &&
                    string.Equals(currentSourceFingerprint, (string)metric["fingerprint"], StringComparison.Ordinal);
                Check(sourceFingerprintMatches, "Current source fingerprint differs from preparation receipt " + id);
                Check(JToken.DeepEquals(cache[id], metric), "Cache/full-run row mismatch " + id);
                var quality = metric["quality"]; string mode = (string)quality?["mode"];
                bool qualityPassed = false;
                if (mode == "adaptive")
                {
                    var last = (quality["attempts"] as JArray)?.LastOrDefault();
                    qualityPassed = (bool?)last?["passed"] == true;
                    foreach (string direction in new[] { "sourceToCandidate", "candidateToSource" })
                        qualityPassed &= (float?)last?[direction]?["max"] <= .003f && (float?)last?[direction]?["p95"] <= .0005f;
                }
                else if (mode == "unchanged" || mode == "source-preserved")
                    qualityPassed = (long?)quality["actualTriangles"] == (long?)quality["inputTriangles"] && (long?)quality["inputTriangles"] > 0;
                Check(qualityPassed, "Invalid geometric error proof " + id);
                if (mode != null) modes[mode] = modes.TryGetValue(mode,out int count) ? count+1 : 1;
                var configs = new SerializedObject(prefab.GetComponent<Wear>()).FindProperty("configs");
                var fits = new JArray();
                for (int i = 0; i < configs.arraySize; i++)
                {
                    var config = configs.GetArrayElementAtIndex(i); var mesh = config.FindPropertyRelative("mesh").objectReferenceValue as Mesh;
                    if (mesh == null) continue; meshes.Add(mesh);
                    long tris = TriangleCount(mesh);
                    Check(tris == (long?)metric["trianglesPerBody"], "Mesh/report triangle mismatch " + id);
                    fits.Add(new JObject { ["actor"] = ((ActorName)config.FindPropertyRelative("actorName").intValue).ToString(),
                        ["mesh"] = AssetDatabase.GetAssetPath(mesh), ["vertices"] = mesh.vertexCount, ["triangles"] = tris,
                        ["scale"] = config.FindPropertyRelative("scale").floatValue, ["heightOffset"] = config.FindPropertyRelative("heightOffset").floatValue });
                }
                prototypeRows.Add(new JObject { ["id"] = id, ["source"] = metric["source"]?.DeepClone(), ["prepared"] = path,
                    ["logicalIds"] = new JArray(wearRows.Where(r => (string)r["main"] == path).Select(r => (string)r["id"])),
                    ["sourceFingerprint"] = metric["fingerprint"]?.DeepClone(), ["sourceSettingsUnchanged"] = metric["sourceSettingsUnchanged"]?.DeepClone(),
                    ["currentSourceFingerprint"] = currentSourceFingerprint, ["sourceFingerprintMatches"] = sourceFingerprintMatches,
                    ["settings"] = metric["settings"]?.DeepClone(), ["actualSettings"] = actualSettings, ["settingsMatch"] = settingsMatch,
                    ["fits"] = fits, ["quality"] = quality?.DeepClone() });
            }
            var maps = mapRows.Select(r => AssetDatabase.LoadAssetAtPath<PaintPointMap>((string)r["main"])).ToArray();
            Check(maps.All(m => m != null), "Missing imported paint map");
            foreach (var mesh in meshes)
                Check(maps.Any(map => map != null && map.MeshName == mesh.name && map.VertexCount == mesh.vertexCount), "No matching paint map " + mesh.name);
            foreach (var map in maps.Where(m => m != null))
                Check(meshes.Any(mesh => map.MeshName == mesh.name && map.VertexCount == mesh.vertexCount), "Orphan/stale paint map " + AssetDatabase.GetAssetPath(map));
            foreach (string path in roots.OrderBy(v => v,StringComparer.Ordinal))
            {
                Check(AssetDatabase.LoadMainAssetAtPath(path) != null, "Missing imported root " + path);
                string[] deps = AssetDatabase.GetDependencies(path,true).OrderBy(v => v,StringComparer.Ordinal).ToArray();
                var forbidden = deps.Where(dep => dep.StartsWith(SourceRoot + "/",StringComparison.Ordinal) ||
                    dep.StartsWith("Assets/HexLiveContent/RuntimeSource/Wear/",StringComparison.Ordinal) ||
                    dep.StartsWith("Assets/ImportedActors/Wear/",StringComparison.Ordinal) ||
                    dep.StartsWith("Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/",StringComparison.Ordinal)).ToArray();
                Check(forbidden.Length == 0, "Native authoring dependency in " + path + ": " + string.Join(",",forbidden));
                dependencyRows.Add(new JObject { ["root"] = path, ["hash"] = AssetDatabase.GetAssetDependencyHash(path).ToString(), ["paths"] = new JArray(deps), ["forbidden"] = new JArray(forbidden) });
            }
            report["coverage"]["uniquePrefabs"] = prefabPaths.Length; report["coverage"]["definitions"] = wearRows.Length;
            report["coverage"]["paintMaps"] = maps.Length; report["coverage"]["uniqueMeshes"] = meshes.Count;
            report["errorModes"] = JObject.FromObject(modes);
            if (errors.Count != 0) throw new InvalidOperationException("Prepared collection validation errors: " + errors.Count);
            if (Environment.GetEnvironmentVariable("HEXLIVE_LEGACY_REVIEW_GALLERY") != "0")
                report["gallery"] = RenderPreparedCollectionGallery(prefabPaths,wearRows);
            report["passed"] = true;
        }
        catch (Exception error) { report["exception"] = error.ToString(); throw; }
        finally
        {
            foreach (var pair in protectedHashes) Check(FileHash(pair.Key) == pair.Value, "Validation changed protected input " + pair.Key);
            if (errors.Count != 0) report["passed"] = false;
            File.WriteAllText(reportPath, report.ToString());
        }
        if (!(bool)report["passed"]) throw new InvalidOperationException("Validation changed a protected input");
        Debug.Log("LEGACY_PREPARED_COLLECTION_VALID: " + reportPath);
    }

    static JObject RenderPreparedCollectionGallery(string[] paths, JToken[] wearRows)
    {
        const int cell = 192, pageRows = 18, columns = 4;
        string directory = OutputRoot + "/collection-review-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff");
        if (Directory.Exists(directory)) throw new IOException("Gallery output already exists");
        Directory.CreateDirectory(directory);
        var mapping = new JArray();
        var variantsByPrefab = wearRows.ToLookup(r => (string)r["main"], StringComparer.Ordinal);
        var oldActive = RenderTexture.active;
        try
        {
            for (int start = 0; start < paths.Length; start += pageRows)
            {
                int count = Math.Min(pageRows, paths.Length-start), page = start/pageRows;
                string pagePath = directory + "/page-" + page.ToString("D2") + ".png";
                var atlas = new Texture2D(columns*cell,count*cell,TextureFormat.RGB24,false);
                try
                {
                    for (int row = 0; row < count; row++)
                    {
                        string path = paths[start+row];
                        var variants = variantsByPrefab[path];
                        var record = variants.OrderBy(r => (string)r["id"], StringComparer.Ordinal).FirstOrDefault();
                        if (record == null)
                            throw new InvalidOperationException("Missing selected wardrobe variants for " + path);
                        RenderPrepared(path, record: record); // Four real production-factory previews.
                        Debug.Log("LEGACY_GALLERY_MODEL " + (start+row+1) + "/" + paths.Length + " " + path);
                        string key = Path.GetFileNameWithoutExtension(path);
                        for (int col = 0; col < columns; col++)
                        {
                            string actor = col < 2 ? "Marta" : "Kshishtof", view = col%2 == 0 ? "front" : "back";
                            string png = OutputRoot + "/review/" + key + "_" + actor + "_" + view + ".png";
                            var image = new Texture2D(2,2,TextureFormat.RGB24,false);
                            var thumb = new Texture2D(cell,cell,TextureFormat.RGB24,false);
                            var rt = RenderTexture.GetTemporary(cell,cell,0,RenderTextureFormat.ARGB32);
                            try
                            {
                                if (!image.LoadImage(File.ReadAllBytes(png))) throw new IOException("Invalid real preview " + png);
                                Graphics.Blit(image,rt); RenderTexture.active = rt;
                                thumb.ReadPixels(new Rect(0,0,cell,cell),0,0); thumb.Apply();
                                atlas.SetPixels32(col*cell,(count-1-row)*cell,cell,cell,thumb.GetPixels32());
                                string copy = directory + "/" + Path.GetFileName(png); File.Copy(png,copy,false);
                                mapping.Add(new JObject { ["index"] = start+row, ["page"] = pagePath, ["rowFromTop"] = row,
                                    ["column"] = col, ["rectFromTop"] = new JArray(col*cell,row*cell,cell,cell), ["prefab"] = path,
                                    ["logicalIds"] = new JArray(variants.Select(r => (string)r["id"])),
                                    ["actor"] = actor, ["view"] = view, ["originalPng"] = copy, ["pngSha256Base64"] = FileHash(copy) });
                            }
                            finally { RenderTexture.active = oldActive; RenderTexture.ReleaseTemporary(rt); Object.DestroyImmediate(image); Object.DestroyImmediate(thumb); }
                        }
                    }
                    atlas.Apply(); File.WriteAllBytes(pagePath,atlas.EncodeToPNG());
                }
                finally { Object.DestroyImmediate(atlas); }
            }
        }
        finally { RenderTexture.active = oldActive; }
        var result = new JObject { ["directory"] = directory, ["models"] = paths.Length, ["viewsPerModel"] = 4,
            ["renderedPngs"] = mapping.Count, ["atlasPages"] = (paths.Length+pageRows-1)/pageRows, ["cellPixels"] = cell,
            ["rowsPerPage"] = pageRows, ["columns"] = columns, ["mapping"] = mapping };
        File.WriteAllText(directory + "/gallery.json",result.ToString()); return result;
    }
}
}
#endif
