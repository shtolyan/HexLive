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

    private static void Run(bool buildAll)
    {
        var succeeded = false;
        try
        {
            var arguments = Environment.GetCommandLineArgs();
            var platform = Required(arguments, "-content-platform");
            var output = Path.GetFullPath(Required(arguments, "-content-output"));
            var runtimeProfile = Optional(arguments, "-content-runtime-profile") ?? RuntimeProfile;
            Directory.CreateDirectory(output);

            var target = ValidateTarget(platform);
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
                var recipes = DiscoverAllRecipes();
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
                foreach (var recipe in recipes)
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

                var summary = new JObject
                {
                    ["platform"] = platform,
                    ["runtimeProfile"] = runtimeProfile,
                    ["discovered"] = recipes.Count,
                    ["built"] = builtCount,
                    ["failed"] = failures.Count,
                    ["failures"] = failures,
                };
                File.WriteAllText(
                    Path.Combine(output, "build-all-summary.json"),
                    summary.ToString(Formatting.Indented) + "\n", new UTF8Encoding(false));
                if (failures.Count != 0)
                {
                    throw new InvalidOperationException(
                        $"Full atomic build failed for {failures.Count}/{recipes.Count} objects.");
                }
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
            target is not (BuildTarget.StandaloneOSX or BuildTarget.StandaloneWindows64))
        {
            throw new InvalidOperationException(
                $"Unsupported platform '{platform}'; use StandaloneOSX or StandaloneWindows64.");
        }
        if (EditorUserBuildSettings.activeBuildTarget != target)
        {
            throw new InvalidOperationException(
                $"Unity active target is {EditorUserBuildSettings.activeBuildTarget}, expected {target}. " +
                "Pass the matching -buildTarget before -executeMethod.");
        }
        return target;
    }

    private static void BuildObject(
        string type, string id, string platform, string runtimeProfile,
        BuildTarget target, string output, BuildInputs inputs)
    {
        ValidateAsset(inputs.Main, "main");
        ValidateAsset(inputs.Metadata, "metadata");
        if (!string.IsNullOrEmpty(inputs.Icon))
        {
            ValidateAsset(inputs.Icon, "icon");
        }

        Directory.CreateDirectory(output);
        DeleteOldOutput(output);
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
            assetNames = assetNames.ToArray(),
            addressableNames = entryNames.ToArray(),
        };
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
        ValidateEntries(payload, entryNames);
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
        foreach (var directory in Directory.GetDirectories(hairRoot)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            var id = Path.GetFileName(directory);
            var main = $"{hairRoot}/{id}/{id}.prefab";
            if (AssetDatabase.LoadMainAssetAtPath(main) != null)
            {
                Add("hair", id, () => ResolveOwnerIcon("hair", id, ResolveHair(id)));
            }
        }

        const string prostheticRoot = "Assets/HexLiveContent/Prosthetics";
        foreach (var path in Directory.GetFiles(prostheticRoot, "prosthetic_*.fbx")
                     .Select(value => value.Replace('\\', '/'))
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(path)["prosthetic_".Length..];
            var id = name.Replace('_', '.');
            Add("prosthetic", id,
                () => ResolveOwnerIcon("prosthetic", id, ResolveProsthetic(id)));
        }

        DiscoverObjectRecipes(Add);
        DiscoverMobRecipes(Add);
        DiscoverGenericRecipes(Add);
        return recipes.Values
            .OrderBy(value => value.Type, StringComparer.Ordinal)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static void DiscoverObjectRecipes(Action<string, string, Func<BuildInputs>> add)
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
        ids.UnionWith(worldConfigs.Keys);
        ids.UnionWith(gearConfigs.Keys);
        foreach (var id in ids.OrderBy(value => value, StringComparer.Ordinal))
        {
            var main = FindObjectMain(id);
            if (main == null)
            {
                continue;
            }
            var capturedMain = main;
            var type = ObjectType(id);
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
            if (authoringOnly.Contains(path) ||
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

    private static void ValidateEntries(string payload, IReadOnlyList<string> expected)
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
        text.Append("  \"state\": \"active\",\n");
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
