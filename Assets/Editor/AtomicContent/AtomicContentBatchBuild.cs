#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HexLive.UnityPresentation.Wearing.Garments;
using HexLive.UnityPresentation.Config;
using HexLive.UnityPresentation.Environment;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// §152 one invocation = one logical object = one self-contained bundle.
/// This deliberately does not reference Addressables editor APIs.
/// </summary>
public static class AtomicContentBatchBuild
{
    private static void ValidateWindowsReferences(BuildTarget target)
    {
        if (target != BuildTarget.StandaloneWindows64) return;
        var backend = PlayerSettings.GetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone);
        var variant = backend == ScriptingImplementation.IL2CPP ? "il2cpp" : "mono";
        var reference = Path.Combine(EditorApplication.applicationContentsPath, "PlaybackEngines",
            "WindowsStandaloneSupport", "Variations", variant, "Managed", "UnityEngine.CoreModule.dll");
        if (!File.Exists(reference))
            throw new UnityEditor.Build.BuildFailedException(
                $"Missing Windows {variant} player references for Unity {Application.unityVersion}: {reference}. " +
                "Install the matching official Windows support module. Do not fall back to Editor DLLs or inject safety defines.");
        Debug.Log("[AtomicContent] Windows player reference: " + reference);
    }

    // Fast toolchain verification without building/publishing content. Uses
    // the same unchecked compilation mode as the native bundle TypeDB path.
    public static void ValidateWindowsScriptCompilation()
    {
        ValidateWindowsReferences(BuildTarget.StandaloneWindows64);
        var output = Path.GetFullPath(Required(Environment.GetCommandLineArgs(), "-content-compile-output"));
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidOperationException("Compile proof output must be new: " + output);
        Directory.CreateDirectory(output);
        var result = UnityEditor.Build.Player.PlayerBuildInterface.CompilePlayerScripts(
            new UnityEditor.Build.Player.ScriptCompilationSettings
            {
                group = BuildTargetGroup.Standalone,
                target = BuildTarget.StandaloneWindows64,
                options = UnityEditor.Build.Player.ScriptCompilationOptions.None
            }, output);
        if (result.assemblies == null || !result.assemblies.Any(path => Path.GetFileName(path) == "Unity.Collections.dll"))
            throw new UnityEditor.Build.BuildFailedException("Windows script compilation produced no Collections assembly.");
        Debug.Log($"[AtomicContent] Windows script compilation PASS: {result.assemblies.Count} assemblies, no extra defines.");
    }
    public const string RuntimeProfile = "unity6000-content1";
    private const string PayloadName = "payload.bundle";
    private const string WearDefinitions =
        "Assets/HexLive/UnityPresentation/Wearing/Garments/Assets";
    private const string WearRoot = "Assets/HexLiveContent/Wear";
    private const string IconRoot = "Assets/HexLiveContent/Icons";
    private const string DescriptorRoot = "Assets/AtomicContent";
    private const string GeneratedMetadataRoot = "Assets/AtomicContent/Generated";
    private const string RuntimeSourceRoot = "Assets/HexLiveContent/RuntimeSource";
    private static string _temporaryMetadata;
    private const string PeopleCatalogPath = "Assets/HexLiveContent/People/catalog.json";
    private const string StagedWardrobeRoot = "Assets/HexLiveContent/People/Wardrobe/";
    // Explicit authoring opt-in. Merely importing new people never switches a live catalog.
    private static bool UsePeopleCatalog => string.Equals(
        Optional(Environment.GetCommandLineArgs(), "-content-people-catalog"), "primal-v1", StringComparison.Ordinal);

    private static JArray PeopleRecords()
    {
        var catalog = JObject.Parse(File.ReadAllText(PeopleCatalogPath));
        if ((string)catalog["peopleCatalog"] != "primal-v1" ||
            (string)catalog["stage"] == "prepared-only" ||
            !HasLiveOrigin(catalog["contentOrigin"]))
            throw new InvalidOperationException("The live build requires the Primal People catalog");
        var records = (JArray)catalog["records"];
        foreach (var row in records)
        {
            var metadata = row["metadata"];
            if (!HasLiveOrigin(metadata?["contentOrigin"]) ||
                JToken.DeepEquals(metadata?["legacyWardrobe"], new JValue(true)) ||
                IsStagedWardrobePath((string)row["main"]) ||
                IsStagedWardrobePath((string)row["definition"]))
                throw new InvalidOperationException($"Staged or unsupported People record: {row["type"]}/{row["id"]}");
        }
        return records;
    }

    private static bool HasLiveOrigin(JToken origin) =>
        origin == null || (origin.Type == JTokenType.String && (string)origin == "primal");

    private static bool IsStagedWardrobePath(string path) =>
        path != null && path.StartsWith(StagedWardrobeRoot, StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacyPeopleMap(string path)
    {
        if (!path.Contains("/PaintMaps/", StringComparison.Ordinal)) return false;
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.StartsWith("garment_", StringComparison.Ordinal)) return true;
        return Enum.GetNames(typeof(HexLive.UnityPresentation.Wearing.ActorName)).Any(actor =>
            name.StartsWith("skin_" + actor, StringComparison.Ordinal) ||
            name.StartsWith("skinpos_" + actor, StringComparison.Ordinal) ||
            name.StartsWith("skinnrm_" + actor, StringComparison.Ordinal));
    }

    private static BuildInputs ResolvePeople(JToken row)
    {
        string type = (string)row["type"], id = (string)row["id"];
        var meta = (JObject)row["metadata"].DeepClone();
        var result = new BuildInputs { Main = (string)row["main"], DisplayName = id, ExtraMetadata = meta,
            Icon = IconBearingTypes.Contains(type) ? $"{IconRoot}/{type}.{id}.png" : null };
        if (type == "wear")
        {
            result.Icon = $"{IconRoot}/{id}.png";
            result.Metadata = (string)row["definition"];
            var def = AssetDatabase.LoadAssetAtPath<GarmentDefinition>(result.Metadata);
            if (def == null) throw new InvalidOperationException("Missing people garment: " + id);
            result.DisplayName = def.displayName; result.ArtId = def.ArtId;
            result.Category = def.category.ToString(); result.IsWear = true;
            result.State = def.retired ? "retired" : "active";
            result.Layer = def.layer.ToString(); result.Covers = def.covers.Select(p => p.ToString()).ToArray();
            result.Slots = meta["slots"].Values<string>().ToArray();
            result.Warmth = def.warmth; result.Armor = def.armor; result.ThermalDelta = def.thermalDelta;
            result.DressDurationTicks = def.dressDurationTicks; result.Capacity = def.capacity;
            result.Sex = (string)meta["sex"];
            // These are emitted from BuildInputs; avoid duplicate JSON keys.
            meta.Remove("artId"); meta.Remove("sex"); meta.Remove("slots");
        }
        else result.Metadata = CreateGeneratedMetadata(type, id, meta);
        return ResolveOwnerIcon(type, id, result);
    }

    // Preparation/dry run only. Does not invoke BuildObject or BuildPipeline.
    public static void AuditPeopleCatalog()
    {
        var rows = PeopleRecords();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            string key = (string)row["type"] + "/" + (string)row["id"];
            if (!seen.Add(key)) throw new InvalidOperationException("Duplicate people record " + key);
            ValidateAsset((string)row["main"], key);
            if (row["definition"] != null) ValidateAsset((string)row["definition"], key + " metadata");
        }
        Debug.Log("PEOPLE_CATALOG_DRY_RUN: " + rows.Count + " records; no bundles built");
    }


    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "wear", "actor", "hair", "prosthetic", "object", "building",
        "mob", "vfx", "audio", "config",
    };

    private static readonly HashSet<string> IconBearingTypes = new(StringComparer.Ordinal)
    {
        "wear", "actor", "hair", "prosthetic", "object", "building", "mob",
    };

    [Serializable]
    private sealed class Descriptor
    {
        public string main;
        public string icon;
        public string displayName;
        public ContentEntry[] entries;
    }

    [Serializable]
    private sealed class ContentEntry
    {
        public string name;
        public string asset;
    }

    private sealed class BuildInputs
    {
        public string Main = string.Empty;
        public string State = "active";
        public string Metadata = string.Empty;
        public string Icon;
        public string DisplayName = string.Empty;
        public string ArtId = string.Empty;
        public string Category = string.Empty;
        public bool IsWear;
        public string Layer = string.Empty;
        public string[] Covers = Array.Empty<string>();
        public string[] Slots = Array.Empty<string>();
        public float Warmth;
        public float Armor;
        public float ThermalDelta;
        public int DressDurationTicks;
        public int Capacity;
        public string Sex = string.Empty;
        public ContentEntry[] Entries = Array.Empty<ContentEntry>();
        public JObject ExtraMetadata = new();
    }

    private sealed class Recipe
    {
        public string Type;
        public string Id;
        public Func<BuildInputs> Inputs;
    }

    public static void Build()
    {
        Run(buildAll: false);
    }

    public static void BuildAll()
    {
        Run(buildAll: true);
    }

    public static void BuildPreparedWebGL()
    {
        if (!UsePeopleCatalog) throw new InvalidOperationException("Prepared WebGL build requires primal-v1");
        ValidateTarget("WebGL");
        AuditAll();
        BuildAll();
    }

    /// <summary>Builds the §176 world props and the two edited Primal owners.</summary>
    public static void BuildPortalWebGL() => Run(buildAll: true, portalOnly: true);

    public static void BuildStagedModernWebGL()
    {
        var arguments = Environment.GetCommandLineArgs();
        if (Required(arguments, "-content-platform") != "WebGL" || UsePeopleCatalog)
            throw new InvalidOperationException("Modern wardrobe staging requires a dedicated WebGL build");
        ValidateTarget("WebGL");

        const string catalogPath = StagedWardrobeRoot + "release-catalog.json";
        const string manifestPath = StagedWardrobeRoot + "release-fit.json";
        const string selectionPath = StagedWardrobeRoot + "release-selection.json";
        const string baseCatalogPath = StagedWardrobeRoot + "base-catalog.json";
        const string preparationPath = StagedWardrobeRoot + "preparation.json";
        var inputHashes = new[] { catalogPath, manifestPath, selectionPath, baseCatalogPath, preparationPath, PeopleCatalogPath }
            .ToDictionary(path => path, Hash, StringComparer.Ordinal);
        var catalog = JObject.Parse(File.ReadAllText(catalogPath));
        var manifest = JObject.Parse(File.ReadAllText(manifestPath));
        var selection = JObject.Parse(File.ReadAllText(selectionPath));
        var preparation = JObject.Parse(File.ReadAllText(preparationPath));
        var baseCatalog = JObject.Parse(File.ReadAllText(baseCatalogPath));
        var generation = manifest["baseGeneration"];
        if ((string)catalog["contentOrigin"] != "modern" || (string)catalog["stage"] != "prepared-only" ||
            (bool?)catalog["gameReady"] != false || (bool?)catalog["bundleBuildAuthorized"] != false ||
            (bool?)catalog["finalFit"] != true || (string)catalog["finalFitManifest"] != manifestPath ||
            (int?)manifest["schemaVersion"] != 1 || (string)manifest["stage"] != "final-fit-prepared-only" ||
            (bool?)manifest["passed"] != true || (bool?)manifest["catalogPromoted"] != true ||
            (string)manifest["finalCatalog"]?["path"] != catalogPath ||
            (string)manifest["finalCatalog"]?["sha"] != inputHashes[catalogPath] ||
            (string)manifest["releaseSelectionSHA256"] != inputHashes[selectionPath] ||
            (string)catalog["releaseSelectionSHA256"] != inputHashes[selectionPath] ||
            (string)generation?["cachePath"] != preparationPath ||
            (string)generation?["cacheSHA"] != inputHashes[preparationPath] ||
            (string)generation?["stagedCatalogSnapshot"] != baseCatalogPath ||
            (string)generation?["stagedCatalogSHA"] != inputHashes[baseCatalogPath] ||
            !(catalog["records"] is JArray records) || !(manifest["rows"] is JObject fits))
            throw new InvalidOperationException("The matching curated Modern wardrobe, fit, and exclusion policy are required");

        var expected = CuratedModernRecords(manifest, selection, preparation, baseCatalog, fits);
        var live = new HashSet<string>(PeopleRecords().Select(RecordKey), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in records)
        {
            string key = RecordKey(row);
            if (!seen.Add(key) || live.Contains(key) || !expected.TryGetValue(key, out var verified) ||
                !JToken.DeepEquals(row, verified))
                throw new InvalidOperationException("Invalid curated wardrobe record " + key);

            roots.Add((string)row["main"]);
            if ((string)row["type"] != "wear") continue;
            string definition = (string)row["definition"];
            string icon = $"{IconRoot}/{row["id"]}.png";
            if (AssetDatabase.LoadAssetAtPath<Sprite>(icon) == null ||
                AssetDatabase.LoadAssetAtPath<GarmentDefinition>(definition) == null)
                throw new InvalidOperationException("Missing staged wardrobe icon or definition: " + key);
            roots.Add(definition);
            roots.Add(icon);
        }
        if (!seen.SetEquals(expected.Keys))
            throw new InvalidOperationException("Incomplete curated wardrobe catalog");
        foreach (var path in AssetDatabase.GetDependencies(roots.ToArray(), true))
            if (path.StartsWith("Assets/", StringComparison.Ordinal) && !File.Exists(path) && !AssetDatabase.IsValidFolder(path))
                throw new InvalidOperationException("Missing imported WebGL wardrobe dependency: " + path);

        RequireModernBuildInputs(inputHashes);
        Run(buildAll: true, stagedWardrobe: records, modernInputHashes: inputHashes);
    }

    private static Dictionary<string, JObject> CuratedModernRecords(
        JObject manifest, JObject selection, JObject preparation, JObject baseCatalog, JObject fits)
    {
        if ((int?)selection["schemaVersion"] != 1 || !(selection["excludedModels"] is JArray policyRows) ||
            !(manifest["excludedModels"] is JArray fitExclusions) || !(baseCatalog["records"] is JArray baseRecords))
            throw new InvalidOperationException("Invalid curated wardrobe selection or base snapshot");

        var policyById = ModernExclusionsById(policyRows);
        var excludedById = ModernExclusionsById(fitExclusions);
        var excludedSources = policyById.Values.Select(row => (string)row["source"]).ToHashSet(StringComparer.Ordinal);
        var allModels = preparation.Properties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (allModels.Count != 183 || fits.Count == 0 || fits.Count + policyById.Count != allModels.Count ||
            !allModels.SetEquals(fits.Properties().Select(property => property.Name).Concat(policyById.Keys)) ||
            !policyById.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(excludedById.Keys))
            throw new InvalidOperationException("Curated model and exclusion inventory differs from the immutable base");

        var baseWear = baseRecords.OfType<JObject>().Where(row => (string)row["type"] == "wear").ToArray();
        var byPrefab = baseWear.ToLookup(row => (string)row["main"], StringComparer.Ordinal);
        var baseIds = baseWear.Select(row => (string)row["id"]).ToHashSet(StringComparer.Ordinal);
        if (baseWear.Length != 660 || baseIds.Count != 660 || baseIds.Contains(null))
            throw new InvalidOperationException("Invalid immutable wardrobe variant inventory");

        var expected = new Dictionary<string, JObject>(StringComparer.Ordinal);
        var artifacts = new HashSet<string>(StringComparer.Ordinal);
        var accountedIds = new HashSet<string>(StringComparer.Ordinal);
        int excludedVariants = 0;
        foreach (var model in preparation.Properties())
        {
            string source = (string)model.Value["source"];
            string prepared = (string)model.Value["prepared"];
            var variants = byPrefab[prepared].ToArray();
            var ids = variants.Select(row => (string)row["id"]).ToHashSet(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(prepared) || variants.Length == 0 ||
                ids.Any(id => !accountedIds.Add(id)))
                throw new InvalidOperationException("Immutable wardrobe variants are not uniquely assigned to " + model.Name);

            if (policyById.TryGetValue(model.Name, out var policy))
            {
                var exclusion = (JObject)excludedById[model.Name].DeepClone();
                var aliases = exclusion["logicalIds"] as JArray;
                exclusion.Remove("logicalIds");
                if ((string)policy["source"] != source || !JToken.DeepEquals(policy, exclusion) ||
                    !ExactModernLogicalIds(aliases, ids))
                    throw new InvalidOperationException("Curated exclusion or its variants differ for " + model.Name);
                excludedVariants += ids.Count;
                continue;
            }

            if (excludedSources.Contains(source) || !(fits[model.Name] is JObject fit) ||
                (string)fit["base"]?["source"] != source || (string)fit["base"]?["prepared"] != prepared ||
                !(fit["final"] is JObject final) || !ExactModernLogicalIds(final["logicalIds"] as JArray, ids))
                throw new InvalidOperationException("Included Modern fit has the wrong source or variants: " + model.Name);

            string prefab = (string)final["prefab"];
            var maps = VerifyModernFitArtifacts(model.Name, final, prefab, artifacts);
            foreach (var original in variants)
            {
                var row = (JObject)original.DeepClone();
                string definition = (string)row["definition"];
                if (definition == null || !definition.StartsWith(StagedWardrobeRoot + "Definitions/", StringComparison.Ordinal))
                    throw new InvalidOperationException("Wrong Modern definition root: " + RecordKey(row));
                row["main"] = prefab;
                row["metadata"]["contentOrigin"] = "modern";
                row["metadata"]["finalFit"] = true;
                if (final["playerReview"]?["layer"] is JObject reviewedLayer)
                {
                    var simulation = row["metadata"]["simulation"];
                    string after = (string)reviewedLayer["after"];
                    var definitionAsset = AssetDatabase.LoadAssetAtPath<GarmentDefinition>(definition);
                    if ((string)simulation["layer"] != (string)reviewedLayer["before"] ||
                        (string)final["settings"]?["layer"] != after || definitionAsset?.layer.ToString() != after)
                        throw new InvalidOperationException("Reviewed Modern layer mismatch: " + RecordKey(row));
                    simulation["layer"] = after;
                }
                AddRecord(row);
            }
            foreach (var map in maps)
            {
                string path = (string)map["path"];
                string name = Path.GetFileNameWithoutExtension(path);
                AddRecord(new JObject
                {
                    ["type"] = "config", ["id"] = ("PaintMaps." + name).ToLowerInvariant(), ["main"] = path,
                    ["metadata"] = new JObject
                    {
                        ["peopleCatalog"] = baseCatalog["peopleCatalog"].DeepClone(),
                        ["contentOrigin"] = "modern", ["legacyWardrobe"] = true, ["finalFit"] = true,
                        ["legacyResourcePath"] = "HexLive/PaintMaps/" + name,
                        ["legacyResourceFolder"] = "HexLive/PaintMaps"
                    }
                });
            }
        }

        int wearCount = expected.Values.Count(row => (string)row["type"] == "wear");
        int mapCount = expected.Count - wearCount;
        var counts = manifest["counts"];
        if (!accountedIds.SetEquals(baseIds) || wearCount + excludedVariants != 660 || mapCount != 2 * fits.Count ||
            (int?)counts?["models"] != fits.Count || (int?)counts?["logicalIds"] != wearCount ||
            (int?)counts?["maps"] != mapCount || (int?)counts?["meshes"] != mapCount)
            throw new InvalidOperationException("Curated wardrobe count or coverage mismatch");
        return expected;

        void AddRecord(JObject row)
        {
            string key = RecordKey(row);
            if ((bool?)row["metadata"]?["legacyWardrobe"] != true || expected.ContainsKey(key))
                throw new InvalidOperationException("Invalid or duplicate verified Modern record " + key);
            expected.Add(key, row);
        }
    }

    private static Dictionary<string, JObject> ModernExclusionsById(JArray rows)
    {
        var entries = new Dictionary<string, JObject>(StringComparer.Ordinal);
        foreach (var item in rows)
        {
            string id = (string)item["id"];
            if (!(item is JObject entry) || string.IsNullOrWhiteSpace(id) ||
                string.IsNullOrWhiteSpace((string)item["source"]) || string.IsNullOrWhiteSpace((string)item["reason"]) ||
                entries.ContainsKey(id))
                throw new InvalidOperationException("Invalid or duplicate curated exclusion " + id);
            entries.Add(id, entry);
        }
        return entries;
    }

    private static bool ExactModernLogicalIds(JArray values, HashSet<string> expected)
    {
        return values != null && values.Count == expected.Count && expected.SetEquals(values.Values<string>());
    }

    private static JArray VerifyModernFitArtifacts(string model, JObject final, string prefab, HashSet<string> artifacts)
    {
        if ((bool?)final["artifactValidationPassed"] != true || !(final["outputs"] is JArray outputs) ||
            outputs.Count != 5 || !(final["meshes"] is JObject meshes) || meshes.Count != 2 ||
            !(meshes["Marta"] is JObject marta) || !(meshes["Kshishtof"] is JObject kshishtof) ||
            !(final["maps"] is JArray maps) || maps.Count != 2)
            throw new InvalidOperationException("Missing verified Modern fit for " + model);
        var byPath = new Dictionary<string, JToken>(StringComparer.Ordinal);
        foreach (var output in outputs)
        {
            string path = (string)output["path"];
            // Dependency hashes are scoped to the original Editor target.
            // Verify raw bytes/GUID and then reopen the actual WebGL payloads.
            if (!IsStagedWardrobePath(path) || !artifacts.Add(path) ||
                (string)output["sha"] != Hash(path) || (string)output["metaSha"] != Hash(path + ".meta") ||
                (string)output["guid"] != AssetDatabase.AssetPathToGUID(path))
                throw new InvalidOperationException("Staged wardrobe artifact changed: " + path);
            byPath.Add(path, output);
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        RequireOutput(prefab, "Prefabs/FinalFit/", null);
        RequireOutput((string)marta["path"], "Meshes/FinalFit/", marta);
        RequireOutput((string)kshishtof["path"], "Meshes/FinalFit/", kshishtof);
        foreach (var map in maps)
            RequireOutput((string)map["path"], "PaintMaps/FinalFit/", map);
        return maps;

        void RequireOutput(string path, string directory, JToken descriptor)
        {
            if (path == null || !path.StartsWith(StagedWardrobeRoot + directory, StringComparison.Ordinal) ||
                !used.Add(path) || !byPath.TryGetValue(path, out var output) ||
                (descriptor != null && new[] { "sha", "metaSha", "guid", "dependencyHash" }
                    .Any(field => !JToken.DeepEquals(descriptor[field], output[field]))))
                throw new InvalidOperationException("Wrong verified Modern artifact ownership for " + model + ": " + path);
        }
    }

    private static void RequireModernBuildInputs(Dictionary<string, string> hashes)
    {
        foreach (var input in hashes)
            if (Hash(input.Key) != input.Value)
                throw new InvalidOperationException("Modern staging input changed during build: " + input.Key);
    }


    private static string RecordKey(JToken row) => (string)row["type"] + "/" + (string)row["id"];

    // Resolve the complete inventory before spending time on any bundle.
    // This also leaves an exact receipt for completeness checks after the build.
    public static void AuditAll()
    {
        var arguments = Environment.GetCommandLineArgs();
        var output = Path.GetFullPath(Required(arguments, "-content-output"));
        Directory.CreateDirectory(output);
        var rows = new JArray();
        var errors = new JArray();
        var checkedFiles = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var recipe in DiscoverAllRecipes())
            {
                try
                {
                    var inputs = recipe.Inputs();
                    ValidateAsset(inputs.Main, recipe.Type + "/" + recipe.Id);
                    ValidateAsset(inputs.Metadata, "metadata");
                    var roots = new List<string> { inputs.Main, inputs.Metadata };
                    if (!string.IsNullOrEmpty(inputs.Icon)) roots.Add(inputs.Icon);
                    roots.AddRange(inputs.Entries.Select(e => e.asset));
                    foreach (var root in roots) ValidateAsset(root, recipe.Id);
                    var dependencies = AssetDatabase.GetDependencies(roots.ToArray(), true);
                    foreach (var path in dependencies)
                    {
                        if (!File.Exists(path) || checkedFiles.Contains(path)) continue;
                        using var file = File.OpenRead(path);
                        var prefix = new byte[43];
                        int count = file.Read(prefix, 0, prefix.Length);
                        if (Encoding.ASCII.GetString(prefix, 0, count).StartsWith(
                                "version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal))
                            throw new InvalidOperationException("Git LFS pointer instead of source bytes: " + path);
                        checkedFiles.Add(path);
                    }
                    if (UsePeopleCatalog)
                    {
                        var legacy = dependencies.FirstOrDefault(p =>
                            IsStagedWardrobePath(p) ||
                            p.StartsWith(RuntimeSourceRoot + "/Actors/", StringComparison.Ordinal) ||
                            p.StartsWith(RuntimeSourceRoot + "/Wear/", StringComparison.Ordinal) ||
                            p.StartsWith(RuntimeSourceRoot + "/Helmets/", StringComparison.Ordinal) ||
                            p.StartsWith("Assets/HexLiveContent/Wear/", StringComparison.Ordinal) ||
                            p.StartsWith("Assets/ImportedActors/Wear/", StringComparison.Ordinal) ||
                            p.StartsWith("Assets/ImportedActors/Hair/", StringComparison.Ordinal));
                        if (legacy != null) throw new InvalidOperationException("Legacy people dependency: " + legacy);
                    }
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(inputs.Main);
                    if (IconBearingTypes.Contains(recipe.Type) &&
                        (prefab == null || !HexLive.UnityPresentation.ObjectFit.HasRenderableGeometry(prefab)))
                        throw new InvalidOperationException("Missing renderable main prefab");
                    if (prefab != null && prefab.GetComponentsInChildren<MonoBehaviour>(true).Any(c => c == null))
                        throw new InvalidOperationException("Missing prefab script");
                    foreach (var path in dependencies.Where(p => p.EndsWith(".mat", StringComparison.Ordinal)))
                    {
                        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                        if (material == null || material.shader == null || material.shader.name == "Hidden/InternalErrorShader")
                            throw new InvalidOperationException("Missing material shader: " + path);
                    }
                    rows.Add(new JObject { ["type"] = recipe.Type, ["id"] = recipe.Id,
                        ["main"] = inputs.Main, ["dependencies"] = new JArray(dependencies) });
                }
                catch (Exception error)
                {
                    errors.Add(new JObject { ["type"] = recipe.Type, ["id"] = recipe.Id, ["error"] = error.Message });
                }
                finally { CleanupGeneratedMetadata(); }
            }
            File.WriteAllText(Path.Combine(output, "inventory.json"), new JObject
            {
                ["passed"] = errors.Count == 0, ["peopleCatalog"] = UsePeopleCatalog ? "primal-v1" : "legacy",
                ["records"] = rows, ["errors"] = errors
            }.ToString(Formatting.Indented) + "\n");
            if (errors.Count != 0) throw new InvalidOperationException("Content preflight failed: " + errors);
            Debug.Log($"[AtomicContent] preflight passed: {rows.Count} objects, no bundles built.");
        }
        finally { CleanupGeneratedMetadata(); }
    }

    /// <summary>
    /// Builds one candidate inside the already open editor. This is the fast
    /// iteration path used for staging verification: it does not start a second
    /// Unity process, switch target, or rebuild any neighbouring object.
    /// </summary>
    public static string BuildOneInOpenEditor(
        string type, string id, string platform, string runtimeProfile, string output)
    {
        ValidateIdentity(type, id, platform, runtimeProfile);
        var target = ValidateTarget(platform);
        var fullOutput = Path.GetFullPath(output);
        try
        {
            var recipe = DiscoverAllRecipes().FirstOrDefault(value =>
                string.Equals(value.Type, type, StringComparison.Ordinal) &&
                string.Equals(value.Id, id, StringComparison.Ordinal));
            if (recipe == null)
            {
                throw new InvalidOperationException(
                    $"No authored atomic ContentObject found for {type}/{id}.");
            }

            BuildObject(type, id, platform, runtimeProfile, target, fullOutput, recipe.Inputs());
            return Path.Combine(fullOutput, "candidate.json");
        }
        finally
        {
            CleanupGeneratedMetadata();
        }
    }

    /// <summary>Release smoke: distinct atomic payloads must coexist in memory.</summary>
    public static void ValidatePayloads()
    {
        var succeeded = false;
        var loaded = new List<AssetBundle>();
        try
        {
            var payloads = Values(Environment.GetCommandLineArgs(), "-content-payload")
                .Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
            if (payloads.Length < 2)
            {
                throw new InvalidOperationException(
                    "Pass at least two distinct -content-payload files.");
            }

            foreach (var payload in payloads)
            {
                var bundle = AssetBundle.LoadFromFile(payload);
                if (bundle == null)
                {
                    throw new InvalidDataException(
                        $"Atomic payload cannot coexist with earlier payloads: {payload}");
                }
                loaded.Add(bundle);
            }

            Debug.Log($"[AtomicContent] coexistence gate passed for {loaded.Count} payloads.");
            succeeded = true;
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            if (!Application.isBatchMode)
            {
                throw;
            }
        }
        finally
        {
            foreach (var bundle in loaded)
            {
                bundle.Unload(true);
            }
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(succeeded ? 0 : 1);
            }
        }
    }

    private static void Run(bool buildAll, JArray stagedWardrobe = null,
        Dictionary<string, string> modernInputHashes = null, bool portalOnly = false)
    {
        var succeeded = false;
        try
        {
            var arguments = Environment.GetCommandLineArgs();
            var platform = Required(arguments, "-content-platform");
            var output = Path.GetFullPath(Required(arguments, "-content-output"));
            var runtimeProfile = Optional(arguments, "-content-runtime-profile") ?? RuntimeProfile;
            Directory.CreateDirectory(output);
            var groupedMode = Optional(arguments, "-content-staged-batch");
            if (portalOnly && (platform != "WebGL" || arguments.Contains("-content-staged-batch") ||
                arguments.Contains("-content-exclude-types") || arguments.Contains("-content-reopen-staged-output") ||
                Directory.EnumerateFileSystemEntries(output).Any()))
                throw new InvalidOperationException("Portal content requires a new WebGL output and its complete owner set");
            if (arguments.Contains("-content-staged-batch") && (stagedWardrobe == null ||
                groupedMode is not ("smoke" or "full") || Directory.EnumerateFileSystemEntries(output).Any()))
                throw new InvalidOperationException("Grouped mode requires a fresh dedicated Modern staging output and smoke or full");

            var target = ValidateTarget(platform);
            if (arguments.Contains("-content-reopen-staged-output"))
            {
                if (!buildAll || stagedWardrobe == null || groupedMode != null)
                    throw new InvalidOperationException("Reopening an existing output requires the dedicated Modern wardrobe build");
                ReopenStagedModernOutput(output, platform, runtimeProfile, stagedWardrobe, modernInputHashes);
                succeeded = true;
                return;
            }
            if (!buildAll)
            {
                var type = Required(arguments, "-content-type");
                var id = Required(arguments, "-content-id");
                ValidateIdentity(type, id, platform, runtimeProfile);
                BuildObject(
                    type, id, platform, runtimeProfile, target, output,
                    ResolveInputs(type, id, arguments));
            }
            else
            {
                // §168.12: веб-набор собирается по частям — персонажи, одежда и
                // волосы переезжают на облегчённые модели и в общий прогон не
                // идут, пока их нет. Исключённый тип не открывается вовсе.
                var excludedTypes = new HashSet<string>(
                    (Optional(arguments, "-content-exclude-types") ?? string.Empty)
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => value.Trim()),
                    StringComparer.Ordinal);
                if (stagedWardrobe != null && excludedTypes.Count != 0)
                    throw new InvalidOperationException("A staged Modern wardrobe requires its complete inventory");
                var recipes = portalOnly ? DiscoverPortalRecipes()
                    : stagedWardrobe == null
                    ? DiscoverAllRecipes().Where(recipe => !excludedTypes.Contains(recipe.Type) &&
                        !PortalContentPolicy.IsApproved(recipe.Type, recipe.Id) &&
                        !PortalContentPolicy.IsModernWorldOwner(recipe.Type, recipe.Id)).ToList()
                    : stagedWardrobe.Select(row => new Recipe
                    {
                        Type = (string)row["type"], Id = (string)row["id"], Inputs = () => ResolvePeople(row)
                    }).ToList();
                var duplicateBuildName = recipes
                    .GroupBy(recipe => BundleBuildName(recipe.Type, recipe.Id),
                        StringComparer.Ordinal)
                    .FirstOrDefault(group => group.Count() > 1);
                if (duplicateBuildName != null)
                {
                    throw new InvalidOperationException(
                        "Atomic bundle identity collision: " +
                        string.Join(", ", duplicateBuildName.Select(
                            recipe => recipe.Type + "/" + recipe.Id)));
                }
                var failures = new JArray();
                var builtCount = 0;
                var complete = groupedMode == null && !portalOnly ? null : new HashSet<Recipe>();
                void BuildSingle(Recipe recipe)
                {
                    try
                    {
                        ValidateIdentity(recipe.Type, recipe.Id, platform, runtimeProfile);
                        var objectOutput = Path.Combine(
                            output, recipe.Type, recipe.Id, platform);
                        BuildObject(
                            recipe.Type, recipe.Id, platform, runtimeProfile, target,
                            objectOutput, recipe.Inputs());
                        builtCount++;
                        complete?.Add(recipe);
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new JObject
                        {
                            ["type"] = recipe.Type,
                            ["id"] = recipe.Id,
                            ["error"] = ex.Message,
                        });
                        Debug.LogError($"[AtomicContent] FAILED {recipe.Type}/{recipe.Id}: {ex}");
                    }
                    finally
                    {
                        CleanupGeneratedMetadata();
                    }
                }

                if (complete == null)
                {
                    foreach (var recipe in recipes) BuildSingle(recipe);
                }
                else
                {
                    var smoke = groupedMode == "smoke";
                    var groups = portalOnly ? PlanPortalGroups(recipes) : PlanStagedGroups(recipes, stagedWardrobe, smoke);
                    var proof = BuildStagedGroups(groups, output, platform, runtimeProfile, target, smoke, BuildSingle, complete);
                    if (portalOnly) proof["mode"] = "portal";
                    else
                    {
                        RequireModernBuildInputs(modernInputHashes);
                        proof["catalogSHA256"] = modernInputHashes[StagedWardrobeRoot + "release-catalog.json"];
                        proof["fitManifestSHA256"] = modernInputHashes[StagedWardrobeRoot + "release-fit.json"];
                        proof["releaseSelectionSHA256"] = modernInputHashes[StagedWardrobeRoot + "release-selection.json"];
                    }
                    File.WriteAllText(Path.Combine(output, "grouped-build-receipt.json"),
                        proof.ToString(Formatting.Indented) + "\n", new UTF8Encoding(false));
                    if (smoke)
                    {
                        if (complete.Count != 2 * StagedGroupSize ||
                            proof["groups"].Any(group => (bool?)group["manifestAndEntriesVerified"] != true))
                            throw new InvalidOperationException("Grouped Modern smoke did not verify both groups");
                        succeeded = true;
                        return;
                    }
                    builtCount = complete.Count;
                    if (builtCount + failures.Count != recipes.Count)
                        throw new InvalidOperationException("Grouped Modern build did not account for every recipe");
                }

                var summary = new JObject
                {
                    ["platform"] = platform,
                    ["runtimeProfile"] = runtimeProfile,
                    ["discovered"] = recipes.Count,
                    ["excludedTypes"] = new JArray(excludedTypes.OrderBy(value => value, StringComparer.Ordinal)),
                    ["built"] = builtCount,
                    ["failed"] = failures.Count,
                    ["failures"] = failures,
                };
                if (portalOnly)
                {
                    summary["stage"] = "portal";
                    summary["unityVersion"] = Application.unityVersion;
                }
                else if (stagedWardrobe != null)
                {
                    summary["contentOrigin"] = "modern";
                    summary["stage"] = "prepared-only";
                    summary["unityVersion"] = Application.unityVersion;
                    RequireModernBuildInputs(modernInputHashes);
                    summary["primalCatalogSHA256"] = modernInputHashes[PeopleCatalogPath];
                    summary["catalogSHA256"] = modernInputHashes[StagedWardrobeRoot + "release-catalog.json"];
                    summary["fitManifestSHA256"] = modernInputHashes[StagedWardrobeRoot + "release-fit.json"];
                    summary["releaseSelectionSHA256"] = modernInputHashes[StagedWardrobeRoot + "release-selection.json"];
                }
                File.WriteAllText(
                    Path.Combine(output, "build-all-summary.json"),
                    summary.ToString(Formatting.Indented) + "\n", new UTF8Encoding(false));
                if (failures.Count != 0)
                {
                    throw new InvalidOperationException(
                        $"Full atomic build failed for {failures.Count}/{recipes.Count} objects.");
                }
                if (stagedWardrobe != null)
                    ValidatePeoplePayloads(output, platform, stagedWardrobe);
                else if (!portalOnly && UsePeopleCatalog)
                    ValidatePeoplePayloads(output, platform);
            }
            succeeded = true;
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            if (!Application.isBatchMode)
            {
                throw;
            }
        }
        finally
        {
            CleanupGeneratedMetadata();
            if (AssetDatabase.IsValidFolder(GeneratedMetadataRoot))
            {
                AssetDatabase.DeleteAsset(GeneratedMetadataRoot);
            }
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(succeeded ? 0 : 1);
            }
        }
    }

    private static BuildTarget ValidateTarget(string platform)
    {
        if (!Enum.TryParse(platform, out BuildTarget target) ||
            target is not (BuildTarget.StandaloneOSX or BuildTarget.StandaloneWindows64 or
                BuildTarget.WebGL))
        {
            throw new InvalidOperationException(
                $"Unsupported platform '{platform}'; use StandaloneOSX, StandaloneWindows64 or WebGL.");
        }
        if (EditorUserBuildSettings.activeBuildTarget != target)
        {
            throw new InvalidOperationException(
                $"Unity active target is {EditorUserBuildSettings.activeBuildTarget}, expected {target}. " +
                "Pass the matching -buildTarget before -executeMethod.");
        }
        return target;
    }

    private static void ReopenStagedModernOutput(string output, string platform, string runtimeProfile,
        JArray records, Dictionary<string, string> inputHashes)
    {
        var summary = JObject.Parse(File.ReadAllText(Path.Combine(output, "build-all-summary.json")));
        if ((string)summary["contentOrigin"] != "modern" || (string)summary["stage"] != "prepared-only" ||
            (string)summary["platform"] != platform || (string)summary["runtimeProfile"] != runtimeProfile ||
            (string)summary["unityVersion"] != Application.unityVersion ||
            (int?)summary["built"] != records.Count || (int?)summary["discovered"] != records.Count ||
            (int?)summary["failed"] != 0 || (summary["failures"] as JArray)?.Count != 0 ||
            (summary["excludedTypes"] as JArray)?.Count != 0 ||
            (string)summary["primalCatalogSHA256"] != inputHashes[PeopleCatalogPath] ||
            (string)summary["catalogSHA256"] != inputHashes[StagedWardrobeRoot + "release-catalog.json"] ||
            (string)summary["fitManifestSHA256"] != inputHashes[StagedWardrobeRoot + "release-fit.json"] ||
            (string)summary["releaseSelectionSHA256"] != inputHashes[StagedWardrobeRoot + "release-selection.json"])
            throw new InvalidOperationException("The existing Modern build does not match the complete verified inventory");

        ValidatePeoplePayloads(output, platform, records);
        RequireModernBuildInputs(inputHashes);
    }

    private static void ValidatePeoplePayloads(string output, string platform, JArray stagedWardrobe = null)
    {
        var contract = JObject.Parse(File.ReadAllText("Assets/HexLiveContent/People/Source/body-contract.json"));
        var retained = new List<AssetBundle>();
        var rows = new JArray();
        var requiredMaps = new Dictionary<string, int>(StringComparer.Ordinal);
        var actualMaps = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            foreach (var row in stagedWardrobe ?? PeopleRecords())
            {
                string type = (string)row["type"], id = (string)row["id"];
                string payload = Path.Combine(output, type, id, platform, PayloadName);
                var bundle = AssetBundle.LoadFromFile(payload);
                if (bundle == null) throw new InvalidOperationException("Cannot reopen or coexist: " + type + "/" + id);
                bool keep = type == "actor" || (type == "wear" && retained.Count < 5);
                try
                {
                    var main = bundle.LoadAsset<UnityEngine.Object>("main");
                    if (main == null) throw new InvalidOperationException("Missing main after serialization: " + id);
                    if (type == "actor")
                    {
                        var actor = main as GameObject;
                        if (!HexLive.UnityPresentation.Wearing.ActorBodyResolver.TryResolve(actor, out var body, out var error))
                            throw new InvalidOperationException(id + ": " + error);
                        requiredMaps["HexLive/PaintMaps/skin_" + id] = body.sharedMesh.vertexCount;
                        foreach (var skin in actor.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                            if (skin.localBounds.center.sqrMagnitude > .001f || skin.localBounds.extents.x <= 0f)
                                throw new InvalidOperationException(id + ": missing repaired hip-relative culling envelope");
                        var names = Enumerable.Range(0, body.sharedMesh.blendShapeCount).Select(body.sharedMesh.GetBlendShapeName);
                        if (!names.SequenceEqual(contract[id]["blendShapes"].Values<string>()))
                            throw new InvalidOperationException(id + ": morph contract changed in bundle");
                        var animator = actor.GetComponent<Animator>();
                        if (animator == null || animator.avatar == null || !animator.avatar.isHuman ||
                            !animator.avatar.isValid || animator.runtimeAnimatorController == null)
                            throw new InvalidOperationException(id + ": humanoid animation references lost");
                        if (actor.GetComponent<HexLive.UnityPresentation.Wearing.PeopleAppearance>() == null)
                            throw new InvalidOperationException(id + ": skin configuration lost");
                    }
                    if (main is GameObject prefab)
                    {
                        if (prefab.GetComponentsInChildren<MonoBehaviour>(true).Any(c => c == null))
                            throw new InvalidOperationException(id + ": serialized script missing");
                        foreach (var renderer in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                            if (renderer.sharedMesh == null || renderer.bones.Any(b => b == null) ||
                                renderer.bones.Length != renderer.sharedMesh.bindposes.Length ||
                                renderer.sharedMaterials.Any(m => m == null || m.shader == null))
                                throw new InvalidOperationException(id + ": serialized skin binding/material invalid");
                    }
                    if (type == "wear")
                    {
                        var mesh = ((GameObject)main).GetComponentInChildren<SkinnedMeshRenderer>(true).sharedMesh;
                        requiredMaps[$"HexLive/PaintMaps/garment_{mesh.name}_{mesh.vertexCount}"] = mesh.vertexCount;
                        var definition = bundle.LoadAsset<GarmentDefinition>("metadata");
                        var materials = definition?.variantMaterials;
                        // The base Modern definition inherits the prefab's authored materials.
                        // Each named material variant must retain its authored override array.
                        var expectedDefinition = stagedWardrobe == null ? null :
                            AssetDatabase.LoadAssetAtPath<GarmentDefinition>((string)row["definition"]);
                        bool materialCountMatches = stagedWardrobe == null ? materials?.Length > 0 :
                            expectedDefinition?.variantMaterials != null && materials?.Length == expectedDefinition.variantMaterials.Length;
                        if (definition == null || definition.id != id || !materialCountMatches || materials.Any(m => m == null))
                            throw new InvalidOperationException(id + ": variant metadata/materials lost");
                        var wear = (main as GameObject)?.GetComponent<HexLive.UnityPresentation.Wearing.Wear>();
                        if (wear == null || wear.Layer.ToString() != definition.layer.ToString() ||
                            definition.layer.ToString() != (string)row["metadata"]["simulation"]["layer"] ||
                            !definition.covers.Select(c => c.ToString()).SequenceEqual(
                                row["metadata"]["simulation"]["covers"].Values<string>()))
                            throw new InvalidOperationException(id + ": prefab, definition and server wardrobe disagree");
                        if (stagedWardrobe != null)
                        {
                            var configs = new SerializedObject(wear).FindProperty("configs");
                            var sourceWear = AssetDatabase.LoadAssetAtPath<GameObject>((string)row["main"])
                                ?.GetComponent<HexLive.UnityPresentation.Wearing.Wear>();
                            if (configs == null || configs.arraySize != 2 ||
                                !wear.SupportsActor(HexLive.UnityPresentation.Wearing.ActorName.Marta) ||
                                !wear.SupportsActor(HexLive.UnityPresentation.Wearing.ActorName.Kshishtof) ||
                                bundle.LoadAsset<Sprite>("icon") == null || sourceWear == null)
                                throw new InvalidOperationException(id + ": missing serialized fit or icon");
                            foreach (var actor in new[] { HexLive.UnityPresentation.Wearing.ActorName.Marta,
                                HexLive.UnityPresentation.Wearing.ActorName.Kshishtof })
                                if (wear.GetConfigScale(actor) != sourceWear.GetConfigScale(actor) ||
                                    wear.HidesGenitals != sourceWear.HidesGenitals)
                                    throw new InvalidOperationException(id + ": reviewed fit settings differ from source");
                            for (int i = 0; i < configs.arraySize; i++)
                            {
                                var fittedMesh = configs.GetArrayElementAtIndex(i).FindPropertyRelative("mesh").objectReferenceValue as Mesh;
                                if (fittedMesh == null || fittedMesh.vertexCount == 0)
                                    throw new InvalidOperationException(id + ": missing serialized fitted mesh");
                                requiredMaps[$"HexLive/PaintMaps/garment_{fittedMesh.name}_{fittedMesh.vertexCount}"] = fittedMesh.vertexCount;
                            }
                        }
                    }
                    if (main is HexLive.UnityPresentation.Wearing.PaintPointMap map)
                    {
                        var key = (string)row["metadata"]["legacyResourcePath"];
                        if (string.IsNullOrEmpty(key) || !map.Zones.Any(z => z.Points.Any(p => p.Valid)))
                            throw new InvalidOperationException(id + ": missing paint alias or valid anchors");
                        actualMaps.Add(key, map.VertexCount);
                    }
                    var receipt = new JObject { ["type"] = type, ["id"] = id, ["passed"] = true };
                    if (stagedWardrobe != null)
                    {
                        receipt["sha256"] = Hash(payload);
                        receipt["size"] = new FileInfo(payload).Length;
                    }
                    rows.Add(receipt);
                    if (keep) retained.Add(bundle);
                }
                finally { if (!retained.Contains(bundle)) bundle.Unload(true); }
            }
            foreach (var map in requiredMaps)
                if (!actualMaps.TryGetValue(map.Key, out var vertices) || vertices != map.Value)
                    throw new InvalidOperationException("Serialized renderer cannot resolve its paint map: " + map.Key);
            File.WriteAllText(Path.Combine(output, "people-payload-validation.json"), new JObject
            {
                ["passed"] = true, ["objects"] = rows, ["coexistingBundles"] = retained.Count,
                ["browserRenderValidated"] = false, ["rendererPaintMapContracts"] = requiredMaps.Count
            }.ToString(Formatting.Indented) + "\n");
            Debug.Log($"[AtomicContent] reopened {rows.Count} People payloads; {retained.Count} coexist; morph/rig/material references intact.");
        }
        finally { foreach (var bundle in retained) bundle.Unload(true); }
    }

    private const int StagedGroupSize = 16;

    private sealed class StagedGroupEntry
    {
        public Recipe Recipe;
        public string[] Roots;
        public HashSet<string> Dependencies;
    }

    private sealed class StagedGroup
    {
        public readonly List<StagedGroupEntry> Entries = new();
        private readonly HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> dependencies = new(StringComparer.OrdinalIgnoreCase);

        public bool CanAdd(StagedGroupEntry entry) => Entries.Count < StagedGroupSize &&
            !dependencies.Overlaps(entry.Roots) && !entry.Dependencies.Overlaps(roots);

        public void Add(StagedGroupEntry entry)
        {
            Entries.Add(entry);
            roots.UnionWith(entry.Roots);
            dependencies.UnionWith(entry.Dependencies);
        }
    }

    private static List<StagedGroup> PlanStagedGroups(List<Recipe> recipes, JArray catalog, bool smoke)
    {
        var rows = catalog.ToDictionary(RecordKey, StringComparer.Ordinal);
        var closure = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var groups = smoke ? new List<StagedGroup> { new(), new() } : new List<StagedGroup>();
        foreach (var recipe in smoke ? recipes.OrderBy(row => row.Type == "wear" ? 0 : 1) : recipes.AsEnumerable())
        {
            bool wear = recipe.Type == "wear";
            if (!wear && recipe.Type != "config")
                throw new InvalidOperationException("Grouped staging supports only wardrobe wear and maps");
            int quota = wear ? 12 : 4;
            if (smoke && groups.All(group => group.Entries.Count(entry => entry.Recipe.Type == recipe.Type) >= quota))
                continue;
            var row = rows[recipe.Type + "/" + recipe.Id];
            var main = (string)row["main"];
            var roots = wear ? new[] { main, (string)row["definition"], $"{IconRoot}/{recipe.Id}.png" } :
                new[] { main, $"{GeneratedMetadataRoot}/{recipe.Type}.{recipe.Id}.json" };
            var dependencies = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots.Take(wear ? roots.Length : 1))
            {
                if (!closure.TryGetValue(root, out var paths))
                    closure[root] = paths = AssetDatabase.GetDependencies(root, true);
                dependencies.UnionWith(paths);
            }
            var entry = new StagedGroupEntry { Recipe = recipe, Roots = roots, Dependencies = dependencies };
            var group = groups.FirstOrDefault(candidate => candidate.CanAdd(entry) && (!smoke ||
                candidate.Entries.Count(member => member.Recipe.Type == recipe.Type) < quota));
            if (group == null && !smoke)
            {
                group = new StagedGroup();
                groups.Add(group);
            }
            group?.Add(entry);
        }
        if (smoke && groups.Any(group => group.Entries.Count != StagedGroupSize))
            throw new InvalidOperationException("Grouped smoke requires two independent groups of 12 wear and 4 maps");
        if (!smoke && groups.Sum(group => group.Entries.Count) != recipes.Count)
            throw new InvalidOperationException("Grouped plan lost a staged wardrobe recipe");
        return groups;
    }

    private static List<StagedGroup> PlanPortalGroups(List<Recipe> recipes)
    {
        var groups = new List<StagedGroup>();
        var closure = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var recipe in recipes)
        {
            try
            {
                var inputs = recipe.Inputs();
                if (recipe.Type == "object" && (string.IsNullOrEmpty(inputs.Icon) ||
                    AssetDatabase.LoadAssetAtPath<Sprite>(inputs.Icon) == null))
                    throw new InvalidOperationException("Portal release requires the imported icon for " + recipe.Id);
                var (roots, _) = PrepareBundleEntries(recipe.Type, recipe.Id, inputs);
                var dependencies = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
                foreach (var root in roots)
                {
                    if (!closure.TryGetValue(root, out var paths))
                        closure[root] = paths = AssetDatabase.GetDependencies(root, true);
                    dependencies.UnionWith(paths);
                }
                var entry = new StagedGroupEntry { Recipe = recipe, Roots = roots, Dependencies = dependencies };
                var group = groups.FirstOrDefault(candidate => candidate.CanAdd(entry));
                if (group == null)
                {
                    group = new StagedGroup();
                    groups.Add(group);
                }
                group.Add(entry);
            }
            finally
            {
                CleanupGeneratedMetadata();
            }
        }
        return groups;
    }

    private static void BuildStagedGroup(StagedGroup group, string work, string output, string platform,
        string runtimeProfile, BuildTarget target, HashSet<Recipe> complete)
    {
        var generated = new List<string>();
        try
        {
            var jobs = new List<(StagedGroupEntry Entry, BuildInputs Inputs, AssetBundleBuild Build)>();
            foreach (var entry in group.Entries)
            {
                var recipe = entry.Recipe;
                ValidateIdentity(recipe.Type, recipe.Id, platform, runtimeProfile);
                var inputs = recipe.Inputs();
                if (_temporaryMetadata != null)
                {
                    generated.Add(_temporaryMetadata);
                    _temporaryMetadata = null;
                }
                var (assets, names) = PrepareBundleEntries(recipe.Type, recipe.Id, inputs);
                if (!assets.SequenceEqual(entry.Roots, StringComparer.Ordinal))
                    throw new InvalidOperationException("Grouped recipe roots changed: " + recipe.Id);
                jobs.Add((entry, inputs, new AssetBundleBuild
                {
                    assetBundleName = BundleBuildName(recipe.Type, recipe.Id),
                    assetNames = assets,
                    addressableNames = names,
                }));
            }

            Directory.CreateDirectory(work);
            var builds = jobs.Select(job => job.Build).ToArray();
            var manifest = BuildPipeline.BuildAssetBundles(work, builds, BuildAssetBundleOptions.ChunkBasedCompression |
                BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.StrictMode, target);
            var expected = new HashSet<string>(builds.Select(build => build.assetBundleName), StringComparer.Ordinal);
            var built = manifest?.GetAllAssetBundles();
            if (built == null || built.Length != expected.Count || !expected.SetEquals(built))
                throw new InvalidOperationException("Grouped manifest inventory differs from its plan");
            foreach (var name in built)
            {
                var dependencies = manifest.GetAllDependencies(name);
                if (dependencies.Length != 0)
                    throw new InvalidOperationException(name + " has external bundle dependencies: " + string.Join(", ", dependencies));
            }

            var prepared = new List<(Recipe Recipe, string Source, string Destination)>();
            foreach (var job in jobs)
            {
                var recipe = job.Entry.Recipe;
                var builtPayload = Path.Combine(work, job.Build.assetBundleName);
                ValidateEntries(builtPayload, job.Build.addressableNames, recipe.Type, recipe.Id);
                var scratch = Path.Combine(work, job.Build.assetBundleName + ".entry");
                Directory.CreateDirectory(scratch);
                var final = Path.Combine(output, recipe.Type, recipe.Id, platform);
                var sha = Hash(builtPayload);
                var size = new FileInfo(builtPayload).Length;
                File.Move(builtPayload, Path.Combine(scratch, PayloadName));
                // Keep unpromoted candidates outside the packager's candidate.json scan.
                File.WriteAllText(Path.Combine(scratch, "candidate.pending"), CandidateJson(recipe.Type, recipe.Id,
                    platform, runtimeProfile, Path.Combine(final, PayloadName), sha, size, job.Inputs), new UTF8Encoding(false));
                prepared.Add((recipe, scratch, final));
            }
            foreach (var item in prepared)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.Destination));
                Directory.Move(item.Source, item.Destination);
                File.Move(Path.Combine(item.Destination, "candidate.pending"), Path.Combine(item.Destination, "candidate.json"));
                complete.Add(item.Recipe);
            }
        }
        finally
        {
            CleanupGeneratedMetadata();
            foreach (var path in generated) AssetDatabase.DeleteAsset(path);
        }
    }

    private static JObject BuildStagedGroups(List<StagedGroup> groups, string output, string platform,
        string runtimeProfile, BuildTarget target, bool smoke, Action<Recipe> buildSingle, HashSet<Recipe> complete)
    {
        var receipts = new JArray();
        for (int i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            string error = null;
            int grouped = 0;
            try
            {
                BuildStagedGroup(group, Path.Combine(output, ".groups", $"g{i:D3}"), output, platform, runtimeProfile, target, complete);
                grouped = group.Entries.Count;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                Debug.LogWarning($"[AtomicContent] staged group {i} failed: {exception}");
                grouped = group.Entries.Count(entry => complete.Contains(entry.Recipe));
                if (!smoke)
                    foreach (var entry in group.Entries.Where(entry => !complete.Contains(entry.Recipe)))
                        buildSingle(entry.Recipe);
            }
            receipts.Add(new JObject
            {
                ["index"] = i, ["elapsedSeconds"] = stopwatch.Elapsed.TotalSeconds,
                ["grouped"] = grouped, ["built"] = group.Entries.Count(entry => complete.Contains(entry.Recipe)),
                ["manifestAndEntriesVerified"] = error == null,
                ["error"] = error,
                ["objects"] = new JArray(group.Entries.Select(entry => new JObject
                {
                    ["type"] = entry.Recipe.Type, ["id"] = entry.Recipe.Id,
                    ["bundle"] = BundleBuildName(entry.Recipe.Type, entry.Recipe.Id),
                })),
            });
            Debug.Log($"[AtomicContent] staged group {i}: {grouped}/{group.Entries.Count} grouped in {stopwatch.Elapsed.TotalSeconds:F2}s");
        }
        return new JObject { ["mode"] = smoke ? "smoke" : "full", ["built"] = complete.Count, ["groups"] = receipts };
    }


    private static (string[] Assets, string[] Names) PrepareBundleEntries(string type, string id, BuildInputs inputs)
    {
        ValidateAsset(inputs.Main, "main");
        ValidateAsset(inputs.Metadata, "metadata");
        // A construction owner carries its exact per-unit contract. Reject a
        // stale/native-overwritten FBX before invoking the bundle builder.
        var constructionReceipt = HexLive.Editor.ConstructionStageBuildGate.ValidateContent(type, id);
        if (constructionReceipt != null)
        {
            var requiredModel = constructionReceipt.Replace(".construction.json", ".fbx");
            if (!string.Equals(inputs.Main, requiredModel, StringComparison.Ordinal))
                throw new InvalidOperationException($"Construction {id} must use {requiredModel}, not {inputs.Main}.");
            var receipt = JObject.Parse(File.ReadAllText(constructionReceipt));
            inputs.ExtraMetadata["construction"] = receipt;
            inputs.Entries = inputs.Entries.Concat(new[] {
                new ContentEntry { name = "construction", asset = constructionReceipt }
            }).ToArray();
        }
        if (!string.IsNullOrEmpty(inputs.Icon))
        {
            ValidateAsset(inputs.Icon, "icon");
        }

        var assetNames = new List<string> { inputs.Main, inputs.Metadata };
        var entryNames = new List<string> { "main", "metadata" };
        if (!string.IsNullOrEmpty(inputs.Icon))
        {
            assetNames.Add(inputs.Icon);
            entryNames.Add("icon");
        }
        foreach (var extra in inputs.Entries)
        {
            if (extra == null || string.IsNullOrEmpty(extra.name) ||
                extra.name is "main" or "metadata" or "icon" ||
                entryNames.Contains(extra.name) || !SafeEntry(extra.name))
            {
                throw new InvalidOperationException(
                    $"Invalid or duplicate extra entry '{extra?.name}'.");
            }
            ValidateAsset(extra.asset, extra.name);
            assetNames.Add(extra.asset);
            entryNames.Add(extra.name);
        }
        return (assetNames.ToArray(), entryNames.ToArray());
    }

    private static void BuildObject(
        string type, string id, string platform, string runtimeProfile,
        BuildTarget target, string output, BuildInputs inputs)
    {
        var (assetNames, entryNames) = PrepareBundleEntries(type, id, inputs);
        Directory.CreateDirectory(output);
        DeleteOldOutput(output);

        // Unity derives the internal CAB identity from the AssetBundle name.
        // Reusing "payload.bundle" for every independently built object made
        // otherwise different files mutually exclusive at runtime ("another
        // AssetBundle with the same files is already loaded"). The published
        // filename stays payload.bundle; only the immutable internal identity
        // is unique and stable per logical object.
        var bundleBuildName = BundleBuildName(type, id);
        var build = new AssetBundleBuild
        {
            assetBundleName = bundleBuildName,
            assetNames = assetNames,
            addressableNames = entryNames,
        };
        // A missing IL2CPP module makes Unity fall back to incompatible
        // Editor reference DLLs (NativeList/ReadOnly CS7036). Require the real
        // player references; never patch Packages or inject compiler defines.
        ValidateWindowsReferences(target);
        var manifest = BuildPipeline.BuildAssetBundles(
            output, new[] { build },
            BuildAssetBundleOptions.ChunkBasedCompression |
            BuildAssetBundleOptions.ForceRebuildAssetBundle |
            BuildAssetBundleOptions.StrictMode, target);
        if (manifest == null)
        {
            throw new InvalidOperationException("BuildPipeline returned no AssetBundleManifest.");
        }
        var built = manifest.GetAllAssetBundles();
        if (built.Length != 1 || built[0] != bundleBuildName)
        {
            throw new InvalidOperationException(
                "Atomic build emitted unexpected bundles: " + string.Join(", ", built));
        }
        var dependencies = manifest.GetAllDependencies(bundleBuildName);
        if (dependencies.Length != 0)
        {
            throw new InvalidOperationException(
                $"{type}/{id} is not self-contained; external dependencies: " +
                string.Join(", ", dependencies));
        }

        var builtPayload = Path.Combine(output, bundleBuildName);
        var payload = Path.Combine(output, PayloadName);
        File.Move(builtPayload, payload);
        ValidateEntries(payload, entryNames, type, id);
        var sha256 = Hash(payload);
        var size = new FileInfo(payload).Length;
        File.WriteAllText(
            Path.Combine(output, "candidate.json"),
            CandidateJson(type, id, platform, runtimeProfile, payload, sha256, size, inputs),
            new UTF8Encoding(false));
        Debug.Log(
            $"[AtomicContent] built {type}/{id} for {platform}/{runtimeProfile}: " +
            $"{size} bytes, sha256={sha256}, dependencies=0, " +
            $"icon={(string.IsNullOrEmpty(inputs.Icon) ? "none" : "embedded")}");
    }

    private static string BundleBuildName(string type, string id)
    {
        using var sha = SHA256.Create();
        var identity = Encoding.UTF8.GetBytes(type + "/" + id);
        var digest = sha.ComputeHash(identity);
        return "hexlive." + type + "." +
               string.Concat(digest.Take(12).Select(value => value.ToString("x2")));
    }

    private static void CleanupGeneratedMetadata()
    {
        if (!string.IsNullOrEmpty(_temporaryMetadata))
        {
            AssetDatabase.DeleteAsset(_temporaryMetadata);
            _temporaryMetadata = null;
        }
    }

    private static List<Recipe> DiscoverAllRecipes()
    {
        var recipes = new Dictionary<string, Recipe>(StringComparer.Ordinal);
        void Add(string type, string id, Func<BuildInputs> inputs)
        {
            var key = type + "\n" + id;
            if (!recipes.ContainsKey(key))
            {
                recipes[key] = new Recipe { Type = type, Id = id, Inputs = inputs };
            }
        }

        if (UsePeopleCatalog)
        {
            foreach (var row in PeopleRecords())
            {
                var entry = row;
                Add((string)entry["type"], (string)entry["id"], () => ResolvePeople(entry));
            }
        }
        else
        {
        // The simulation defaults are the active authoring inventory. The asset
        // tree also contains unfinished extracted variants which deliberately
        // have no art/icon yet; publishing those would expose broken records.
        // This is build-time discovery only, never a runtime content catalog.
        foreach (var garment in GarmentLibrary.Defaults)
        {
            if (garment == null || string.IsNullOrWhiteSpace(garment.Id))
            {
                continue;
            }
            var id = garment.Id;
            Add("wear", id, () => ResolveOwnerIcon("wear", id, ResolveWear(id)));
        }

        foreach (var guid in AssetDatabase.FindAssets(
                     "t:Prefab", new[] { RuntimeSourceRoot + "/Actors" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var id = Path.GetFileNameWithoutExtension(path);
            Add("actor", id, () => ResolveOwnerIcon(
                "actor", id, ResolveConvention(
                    "actor", id, path, $"{IconRoot}/actor.{id}.png", "HexLive/Actors/" + id)));
        }

        const string hairRoot = "Assets/ImportedActors/Hair";
        // A content-only checkout (§168.12) may not carry the hair at all.
        foreach (var directory in (Directory.Exists(hairRoot)
                         ? Directory.GetDirectories(hairRoot)
                         : Array.Empty<string>())
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            var id = Path.GetFileName(directory);
            var main = $"{hairRoot}/{id}/{id}.prefab";
            if (AssetDatabase.LoadMainAssetAtPath(main) != null)
            {
                Add("hair", id, () => ResolveOwnerIcon("hair", id, ResolveHair(id)));
            }
        }

        }

        const string prostheticRoot = "Assets/HexLiveContent/Prosthetics";
        foreach (var path in (Directory.Exists(prostheticRoot)
                         ? Directory.GetFiles(prostheticRoot, "prosthetic_*.fbx")
                         : Array.Empty<string>())
                     .Select(value => value.Replace('\\', '/'))
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(path)["prosthetic_".Length..];
            var id = name.Replace('_', '.');
            Add("prosthetic", id,
                () => ResolveOwnerIcon("prosthetic", id, ResolveProsthetic(id)));
        }

        DiscoverObjectRecipes(Add);
        DiscoverFistGearRecipe(Add);
        DiscoverMobRecipes(Add);
        DiscoverGenericRecipes(Add);
        return recipes.Values
            .OrderBy(value => value.Type, StringComparer.Ordinal)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static void DiscoverFistGearRecipe(
        Action<string, string, Func<BuildInputs>> add)
    {
        // Fists deliberately use GearCatalog.Fist (the empty string), so they
        // cannot join object/<gearId> below and have no renderable item prefab
        // to serve as an object record's main entry. Publish the GearConfig
        // itself as the main asset of one config-only record.
        var fist = AssetDatabase.FindAssets(
                "t:GearConfig", new[] { RuntimeSourceRoot + "/Gear" })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Select(path => (path, config: AssetDatabase.LoadAssetAtPath<GearConfig>(path)))
            .FirstOrDefault(pair => pair.config != null &&
                                    string.IsNullOrEmpty(pair.config.gearId));
        if (fist.config == null)
        {
            return;
        }

        add(GearConfig.FistContentType, GearConfig.FistContentId, () =>
        {
            var metadata = new JObject
            {
                ["legacyResourcePath"] = GearConfig.FistContentPath,
                ["legacyResourceFolder"] = GearConfig.ResourceFolder,
            };
            return new BuildInputs
            {
                Main = fist.path,
                Metadata = CreateGeneratedMetadata(
                    GearConfig.FistContentType, GearConfig.FistContentId, metadata),
                DisplayName = "fist",
                ExtraMetadata = metadata,
            };
        });
    }

    private static List<Recipe> DiscoverPortalRecipes()
    {
        var expected = PortalContentPolicy.Objects.Select(id => "object/" + id)
            .Concat(PortalContentPolicy.Buildings.Select(id => "building/" + id))
            .Concat(new[] { "object/chair.basic", "object/" + ContentIds.Workbench })
            .ToHashSet(StringComparer.Ordinal);
        var recipes = new List<Recipe>();
        DiscoverObjectRecipes((type, id, inputs) =>
            recipes.Add(new Recipe { Type = type, Id = id, Inputs = inputs }), portalOnly: true);
        var discovered = recipes.Select(recipe => recipe.Type + "/" + recipe.Id);
        if (!expected.SetEquals(discovered))
            throw new InvalidOperationException("Missing portal release owners: " +
                string.Join(", ", expected.Except(discovered).OrderBy(id => id, StringComparer.Ordinal)));
        return recipes;
    }

    private static void DiscoverObjectRecipes(Action<string, string, Func<BuildInputs>> add, bool portalOnly = false)
    {
        var worldConfigs = AssetDatabase.FindAssets(
                "t:WorldObjectConfig", new[] { RuntimeSourceRoot + "/WorldObjects" })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Select(path => (path, value: AssetDatabase.LoadAssetAtPath<WorldObjectConfig>(path)))
            .Where(pair => pair.value != null && !string.IsNullOrWhiteSpace(pair.value.objectId))
            .GroupBy(pair => pair.value.objectId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().path, StringComparer.Ordinal);
        var gearConfigs = AssetDatabase.FindAssets(
                "t:GearConfig", new[] { RuntimeSourceRoot + "/Gear" })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Select(path => (path, value: AssetDatabase.LoadAssetAtPath<GearConfig>(path)))
            .Where(pair => pair.value != null && !string.IsNullOrWhiteSpace(pair.value.gearId))
            .GroupBy(pair => pair.value.gearId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().path, StringComparer.Ordinal);

        var ids = new HashSet<string>(PrototypeContentCatalog.CreateDefaults().Keys, StringComparer.Ordinal);
        // Some player-buildable visuals are presentation variants rather than
        // standalone simulation objects. The indoor furniture.hearth is drawn
        // for campfire.spot, so it is absent from PrototypeContentCatalog but
        // is still an independent logical ContentObject and must be published.
        ids.UnionWith(BuildCatalogDefinition.All.Select(entry => entry.DefinitionId));
        // Construction art includes presentation variants (for example a flat
        // roof) without a standalone simulation definition. The stage manifest
        // owns these IDs too; omitting them leaves valid exported FBXs unbuildable.
        var construction = JObject.Parse(File.ReadAllText(HexLive.Editor.ConstructionStageBuildGate.ManifestPath));
        ids.UnionWith(construction["definitions"].Select(entry => (string)entry["id"]));
        ids.UnionWith(worldConfigs.Keys);
        ids.UnionWith(gearConfigs.Keys);
        const string modernArtManifest = "Assets/HexLiveContent/Portal/modern-apartment-art.json";
        if (File.Exists(modernArtManifest))
        {
            var modernArt = JObject.Parse(File.ReadAllText(modernArtManifest));
            ids.UnionWith(modernArt["records"].Select(record => (string)record["id"]));
        }
        foreach (var id in ids.OrderBy(value => value, StringComparer.Ordinal))
        {
            var type = ObjectType(id);
            var isPortalOwner = PortalContentPolicy.IsApproved(type, id);
            if (portalOnly && !isPortalOwner && id is not ("chair.basic" or ContentIds.Workbench)) continue;
            var main = FindObjectMain(id);
            if (main == null)
            {
                continue;
            }
            var capturedMain = main;
            worldConfigs.TryGetValue(id, out var worldConfig);
            gearConfigs.TryGetValue(id, out var gearConfig);
            add(type, id, () =>
            {
                var folders = new JArray();
                var entries = new List<ContentEntry>();
                if (!string.IsNullOrEmpty(worldConfig))
                {
                    folders.Add(WorldObjectConfig.ResourceFolder);
                    entries.Add(new ContentEntry { name = "world-config", asset = worldConfig });
                }
                if (!string.IsNullOrEmpty(gearConfig))
                {
                    folders.Add(GearConfig.ResourceFolder);
                    entries.Add(new ContentEntry { name = "gear-config", asset = gearConfig });
                }
                var metadata = new JObject
                {
                    ["legacyResourcePath"] = "HexLive/Objects/" + id,
                    ["legacyResourceFolders"] = folders,
                };
                if (isPortalOwner || PortalContentPolicy.IsModernWorldOwner(type, id))
                    metadata["contentOrigin"] = PortalContentPolicy.Origin;
                else if (portalOnly) metadata["contentOrigin"] = "primal";
                if (isPortalOwner)
                    metadata["acquisition"] = PortalContentPolicy.Acquisition;
                var inputs = new BuildInputs
                {
                    Main = capturedMain,
                    Metadata = CreateGeneratedMetadata(type, id, metadata),
                    Icon = $"{IconRoot}/{id}.png",
                    DisplayName = id,
                    Entries = entries.ToArray(),
                    ExtraMetadata = metadata,
                };
                return ResolveOwnerIcon(type, id, inputs);
            });
        }
    }

    // Read-only authoring preflight: every stage owner must resolve to its
    // approved native FBX through the same discovery used by individual builds.
    public static void ValidateConstructionRecipes()
    {
        try
        {
            var recipes = DiscoverAllRecipes();
            var definitions = JObject.Parse(File.ReadAllText(
                HexLive.Editor.ConstructionStageBuildGate.ManifestPath))["definitions"];
            foreach (var definition in definitions)
            {
                var id = (string)definition["id"];
                var type = ObjectType(id);
                var recipe = recipes.SingleOrDefault(value => value.Type == type && value.Id == id);
                if (recipe == null || recipe.Inputs().Main != (string)definition["export"])
                    throw new InvalidOperationException("Construction owner does not resolve to approved FBX: " + type + "/" + id);
            }
            Debug.Log($"[AtomicContent] Construction recipes PASS: {definitions.Count()} owners resolve to approved FBXs.");
        }
        finally { CleanupGeneratedMetadata(); }
    }

    private static void DiscoverMobRecipes(Action<string, string, Func<BuildInputs>> add)
    {
        foreach (var path in AssetDatabase.FindAssets(
                     "t:MobConfig", new[] { RuntimeSourceRoot + "/Mobs" })
                     .Select(AssetDatabase.GUIDToAssetPath))
        {
            var config = AssetDatabase.LoadAssetAtPath<MobConfig>(path);
            if (config == null || string.IsNullOrWhiteSpace(config.MobId))
            {
                continue;
            }
            var id = config.MobId;
            add("mob", id, () => ResolveOwnerIcon("mob", id, ResolveMob(id)));
        }
    }

    private static void DiscoverGenericRecipes(Action<string, string, Func<BuildInputs>> add)
    {
        var skipped = new[] { "/Actors/", "/Wear/", "/Objects/", "/Mobs/", "/Animals/",
            "/WorldObjects/", "/Gear/", "/UI/" };
        var authoringOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            RuntimeSourceRoot + "/ActorAppearanceCatalog.asset",
            RuntimeSourceRoot + "/GarmentCatalog.asset",
        };
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".asset", ".prefab", ".fbx", ".anim", ".controller", ".mat", ".shader",
            ".png", ".jpg", ".jpeg", ".tga", ".uxml", ".uss", ".json",
        };
        var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var guid in AssetDatabase.FindAssets(string.Empty, new[] { RuntimeSourceRoot }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if ((UsePeopleCatalog && (IsLegacyPeopleMap(path) ||
                    path.Contains("/Eyes/", StringComparison.Ordinal) ||
                    path.Contains("/Helmets/", StringComparison.Ordinal))) ||
                authoringOnly.Contains(path) ||
                skipped.Any(value => path.Contains(value, StringComparison.Ordinal)) ||
                !allowed.Contains(Path.GetExtension(path)) ||
                AssetDatabase.IsValidFolder(path))
            {
                continue;
            }
            var identity = GenericIdentity(path);
            if (identity.type == null)
            {
                continue;
            }
            var key = identity.type + "\n" + identity.id;
            if (!grouped.TryGetValue(key, out var paths))
            {
                paths = new List<string>();
                grouped[key] = paths;
            }
            paths.Add(path);
        }

        foreach (var pair in grouped.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            var split = pair.Key.Split('\n');
            var type = split[0];
            var id = split[1];
            var paths = pair.Value.OrderBy(GenericPriority).ThenBy(value => value, StringComparer.Ordinal)
                .ToArray();
            add(type, id, () =>
            {
                var main = paths[0];
                var legacyPath = LegacyPath(main);
                var metadata = new JObject
                {
                    ["legacyResourcePath"] = legacyPath,
                    ["legacyResourceFolder"] = legacyPath.Contains('/')
                        ? legacyPath[..legacyPath.LastIndexOf('/')]
                        : "HexLive",
                };
                var entries = new List<ContentEntry>();
                var style = paths.FirstOrDefault(value => value.EndsWith(".uss", StringComparison.OrdinalIgnoreCase));
                if (style != null && style != main)
                {
                    entries.Add(new ContentEntry { name = "style", asset = style });
                }
                return new BuildInputs
                {
                    Main = main,
                    Metadata = CreateGeneratedMetadata(type, id, metadata),
                    DisplayName = id,
                    Entries = entries.ToArray(),
                    ExtraMetadata = metadata,
                };
            });
        }
    }

    private static BuildInputs ResolveOwnerIcon(string type, string id, BuildInputs inputs)
    {
        if (!IconBearingTypes.Contains(type) ||
            AssetDatabase.LoadMainAssetAtPath(inputs.Icon) != null)
        {
            return inputs;
        }
        // An icon is optional author art. Do not bake a fake image merely to
        // satisfy the transport schema: the runtime already owns a stable
        // per-item/category emoji and can show it immediately without network
        // content. A real image, when authored later, still lives only in this
        // owner's next atomic revision.
        inputs.Icon = null;
        inputs.ExtraMetadata.Remove("iconPlaceholder");
        inputs.ExtraMetadata["iconFallback"] = "emoji";
        Debug.Log($"[AtomicContent] {type}/{id} has no authored icon; using Player emoji fallback.");
        return inputs;
    }

    private static string FindObjectMain(string id)
    {
        var root = RuntimeSourceRoot + "/Objects/";
        var native = WorldPropResources.NativeName(id);
        foreach (var name in new[] { native, id })
        {
            foreach (var extension in new[] { ".prefab", ".fbx" })
            {
                var path = root + name + extension;
                if (AssetDatabase.LoadMainAssetAtPath(path) != null)
                {
                    return path;
                }
            }
            foreach (var path in Directory.GetFiles(root, name + "__*.*")
                         .Select(value => value.Replace('\\', '/'))
                         .OrderBy(value => value, StringComparer.Ordinal))
            {
                if (Path.GetExtension(path) is ".prefab" or ".fbx" &&
                    AssetDatabase.LoadMainAssetAtPath(path) != null)
                {
                    return path;
                }
            }
        }
        return null;
    }

    private static string FindFormerResource(string formerPath)
    {
        if (string.IsNullOrWhiteSpace(formerPath) ||
            !formerPath.StartsWith("HexLive/", StringComparison.Ordinal))
        {
            return null;
        }
        var basePath = RuntimeSourceRoot + "/" + formerPath[8..];
        foreach (var extension in new[] { ".prefab", ".fbx", ".asset" })
        {
            if (AssetDatabase.LoadMainAssetAtPath(basePath + extension) != null)
            {
                return basePath + extension;
            }
        }
        return null;
    }

    private static string ObjectType(string id) =>
        id.StartsWith("building.", StringComparison.Ordinal) ||
        id.StartsWith("architecture.", StringComparison.Ordinal)
            ? "building"
            : "object";

    private static (string type, string id) GenericIdentity(string path)
    {
        var legacy = LegacyPath(path);
        var relative = legacy.StartsWith("HexLive/", StringComparison.Ordinal) ? legacy[8..] : legacy;
        var slash = relative.IndexOf('/');
        var family = slash < 0 ? relative : relative[..slash];
        var tail = slash < 0 ? relative : relative[(slash + 1)..];
        return family switch
        {
            "VFX" => ("vfx", Stable(tail)),
            "Decals" => ("vfx", "decal." + Stable(tail)),
            "BloodStainMats" => ("vfx", "blood-material." + Stable(tail)),
            "BloodStains" => ("vfx", "blood." + Stable(tail)),
            "BloodStainNormals" => ("vfx", "blood-normal." + Stable(tail)),
            "Remains" => ("vfx", "remains." + Stable(tail)),
            "Water" => ("vfx", "water." + Stable(tail)),
            "Shaders" => ("vfx", "shader." + Stable(tail)),
            _ => ("config", Stable(relative)),
        };
    }

    private static string LegacyPath(string assetPath)
    {
        var relative = assetPath[(RuntimeSourceRoot.Length + 1)..];
        return "HexLive/" + relative[..^Path.GetExtension(relative).Length];
    }

    private static string Stable(string value) => value
        .Replace('/', '.')
        .Replace(' ', '-')
        .ToLowerInvariant();

    private static int GenericPriority(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".uxml" => 0,
        ".asset" => 1,
        ".prefab" => 2,
        ".fbx" => 3,
        ".anim" => 4,
        ".shader" => 5,
        ".mat" => 6,
        ".png" => 7,
        _ => 20,
    };

    private static BuildInputs ResolveInputs(string type, string id, string[] arguments)
    {
        var explicitMain = Optional(arguments, "-content-main");
        var explicitIcon = Optional(arguments, "-content-icon");
        var explicitMetadata = Optional(arguments, "-content-metadata");
        if (!string.IsNullOrEmpty(explicitMain))
        {
            return ResolveOwnerIcon(type, id, new BuildInputs
            {
                Main = explicitMain,
                Metadata = explicitMetadata ?? throw new InvalidOperationException(
                    "An explicit -content-main also requires -content-metadata."),
                Icon = explicitIcon ?? (IconBearingTypes.Contains(type)
                    ? $"{IconRoot}/{id}.png"
                    : null),
                DisplayName = id,
            });
        }

        BuildInputs resolved;
        if (UsePeopleCatalog)
        {
            var entry = PeopleRecords().FirstOrDefault(r => (string)r["type"] == type && (string)r["id"] == id);
            if (entry != null) { resolved = ResolvePeople(entry); goto resolved_input; }
            if (type is "actor" or "wear" or "hair")
                throw new InvalidOperationException("Object is outside the new people catalog: " + type + "/" + id);
        }
        if (type == "wear")
        {
            resolved = ResolveWear(id);
        }
        else if (type == "hair")
        {
            resolved = ResolveHair(id);
        }
        else if (type == "prosthetic")
        {
            resolved = ResolveProsthetic(id);
        }
        else if (type == "actor")
        {
            resolved = ResolveConvention(
                type, id,
                $"Assets/HexLiveContent/RuntimeSource/Actors/{id}.prefab",
                $"{IconRoot}/actor.{id}.png",
                "HexLive/Actors/" + id);
        }
        else if (type == "mob")
        {
            resolved = ResolveMob(id);
        }
        else
        {
            var discovered = DiscoverAllRecipes().FirstOrDefault(value =>
                string.Equals(value.Type, type, StringComparison.Ordinal) &&
                string.Equals(value.Id, id, StringComparison.Ordinal));
            if (discovered != null)
            {
                resolved = discovered.Inputs();
                goto resolved_input;
            }

            var descriptorPath = $"{DescriptorRoot}/{type}/{id}.json";
            var descriptorAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(descriptorPath);
            if (descriptorAsset == null)
            {
                throw new InvalidOperationException(
                    $"No authoring descriptor '{descriptorPath}'. Add it or pass " +
                    "--main/--metadata/--icon to Tools/content.py build.");
            }

            var descriptor = JsonUtility.FromJson<Descriptor>(descriptorAsset.text);
            if (descriptor == null || string.IsNullOrEmpty(descriptor.main))
            {
                throw new InvalidOperationException($"Descriptor '{descriptorPath}' has no main asset.");
            }

            var descriptorJson = JObject.Parse(descriptorAsset.text);
            resolved = new BuildInputs
            {
                Main = descriptor.main,
                Metadata = descriptorPath,
                Icon = string.IsNullOrEmpty(descriptor.icon) && IconBearingTypes.Contains(type)
                    ? $"{IconRoot}/{id}.png"
                    : descriptor.icon,
                DisplayName = string.IsNullOrEmpty(descriptor.displayName) ? id : descriptor.displayName,
                Entries = descriptor.entries ?? Array.Empty<ContentEntry>(),
                ExtraMetadata = descriptorJson["metadata"] as JObject ?? new JObject(),
            };
        }

    resolved_input:
        if (!string.IsNullOrEmpty(explicitMetadata))
        {
            resolved.Metadata = explicitMetadata;
        }
        if (!string.IsNullOrEmpty(explicitIcon))
        {
            resolved.Icon = explicitIcon;
        }
        return ResolveOwnerIcon(type, id, resolved);
    }

    private static BuildInputs ResolveMob(string id)
    {
        var match = AssetDatabase.FindAssets(
                "t:MobConfig", new[] { RuntimeSourceRoot + "/Mobs" })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Select(path => (path, config: AssetDatabase.LoadAssetAtPath<MobConfig>(path)))
            .FirstOrDefault(pair => pair.config != null &&
                string.Equals(pair.config.MobId, id, StringComparison.Ordinal));
        if (match.config == null)
        {
            throw new InvalidOperationException($"Unknown mob '{id}'.");
        }

        var main = match.config.prefab != null
            ? AssetDatabase.GetAssetPath(match.config.prefab)
            : FindFormerResource(match.config.prefabResourcePath);
        if (string.IsNullOrEmpty(main))
        {
            throw new InvalidOperationException($"Mob '{id}' has no prefab asset.");
        }
        var metadata = new JObject
        {
            ["legacyResourcePath"] = match.config.prefabResourcePath,
            ["legacyResourceFolders"] = new JArray("HexLive/Mobs"),
        };
        return new BuildInputs
        {
            Main = main,
            Metadata = CreateGeneratedMetadata("mob", id, metadata),
            Icon = $"{IconRoot}/mob.{id}.png",
            DisplayName = id,
            Entries = new[] { new ContentEntry { name = "mob-config", asset = match.path } },
            ExtraMetadata = metadata,
        };
    }

    private static BuildInputs ResolveWear(string id)
    {
        GarmentDefinition definition = null;
        string definitionPath = null;
        foreach (var guid in AssetDatabase.FindAssets("t:GarmentDefinition", new[] { WearDefinitions }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var candidate = AssetDatabase.LoadAssetAtPath<GarmentDefinition>(path);
            if (candidate != null && string.Equals(candidate.id, id, StringComparison.Ordinal))
            {
                definition = candidate;
                definitionPath = path;
                break;
            }
        }

        if (definition == null)
        {
            throw new InvalidOperationException($"Unknown garment '{id}'.");
        }

        var prefabs = AssetDatabase.FindAssets("t:Prefab", new[] { $"{WearRoot}/{definition.ArtId}" })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(path => path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (prefabs.Length != 1)
        {
            throw new InvalidOperationException(
                $"Garment '{id}' must resolve to exactly one prefab in {WearRoot}/{definition.ArtId}; " +
                $"found {prefabs.Length}.");
        }

        var icon = $"{IconRoot}/{id}.png";
        return new BuildInputs
        {
            Main = prefabs[0],
            Metadata = definitionPath,
            State = definition.retired ? "retired" : "active",
            Icon = icon,
            DisplayName = string.IsNullOrEmpty(definition.displayName) ? id : definition.displayName,
            ArtId = definition.ArtId,
            Category = definition.category.ToString(),
            IsWear = true,
            Layer = definition.layer.ToString(),
            Covers = definition.covers.Select(value => value.ToString()).ToArray(),
            Slots = WearSlotCatalog.For(definition.id).Select(value => value.ToString()).ToArray(),
            Warmth = definition.warmth,
            Armor = definition.armor,
            ThermalDelta = definition.thermalDelta,
            DressDurationTicks = definition.dressDurationTicks,
            Capacity = definition.capacity,
            Sex = Sex(definition.id),
        };
    }

    private static BuildInputs ResolveHair(string id)
    {
        var root = $"Assets/ImportedActors/Hair/{id}";
        var main = $"{root}/{id}.prefab";
        var entries = new List<ContentEntry>();
        var colours = new JArray();
        var materialRoot = root + "/Materials";
        if (Directory.Exists(materialRoot))
        {
            foreach (var colourFolder in Directory.GetDirectories(materialRoot)
                         .OrderBy(value => value, StringComparer.Ordinal))
            {
                var colour = Path.GetFileName(colourFolder);
                var surfaces = new JArray();
                foreach (var file in Directory.GetFiles(colourFolder, "*.mat")
                             .OrderBy(value => value, StringComparer.Ordinal))
                {
                    var asset = file.Replace('\\', '/');
                    var surface = Path.GetFileNameWithoutExtension(asset);
                    entries.Add(new ContentEntry
                    {
                        name = $"colour/{colour}/{surface}",
                        asset = asset,
                    });
                    surfaces.Add(surface);
                }
                if (surfaces.Count > 0)
                {
                    colours.Add(new JObject
                    {
                        ["id"] = colour,
                        ["surfaces"] = surfaces,
                    });
                }
            }
        }

        var metadata = new JObject
        {
            ["legacyResourcePath"] = "HexLive/Hair/" + id,
            ["colours"] = colours,
        };
        return new BuildInputs
        {
            Main = main,
            Metadata = CreateGeneratedMetadata("hair", id, metadata),
            Icon = $"{IconRoot}/hair.{id}.png",
            DisplayName = id,
            Entries = entries.ToArray(),
            ExtraMetadata = metadata,
        };
    }

    private static BuildInputs ResolveProsthetic(string id)
    {
        var sourceName = "prosthetic_" + id.Replace('.', '_');
        return ResolveConvention(
            "prosthetic", id,
            $"Assets/HexLiveContent/Prosthetics/{sourceName}.fbx",
            $"{IconRoot}/{sourceName}.png",
            "HexLive/Prosthetics/" + sourceName);
    }

    private static BuildInputs ResolveConvention(
        string type, string id, string main, string icon, string legacyPath)
    {
        var metadata = new JObject { ["legacyResourcePath"] = legacyPath };
        return new BuildInputs
        {
            Main = main,
            Metadata = CreateGeneratedMetadata(type, id, metadata),
            Icon = icon,
            DisplayName = id,
            ExtraMetadata = metadata,
        };
    }

    private static string CreateGeneratedMetadata(string type, string id, JObject metadata)
    {
        EnsureGeneratedFolder();

        _temporaryMetadata = $"{GeneratedMetadataRoot}/{type}.{id}.json";
        var value = new JObject
        {
            ["type"] = type,
            ["id"] = id,
            ["metadata"] = metadata.DeepClone(),
        };
        File.WriteAllText(
            _temporaryMetadata, value.ToString(Formatting.Indented) + "\n",
            new UTF8Encoding(false));
        File.WriteAllText(
            _temporaryMetadata + ".meta",
            "fileFormatVersion: 2\n" +
            $"guid: {DeterministicToken("metadata", type, id)}\n" +
            "TextScriptImporter:\n" +
            "  externalObjects: {}\n" +
            "  userData: \n" +
            "  assetBundleName: \n" +
            "  assetBundleVariant: \n",
            new UTF8Encoding(false));
        AssetDatabase.ImportAsset(_temporaryMetadata, ImportAssetOptions.ForceSynchronousImport);
        return _temporaryMetadata;
    }

    private static string DeterministicToken(string purpose, string type, string id)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(
            $"hexlive-atomic-generated-v1\n{purpose}\n{type}\n{id}"));
        return BitConverter.ToString(bytes).Replace("-", string.Empty)
            .ToLowerInvariant()[..32];
    }

    private static void EnsureGeneratedFolder()
    {
        if (AssetDatabase.IsValidFolder(GeneratedMetadataRoot))
        {
            return;
        }
        if (!AssetDatabase.IsValidFolder(DescriptorRoot))
        {
            throw new InvalidOperationException($"Missing descriptor root '{DescriptorRoot}'.");
        }
        AssetDatabase.CreateFolder(DescriptorRoot, "Generated");
    }

    private static void ValidateEntries(
        string payload, IReadOnlyList<string> expected, string type, string id)
    {
        var bundle = AssetBundle.LoadFromFile(payload);
        if (bundle == null)
        {
            throw new InvalidOperationException($"Built payload '{payload}' cannot be opened.");
        }

        try
        {
            foreach (var entry in expected)
            {
                if (!bundle.Contains(entry))
                {
                    throw new InvalidOperationException(
                        $"Built payload does not expose required entry '{entry}'.");
                }
            }

            // All physical content families must provide an actually visible
            // root. A non-null empty prefab (or config-only bundle) used to pass
            // Contains("main") and later freeze a primitive fallback into the
            // world. VFX/audio/config have different payload semantics and are
            // validated by their family-specific gates.
            if (IconBearingTypes.Contains(type))
            {
                var main = bundle.LoadAsset<GameObject>("main");
                if (main == null ||
                    !HexLive.UnityPresentation.ObjectFit.HasRenderableGeometry(main))
                {
                    throw new InvalidOperationException(
                        $"Built payload {type}/{id} has no renderable GameObject entry 'main'.");
                }
            }
        }
        finally
        {
            bundle.Unload(true);
        }
    }

    private static void ValidateAsset(string path, string entry)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal) ||
            AssetDatabase.LoadMainAssetAtPath(path) == null)
        {
            throw new InvalidOperationException(
                $"Required '{entry}' asset is missing or outside Assets: '{path ?? "<null>"}'.");
        }
    }

    private static void DeleteOldOutput(string output)
    {
        foreach (var file in Directory.GetFiles(output, "*", SearchOption.TopDirectoryOnly))
        {
            if (!string.Equals(Path.GetFileName(file), "unity.log", StringComparison.Ordinal))
            {
                File.Delete(file);
            }
        }
    }

    private static void ValidateIdentity(
        string type, string id, string platform, string runtimeProfile)
    {
        if (!Types.Contains(type) || !Safe(id, 128) || !Safe(platform, 64) ||
            !Safe(runtimeProfile, 64))
        {
            throw new InvalidOperationException("Invalid content type/id/platform/runtime profile.");
        }
    }

    private static bool Safe(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength ||
            !char.IsLetterOrDigit(value[0]))
        {
            return false;
        }

        return value.All(character =>
            char.IsLetterOrDigit(character) || character is '.' or '_' or '-' or ' ');
    }

    private static bool SafeEntry(string value) => value.Length <= 160 && value.All(character =>
        char.IsLetterOrDigit(character) || character is '.' or '_' or '-' or '/' or ' ');

    private static string Required(string[] arguments, string name) =>
        Optional(arguments, name) ?? throw new InvalidOperationException($"Missing {name}.");

    private static string Optional(string[] arguments, string name)
    {
        for (var index = 0; index + 1 < arguments.Length; index++)
        {
            if (arguments[index] == name)
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private static IEnumerable<string> Values(string[] arguments, string name)
    {
        for (var index = 0; index + 1 < arguments.Length; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.Ordinal))
            {
                yield return arguments[index + 1];
                index++;
            }
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty)
            .ToLowerInvariant();
    }

    private static string CandidateJson(
        string type,
        string id,
        string platform,
        string runtimeProfile,
        string payload,
        string sha256,
        long size,
        BuildInputs inputs)
    {
        var text = new StringBuilder();
        text.Append("{\n");
        text.Append("  \"type\": ").Append(Quote(type)).Append(",\n");
        text.Append("  \"id\": ").Append(Quote(id)).Append(",\n");
        text.Append("  \"state\": ").Append(Quote(inputs.State)).Append(",\n");
        text.Append("  \"metadata\": {\n");
        text.Append("    \"displayName\": ").Append(Quote(inputs.DisplayName)).Append(",\n");
        text.Append("    \"hasIcon\": ").Append(string.IsNullOrEmpty(inputs.Icon) ? "false" : "true");
        if (!string.IsNullOrEmpty(inputs.ArtId))
        {
            text.Append(",\n    \"artId\": ").Append(Quote(inputs.ArtId));
        }
        if (!string.IsNullOrEmpty(inputs.Category))
        {
            text.Append(",\n    \"category\": ").Append(Quote(inputs.Category));
        }
        if (inputs.IsWear)
        {
            text.Append(",\n    \"layer\": ").Append(Quote(inputs.Layer));
            text.Append(",\n    \"covers\": ").Append(ArrayJson(inputs.Covers));
            text.Append(",\n    \"slots\": ").Append(ArrayJson(inputs.Slots));
            text.Append(",\n    \"warmth\": ").Append(Number(inputs.Warmth));
            text.Append(",\n    \"armor\": ").Append(Number(inputs.Armor));
            text.Append(",\n    \"thermalDelta\": ").Append(Number(inputs.ThermalDelta));
            text.Append(",\n    \"dressDurationTicks\": ").Append(inputs.DressDurationTicks);
            text.Append(",\n    \"capacity\": ").Append(inputs.Capacity);
            text.Append(",\n    \"sex\": ").Append(Quote(inputs.Sex));
        }
        foreach (var property in inputs.ExtraMetadata.Properties())
        {
            if (property.Name is "displayName" or "hasIcon")
            {
                continue;
            }
            text.Append(",\n    ").Append(Quote(property.Name)).Append(": ")
                .Append(property.Value.ToString(Formatting.None));
        }
        text.Append("\n  },\n");
        text.Append("  \"variants\": [\n    {\n");
        text.Append("      \"platform\": ").Append(Quote(platform)).Append(",\n");
        text.Append("      \"runtimeProfile\": ").Append(Quote(runtimeProfile)).Append(",\n");
        text.Append("      \"sha256\": ").Append(Quote(sha256)).Append(",\n");
        text.Append("      \"size\": ").Append(size).Append(",\n");
        text.Append("      \"payloadType\": \"assetBundle\",\n");
        text.Append("      \"entryAsset\": \"main\",\n");
        if (!string.IsNullOrEmpty(inputs.Icon))
        {
            text.Append("      \"iconAsset\": \"icon\",\n");
        }
        text.Append("      \"stagedPath\": ").Append(Quote(payload.Replace('\\', '/'))).Append("\n");
        text.Append("    }\n  ]\n}\n");
        return text.ToString();
    }

    private static string Quote(string value) =>
        "\"" + (value ?? string.Empty)
            .Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\n", "\\n").Replace("\r", "\\r") + "\"";

    private static string ArrayJson(IEnumerable<string> values) =>
        "[" + string.Join(", ", values.Select(Quote)) + "]";

    private static string Number(float value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string Sex(string id)
    {
        foreach (var garment in GarmentLibrary.Defaults)
        {
            if (garment.Id == id)
            {
                return garment.Sex.ToString();
            }
        }
        return GarmentSex.Any.ToString();
    }
}
#endif
