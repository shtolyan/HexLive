#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build.Pipeline.Utilities;
using UnityEngine;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceLocations;

// ---------------------------------------------------------------------------
//  Маленький патч иконок для уже установленной macOS-сборки.
//
//  Обычный BuildPlayerContent строит каталог по ВСЕМ группам. Если положить
//  такой каталог поверх старой установки, он переключит 390 wear/hair ссылок
//  на новые хэши и потребует заново раздать весь многогигабайтный гардероб.
//
//  Этот builder делает другое:
//    1. временно включает только HexLive.Icons и собирает один маленький bundle;
//    2. читает catalog, который УЖЕ лежит рядом с установленной игрой;
//    3. дописывает в него только icon/* и их bundle-зависимости;
//    4. проверяет, что каждый старый ключ разрешается ровно в те же локации;
//    5. кладёт рядом с игрой только новый bundle + catalog.bin + catalog.hash.
//
//  Все временные настройки восстанавливаются в finally. Полная Addressables-
//  сборка не запускается и существующие wear/hair bundles не копируются.
// ---------------------------------------------------------------------------
public static class HexLiveIconPatch
{
    private const string MenuPath =
        "HexLive/Addressables/Собрать и установить только недостающие иконки";
    private const string TargetArgument = "-hexlive-icon-target";
    private const string PatchBuildRoot = "Build/IconPatch";
    private const string PatchStateRoot = "Build/IconPatchState";
    private const string PatchVersion = "icon_patch";
    private const long MaxPatchBundleBytes = 64L * 1024L * 1024L;

