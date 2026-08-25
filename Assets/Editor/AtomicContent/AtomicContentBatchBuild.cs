#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HexLive.UnityPresentation.Wearing.Garments;
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
        "Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear";
    private const string WearRoot = "Assets/HexLiveContent/Wear";
    private const string IconRoot = "Assets/HexLiveContent/Icons";
    private const string DescriptorRoot = "Assets/AtomicContent";
    private const string GeneratedMetadataRoot = "Assets/AtomicContent/Generated";
    private static string _temporaryMetadata;

    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "wear", "actor", "hair", "prosthetic", "object", "building",
        "mob", "ui", "vfx", "audio", "config",
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

    public static void Build()
    {
        var succeeded = false;
        try
        {
            var arguments = Environment.GetCommandLineArgs();
            var type = Required(arguments, "-content-type");
            var id = Required(arguments, "-content-id");
            var platform = Required(arguments, "-content-platform");
            var output = Path.GetFullPath(Required(arguments, "-content-output"));
            var runtimeProfile = Optional(arguments, "-content-runtime-profile") ?? RuntimeProfile;
            ValidateIdentity(type, id, platform, runtimeProfile);

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

            var inputs = ResolveInputs(type, id, arguments);
            ValidateAsset(inputs.Main, "main");
            ValidateAsset(inputs.Metadata, "metadata");
            if (IconBearingTypes.Contains(type))
            {
                ValidateAsset(inputs.Icon, "icon");
            }
            else if (!string.IsNullOrEmpty(inputs.Icon))
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

            var build = new AssetBundleBuild
            {
                assetBundleName = PayloadName,
                assetNames = assetNames.ToArray(),
                addressableNames = entryNames.ToArray(),
            };
            var manifest = BuildPipeline.BuildAssetBundles(
                output,
                new[] { build },
                BuildAssetBundleOptions.ChunkBasedCompression |
                BuildAssetBundleOptions.ForceRebuildAssetBundle |
                BuildAssetBundleOptions.StrictMode,
                target);
            if (manifest == null)
            {
                throw new InvalidOperationException("BuildPipeline returned no AssetBundleManifest.");
            }

            var built = manifest.GetAllAssetBundles();
            if (built.Length != 1 || built[0] != PayloadName)
            {
                throw new InvalidOperationException(
                    "Atomic build emitted unexpected bundles: " + string.Join(", ", built));
            }

            var dependencies = manifest.GetAllDependencies(PayloadName);
            if (dependencies.Length != 0)
            {
                throw new InvalidOperationException(
                    $"{type}/{id} is not self-contained; external dependencies: " +
                    string.Join(", ", dependencies));
            }

            var payload = Path.Combine(output, PayloadName);
            ValidateEntries(payload, entryNames);
            var sha256 = Hash(payload);
            var size = new FileInfo(payload).Length;
            var candidate = CandidateJson(
                type, id, platform, runtimeProfile, payload, sha256, size, inputs);
            File.WriteAllText(
                Path.Combine(output, "candidate.json"), candidate, new UTF8Encoding(false));

            Debug.Log(
                $"[AtomicContent] built {type}/{id} for {platform}/{runtimeProfile}: " +
                $"{size} bytes, sha256={sha256}, dependencies=0, " +
                $"icon={(string.IsNullOrEmpty(inputs.Icon) ? "none" : "embedded")}");
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
            if (!string.IsNullOrEmpty(_temporaryMetadata))
            {
                AssetDatabase.DeleteAsset(_temporaryMetadata);
                _temporaryMetadata = null;
            }
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

    private static BuildInputs ResolveInputs(string type, string id, string[] arguments)
    {
        var explicitMain = Optional(arguments, "-content-main");
        var explicitIcon = Optional(arguments, "-content-icon");
        var explicitMetadata = Optional(arguments, "-content-metadata");
        if (!string.IsNullOrEmpty(explicitMain))
        {
            return new BuildInputs
            {
                Main = explicitMain,
                Metadata = explicitMetadata ?? throw new InvalidOperationException(
                    "An explicit -content-main also requires -content-metadata."),
                Icon = explicitIcon ?? (IconBearingTypes.Contains(type)
                    ? $"{IconRoot}/{id}.png"
                    : null),
                DisplayName = id,
            };
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
        return resolved;
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
        if (!AssetDatabase.IsValidFolder(GeneratedMetadataRoot))
        {
            if (!AssetDatabase.IsValidFolder(DescriptorRoot))
            {
                throw new InvalidOperationException($"Missing descriptor root '{DescriptorRoot}'.");
            }
            AssetDatabase.CreateFolder(DescriptorRoot, "Generated");
        }

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
        AssetDatabase.ImportAsset(_temporaryMetadata, ImportAssetOptions.ForceSynchronousImport);
        return _temporaryMetadata;
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
            char.IsLetterOrDigit(character) || character is '.' or '_' or '-');
    }

    private static bool SafeEntry(string value) => value.Length <= 160 && value.All(character =>
        char.IsLetterOrDigit(character) || character is '.' or '_' or '-' or '/');

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