    [MenuItem(MenuPath)]
    public static void BuildAndDeploy()
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneOSX)
        {
            throw new InvalidOperationException(
                "[IconPatch] активная цель должна быть StandaloneOSX.");
        }

        var settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null)
        {
            throw new InvalidOperationException("[IconPatch] Addressables settings не найдены.");
        }

        var iconGroup = settings.FindGroup(HexLiveAddressablesContent.IconGroup);
        if (iconGroup == null || iconGroup.entries.Count == 0)
        {
            throw new InvalidOperationException(
                "[IconPatch] группа HexLive.Icons пуста — сначала запусти разметку адресов.");
        }

        var addresses = iconGroup.entries
            .Where(entry => entry != null && entry.address.StartsWith("icon/", StringComparison.Ordinal))
            .Select(entry => entry.address)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(address => address, StringComparer.Ordinal)
            .ToArray();
        if (addresses.Length == 0)
        {
            throw new InvalidOperationException("[IconPatch] в группе нет icon/* адресов.");
        }

        var target = ResolveTargetFolder();
        var baselineCatalog = SingleCatalog(target);
        var baselineHash = Path.ChangeExtension(baselineCatalog, ".hash");
        if (!File.Exists(baselineHash))
        {
            throw new FileNotFoundException("[IconPatch] рядом с catalog нет hash.", baselineHash);
        }

        var installedLocator = CreateLocator(LoadCatalog(baselineCatalog));
        var alreadyInstalled = addresses.Count(address =>
            installedLocator.Locate(address, typeof(Sprite), out var locations) &&
            locations.Count > 0);
        if (alreadyInstalled == addresses.Length)
        {
            Debug.Log($"[IconPatch] все {addresses.Length} иконок уже установлены: {target}");
            return;
        }

        if (alreadyInstalled > 0)
        {
            throw new InvalidOperationException(
                $"[IconPatch] catalog частично пропатчен: найдено {alreadyInstalled} " +
                $"из {addresses.Length} icon/* адресов. Сначала восстанови baseline-backup.");
        }

        var projectRoot = Path.GetDirectoryName(Application.dataPath) ?? ".";
        var patchRoot = Path.GetFullPath(Path.Combine(projectRoot, PatchBuildRoot));
        var patchPlatform = Path.Combine(patchRoot, "StandaloneOSX");
        RecreateDirectory(patchRoot);

        BuildIconsOnly(settings, iconGroup);

        var patchCatalog = SingleCatalog(patchPlatform);
        var mergedCatalog = Path.Combine(patchRoot, Path.GetFileName(baselineCatalog));
        var mergedHash = Path.ChangeExtension(mergedCatalog, ".hash");

        var patchBundles = MergeCatalogs(
            baselineCatalog, patchCatalog, mergedCatalog, addresses, patchPlatform);
        var patchBytes = patchBundles.Sum(path => new FileInfo(path).Length);
        if (patchBytes <= 0 || patchBytes > MaxPatchBundleBytes)
        {
            throw new InvalidOperationException(
                $"[IconPatch] подозрительный размер bundle-патча: {patchBytes / 1024f / 1024f:0.0} MiB.");
        }

        File.WriteAllText(mergedHash,
            HashingMethods.Calculate(File.ReadAllBytes(mergedCatalog)).ToString());

        var backup = Path.Combine(patchRoot, "baseline-backup");
        Directory.CreateDirectory(backup);
        File.Copy(baselineCatalog, Path.Combine(backup, Path.GetFileName(baselineCatalog)), true);
        File.Copy(baselineHash, Path.Combine(backup, Path.GetFileName(baselineHash)), true);

        // Bundles first, catalog second, hash last. If a player is launched in
        // the middle, the old hash keeps it on the old valid catalog until all
        // payload files are already in place.
        foreach (var bundle in patchBundles)
        {
            DeployFile(bundle, Path.Combine(target, Path.GetFileName(bundle)));
        }

        DeployFile(mergedCatalog, baselineCatalog);
        DeployFile(mergedHash, baselineHash);

        Debug.Log($"[IconPatch] ГОТОВО: адресов {addresses.Length}, " +
                  $"bundle-файлов {patchBundles.Count}, " +
                  $"размер {patchBytes / 1024f / 1024f:0.0} MiB -> {target}\n" +
                  string.Join("\n", patchBundles.Select(Path.GetFileName)));
    }

    private static void BuildIconsOnly(
        AddressableAssetSettings settings, AddressableAssetGroup iconGroup)
    {
        var include = settings.groups
            .Where(group => group != null)
            .ToDictionary(group => group, group => group.IncludeInBuild);
        var profiles = settings.profileSettings;
        var profileId = settings.activeProfileId;
        var oldBuildPath = profiles.GetValueByName(
            profileId, HexLiveAddressablesSetup.BuildPathVariable);
        var oldContentStatePath = settings.ContentStateBuildPath;
        var oldVersion = settings.OverridePlayerVersion;

        try
        {
            foreach (var group in include.Keys)
            {
                group.IncludeInBuild = group == iconGroup;
            }

            var schema = iconGroup.GetSchema<BundledAssetGroupSchema>();
            if (schema == null)
            {
                throw new InvalidOperationException(
                    "[IconPatch] у HexLive.Icons нет BundledAssetGroupSchema.");
            }

            schema.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
            HexLiveAddressablesSetup.PointGroupOutside(settings, iconGroup);
            profiles.SetValue(
                profileId, HexLiveAddressablesSetup.BuildPathVariable,
                PatchBuildRoot + "/[BuildTarget]");
            settings.ContentStateBuildPath = PatchStateRoot;
            settings.OverridePlayerVersion = PatchVersion;

            var input = new AddressablesDataBuilderInput(settings)
            {
                PlayerVersion = PatchVersion
            };
            var result = settings.ActivePlayerDataBuilder
                .BuildData<AddressablesPlayerBuildResult>(input);
            if (result == null || !string.IsNullOrEmpty(result.Error))
            {
                throw new InvalidOperationException(
                    "[IconPatch] сборка группы провалилась: " +
                    (result?.Error ?? "нет результата"));
            }
        }
        finally
        {
            foreach (var pair in include)
            {
                pair.Key.IncludeInBuild = pair.Value;
            }

            profiles.SetValue(
                profileId, HexLiveAddressablesSetup.BuildPathVariable, oldBuildPath);
            settings.ContentStateBuildPath = oldContentStatePath;
            settings.OverridePlayerVersion = oldVersion;
            AssetDatabase.SaveAssets();
        }
    }

    private static List<string> MergeCatalogs(
        string baselinePath,
        string patchPath,
        string outputPath,
        IReadOnlyCollection<string> addresses,
        string patchFolder)
    {
        var baseline = LoadCatalog(baselinePath);
        var patch = LoadCatalog(patchPath);
        var baselineLocator = CreateLocator(baseline);
        var patchLocator = CreateLocator(patch);

        var patchRecords = ReadRecords(patchLocator);
        var selected = new Dictionary<string, LocationRecord>(StringComparer.Ordinal);
        var rootsByAddress = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var address in addresses)
        {
            if (baselineLocator.Locate(address, typeof(Sprite), out var existing) &&
                existing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"[IconPatch] установленный catalog уже содержит {address}; " +
                    "повторно накладывать патч нельзя.");
            }

            if (!patchLocator.Locate(address, typeof(Sprite), out var roots) || roots.Count == 0)
            {
                throw new InvalidOperationException(
                    $"[IconPatch] временный catalog не разрешает {address} как Sprite.");
            }

            rootsByAddress[address] = roots.Select(LocationId).ToList();
            foreach (var root in roots)
            {
                SelectWithDependencies(root, patchRecords, selected);
            }
        }

        // Never deserialize + reserialize the installed entries. A catalog can
        // outlive the exact player/package build that authored it; rewriting
        // its Type records made apparently identical UnityEngine.GameObject
        // types fail IsAssignableFrom in that player. Seed Addressables' binary
        // writer with the original bytes, append only the new icon locations,
        // append a new key table, then change the single header pointer to it.
        // Every old string, Type, provider option and location offset remains
        // byte-for-byte untouched.
        var baselineBytes = File.ReadAllBytes(baselinePath);
        var oldKeys = ReadRawKeys(baselineBytes);
        var writer = CreateAppendWriter(baselineBytes);

        var entryById = selected.ToDictionary(
            pair => pair.Key, pair => ToEntry(pair.Value), StringComparer.Ordinal);
        var entries = entryById.Values.ToList();
        var offsets = AppendLocations(writer, entries);
        var appendedKeys = new List<RawKeyData>(addresses.Count);
        foreach (var address in addresses)
        {
            var locationOffsets = rootsByAddress[address]
                .Select(id => offsets[entryById[id]])
                .Distinct()
                .ToArray();
            appendedKeys.Add(new RawKeyData
            {
                KeyNameOffset = WriteObject(writer, address, true),
                LocationSetOffset = WriteArray(writer, locationOffsets)
            });
        }

        oldKeys.AddRange(appendedKeys);
        var newKeysOffset = WriteArray(writer, oldKeys.ToArray());
        var mergedBytes = SerializeWriter(writer);
        WriteUInt32(mergedBytes, HeaderKeysOffset, newKeysOffset);
        File.WriteAllBytes(outputPath, mergedBytes);

        var mergedLoaded = LoadCatalog(outputPath);
        var mergedLocator = CreateLocator(mergedLoaded);
        ValidateBaselineUnchanged(baselineLocator, mergedLocator);

        foreach (var address in addresses)
        {
            if (!mergedLocator.Locate(address, typeof(Sprite), out var locations) || locations.Count == 0)
            {
                throw new InvalidOperationException(
                    $"[IconPatch] после merge не разрешается {address}.");
            }
        }

        var bundleNames = selected.Values
            .Select(record => record.Location.InternalId)
            .Where(id => id.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
            .Select(BundleFileName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (bundleNames.Length == 0)
        {
            throw new InvalidOperationException("[IconPatch] catalog не сослался ни на один bundle.");
        }

        var built = new List<string>(bundleNames.Length);
        foreach (var name in bundleNames)
        {
            var matches = Directory.GetFiles(patchFolder, name, SearchOption.AllDirectories);
            if (matches.Length != 1)
            {
                throw new FileNotFoundException(
                    $"[IconPatch] bundle {name}: найдено файлов {matches.Length}.");
            }

            built.Add(matches[0]);
        }

        return built;
    }

    private const int HeaderKeysOffset = 8;
    private const int RawKeyDataSize = 8;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct RawKeyData
    {
        public uint KeyNameOffset;
        public uint LocationSetOffset;
    }

    private static List<RawKeyData> ReadRawKeys(byte[] catalog)
    {
        if (catalog == null || catalog.Length < HeaderKeysOffset + sizeof(uint))
        {
            throw new InvalidDataException("[IconPatch] catalog слишком короткий.");
        }

        var keysOffset = ReadUInt32(catalog, HeaderKeysOffset);
        if (keysOffset < sizeof(uint) || keysOffset > catalog.Length)
        {
            throw new InvalidDataException("[IconPatch] catalog содержит битый keysOffset.");
        }

        var byteCount = ReadUInt32(catalog, checked((int)keysOffset - sizeof(uint)));
        if (byteCount % RawKeyDataSize != 0 ||
            (ulong)keysOffset + byteCount > (ulong)catalog.Length)
        {
            throw new InvalidDataException("[IconPatch] catalog содержит битую таблицу keys.");
        }

        var result = new List<RawKeyData>((int)byteCount / RawKeyDataSize);
        for (var offset = (int)keysOffset;
             offset < (int)keysOffset + byteCount;
             offset += RawKeyDataSize)
        {
            result.Add(new RawKeyData
            {
                KeyNameOffset = ReadUInt32(catalog, offset),
                LocationSetOffset = ReadUInt32(catalog, offset + sizeof(uint))
            });
        }

        return result;
    }

    private static object CreateAppendWriter(byte[] baseline)
    {
        var resourceManagerAssembly = typeof(IResourceLocation).Assembly;
        var bufferType = resourceManagerAssembly.GetType(
            "UnityEngine.ResourceManagement.Util.BinaryStorageBuffer", true);
        var writerType = bufferType.GetNestedType(
            "Writer", BindingFlags.Public | BindingFlags.NonPublic);
        var adapterType = bufferType.GetNestedType(
            "ISerializationAdapter", BindingFlags.Public | BindingFlags.NonPublic);
        var serializerType = typeof(BinaryContentCatalogData).GetNestedType(
            "Serializer", BindingFlags.NonPublic);
        if (writerType == null || adapterType == null || serializerType == null)
        {
            throw new MissingMemberException(
                "[IconPatch] внутренний binary writer Addressables не найден.");
        }

        var serializer = Activator.CreateInstance(serializerType, true);
        var adapters = Array.CreateInstance(adapterType, 1);
        adapters.SetValue(serializer, 0);
        var writer = Activator.CreateInstance(
            writerType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new object[] { 0, adapters },
            null);

        var chunksField = writerType.GetField(
            "chunks", BindingFlags.Instance | BindingFlags.NonPublic);
        var totalBytesField = writerType.GetField(
            "totalBytes", BindingFlags.Instance | BindingFlags.NonPublic);
        var chunks = chunksField?.GetValue(writer) as IList;
        if (chunks == null || chunks.Count != 1 || totalBytesField == null)
        {
            throw new MissingMemberException(
                "[IconPatch] структура binary writer Addressables изменилась.");
        }

        var chunk = chunks[0];
        var chunkType = chunk.GetType();
        chunkType.GetField("data", BindingFlags.Instance | BindingFlags.Public)
            ?.SetValue(chunk, (byte[])baseline.Clone());
        chunkType.GetField("position", BindingFlags.Instance | BindingFlags.Public)
            ?.SetValue(chunk, checked((uint)baseline.Length));
        totalBytesField.SetValue(writer, checked((uint)baseline.Length));
        return writer;
    }

    private static Dictionary<ContentCatalogDataEntry, uint> AppendLocations(
        object writer, IList<ContentCatalogDataEntry> entries)
    {
        var locatorType = typeof(BinaryContentCatalogData).GetNestedType(
            "ResourceLocator", BindingFlags.NonPublic);
        var contextType = locatorType?.GetNestedType(
            "ContentCatalogDataEntrySerializationContext",
            BindingFlags.Public | BindingFlags.NonPublic);
        if (contextType == null)
        {
            throw new MissingMemberException(
                "[IconPatch] serialization context Addressables не найден.");
        }

        var keyToIndices = new Dictionary<object, List<int>>();
        for (var i = 0; i < entries.Count; i++)
        {
            foreach (var key in entries[i].Keys)
            {
                if (!keyToIndices.TryGetValue(key, out var indices))
                {
                    keyToIndices.Add(key, indices = new List<int>());
                }

                indices.Add(i);
            }
        }

        var offsets = new Dictionary<ContentCatalogDataEntry, uint>();
        foreach (var entry in entries)
        {
            var context = Activator.CreateInstance(contextType, true);
            contextType.GetField("entry")?.SetValue(context, entry);
            contextType.GetField("allEntries")?.SetValue(context, entries);
            contextType.GetField("keyToEntryIndices")?.SetValue(context, keyToIndices);
            contextType.GetField("entryOffsets")?.SetValue(context, offsets);
            WriteObject(writer, context, false);
        }

        if (offsets.Count != entries.Count)
        {
            throw new InvalidOperationException(
                $"[IconPatch] записано locations {offsets.Count}, ожидалось {entries.Count}.");
        }

        return offsets;
    }

    private static uint WriteObject(object writer, object value, bool serializeTypeData)
    {
        var method = writer.GetType().GetMethod(
            "WriteObject", BindingFlags.Instance | BindingFlags.Public);
        if (method == null)
        {
            throw new MissingMethodException("BinaryStorageBuffer.Writer.WriteObject");
        }

        return (uint)method.Invoke(writer, new[] { value, (object)serializeTypeData });
    }

    private static uint WriteArray<T>(object writer, T[] values) where T : struct
    {
        var method = writer.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(candidate =>
                candidate.Name == "Write" &&
                candidate.IsGenericMethodDefinition &&
                candidate.GetParameters().Length == 2 &&
                candidate.GetParameters()[0].ParameterType.IsArray);
        return (uint)method.MakeGenericMethod(typeof(T))
            .Invoke(writer, new object[] { values, false });
    }

    private static byte[] SerializeWriter(object writer)
    {
        var method = writer.GetType().GetMethod(
            "SerializeToByteArray", BindingFlags.Instance | BindingFlags.Public);
        if (method == null)
        {
            throw new MissingMethodException(
                "BinaryStorageBuffer.Writer.SerializeToByteArray");
        }

        return (byte[])method.Invoke(writer, null);
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + sizeof(uint) > bytes.Length)
        {
            throw new InvalidDataException("[IconPatch] uint offset вне catalog.");
        }

        return BitConverter.ToUInt32(bytes, offset);
    }

    private static void WriteUInt32(byte[] bytes, int offset, uint value)
    {
        var encoded = BitConverter.GetBytes(value);
        Buffer.BlockCopy(encoded, 0, bytes, offset, encoded.Length);
    }

    private static Dictionary<string, LocationRecord> ReadRecords(IResourceLocator locator)
    {
        var records = new Dictionary<string, LocationRecord>(StringComparer.Ordinal);
        foreach (var key in locator.Keys)
        {
            if (!locator.Locate(key, typeof(object), out var locations))
            {
                continue;
            }

            foreach (var location in locations)
            {
                var id = LocationId(location);
                if (!records.TryGetValue(id, out var record))
                {
                    record = new LocationRecord(location);
                    records.Add(id, record);
                }

                record.Keys.Add(key);
                record.Keys.Add(location.PrimaryKey);
            }
        }

        return records;
    }

    private static void ValidateBaselineUnchanged(
        IResourceLocator baseline, IResourceLocator merged)
    {
        foreach (var key in baseline.Keys)
        {
            if (!baseline.Locate(key, typeof(object), out var before) || before.Count == 0)
            {
                continue;
            }

            if (!merged.Locate(key, typeof(object), out var after) || after.Count == 0)
            {
                throw new InvalidOperationException(
                    $"[IconPatch] после merge потерян старый catalog key: {key}");
            }

            var beforeIds = before.Select(LocationId).OrderBy(id => id, StringComparer.Ordinal);
            var afterIds = after.Select(LocationId).OrderBy(id => id, StringComparer.Ordinal);
            if (!beforeIds.SequenceEqual(afterIds, StringComparer.Ordinal))
            {
                var beforeText = string.Join("\n  BEFORE ", beforeIds);
                var afterText = string.Join("\n  AFTER  ", afterIds);
                throw new InvalidOperationException(
                    $"[IconPatch] после merge изменились старые локации key: {key}\n" +
                    $"  BEFORE {beforeText}\n  AFTER  {afterText}");
            }
        }
    }

    private static void SelectWithDependencies(
        IResourceLocation location,
        IReadOnlyDictionary<string, LocationRecord> records,
        IDictionary<string, LocationRecord> selected)
    {
        var id = LocationId(location);
        if (selected.ContainsKey(id))
        {
            return;
        }

        if (!records.TryGetValue(id, out var record))
        {
            throw new InvalidOperationException(
                $"[IconPatch] catalog record не найден: {location.PrimaryKey}");
        }

        selected.Add(id, record);
        if (!location.HasDependencies)
        {
            return;
        }

        foreach (var dependency in location.Dependencies)
        {
            SelectWithDependencies(dependency, records, selected);
        }
    }

    private static ContentCatalogDataEntry ToEntry(LocationRecord record)
    {
        var location = record.Location;
        // The first catalog key becomes IResourceLocation.PrimaryKey. Keep it
        // byte-for-byte semantically stable; HashSet enumeration would make a
        // legacy alias such as "wear.FAO …" replace canonical "wear/FAO …".
        var keys = new List<object> { location.PrimaryKey };
        keys.AddRange(record.Keys.Where(key => !Equals(key, location.PrimaryKey)));
        var dependencies = location.HasDependencies
            ? location.Dependencies.Select(dependency => (object)dependency.PrimaryKey).ToList()
            : null;
        return new ContentCatalogDataEntry(
            location.ResourceType,
            location.InternalId,
            location.ProviderId,
            keys,
            dependencies,
            location.Data);
    }

    private static BinaryContentCatalogData LoadCatalog(string path)
    {
        var method = typeof(BinaryContentCatalogData).GetMethod(
            "LoadFromFile", BindingFlags.Static | BindingFlags.NonPublic);
        if (method == null)
        {
            throw new MissingMethodException("BinaryContentCatalogData.LoadFromFile");
        }

        return (BinaryContentCatalogData)method.Invoke(null, new object[] { path, false });
    }

    private static IResourceLocator CreateLocator(BinaryContentCatalogData catalog)
    {
        var method = typeof(BinaryContentCatalogData).GetMethod(
            "CreateCustomLocator", BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null)
        {
            throw new MissingMethodException("BinaryContentCatalogData.CreateCustomLocator");
        }

        return (IResourceLocator)method.Invoke(catalog, new object[] { string.Empty, null });
    }

    private static string ResolveTargetFolder()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == TargetArgument)
            {
                return RequireDirectory(args[i + 1]);
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return RequireDirectory(Path.Combine(
            home, "hex-girls", HexLiveAddressablesSetup.ContentFolderName, "StandaloneOSX"));
    }

    private static string RequireDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException(
                $"[IconPatch] папка установленного контента не найдена: {full}");
        }

        return full;
    }

    private static string SingleCatalog(string folder)
    {
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException(folder);
        }

        var catalogs = Directory.GetFiles(folder, "catalog_*.bin", SearchOption.TopDirectoryOnly);
        if (catalogs.Length != 1)
        {
            throw new InvalidOperationException(
                $"[IconPatch] ожидался один catalog_*.bin в {folder}, найдено {catalogs.Length}.");
        }

        return catalogs[0];
    }

    private static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }

        Directory.CreateDirectory(path);
    }

    private static void DeployFile(string source, string destination)
    {
        var temporary = destination + ".iconpatch.tmp";
        File.Copy(source, temporary, true);
        if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        File.Move(temporary, destination);
    }

    private static string BundleFileName(string internalId)
    {
        var normalized = internalId.Replace('\\', '/');
        var query = normalized.IndexOf('?');
        if (query >= 0)
        {
            normalized = normalized.Substring(0, query);
        }

        return normalized.Substring(normalized.LastIndexOf('/') + 1);
    }

    private static string LocationId(IResourceLocation location) =>
        location.ProviderId + "\n" +
        location.ResourceType.AssemblyQualifiedName + "\n" +
        location.PrimaryKey + "\n" +
        location.InternalId;

    private sealed class LocationRecord
    {
        public LocationRecord(IResourceLocation location)
        {
            Location = location;
        }

        public IResourceLocation Location { get; }

        public HashSet<object> Keys { get; } = new();
    }
}
#endif
