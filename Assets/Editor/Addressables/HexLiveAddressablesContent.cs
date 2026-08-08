#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

// ---------------------------------------------------------------------------
//  Разметка внешнего контента адресами.
//
//  Адрес — это ДОГОВОР между кодом и контентом, поэтому он строится по правилу,
//  а не назначается руками:
//
//      hair/<Причёска>                      — префаб причёски
//      hair/<Причёска>/<Цвет>/<Поверхность> — материал расцветки
//      wear/<artId>                         — префаб арта вещи
//      prosthetic/<limb>/<tier>/<side>       — модель протеза
//
//  Почему материал адресуется поштучно, а не набором: подмена цвета и так идёт
//  ПО ИМЕНИ ПОВЕРХНОСТИ (порядок сабмешей у причёски не гарантирован), значит
//  имя поверхности уже есть на руках в момент загрузки — и адрес собирается из
//  того, что известно. Каталогу тогда достаточно СТРОК, а строки ничего не
//  тянут в билд. Прямая ссылка на материал тянула бы.
//
//  ⭐ Именно это — суть перехода. Пока `ActorAppearanceCatalog` держит 254
//  набора материалов ПРЯМЫМИ ссылками, они попадут в билд, сколько Addressables
//  ни настраивай: в билд едет всё, до чего дотянулась ссылка из Resources.
//
//  Menu: HexLive ▸ Addressables ▸ Разметить гардероб и волосы
// ---------------------------------------------------------------------------
public static class HexLiveAddressablesContent
{
    public const string HairGroup = "HexLive.Hair";
    public const string IconGroup = "HexLive.Icons";
    public const string ProstheticGroup = "HexLive.Prosthetics";
    public const string WearGroup = "HexLive.Wear";
    public const string ProstheticLabel = "prosthetic.core";

    private const string HairRoot = "Assets/ImportedActors/Hair";
    private const string IconRoot = "Assets/HexLiveContent/Icons";
    private const string ProstheticRoot = "Assets/HexLiveContent/Prosthetics";
    private const string LegacyProstheticResources = "Assets/Resources/HexLive/Prosthetics";
    private const string WearRoot = "Assets/HexLiveContent/Wear";

    private static readonly string[] ExpectedProstheticAddresses =
    {
        "prosthetic/arm/wood/l",
        "prosthetic/arm/wood/r",
        "prosthetic/leg/wood/l",
        "prosthetic/leg/wood/r",
        "prosthetic/arm/mechanical/l",
        "prosthetic/arm/mechanical/r",
        "prosthetic/leg/mechanical/l",
        "prosthetic/leg/mechanical/r",
    };

    public static string HairAddress(string hair) => $"hair/{hair}";

    public static string HairColourAddress(string hair, string colour, string surface) =>
        $"hair/{hair}/{colour}/{surface}";

    public static string WearAddress(string artId) => $"wear/{artId}";

    [MenuItem("HexLive/Addressables/Разметить гардероб и волосы")]
    public static void MarkAll()
    {
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null)
        {
            Debug.LogError("[Addressables] сначала «Настроить проект».");
            return;
        }

        var hair = MarkHair(settings);
        var wear = MarkWear(settings);
        var icons = MarkStandaloneIcons(settings);
        var prosthetics = MarkProsthetics(settings);

        AssetDatabase.SaveAssets();
        Debug.Log($"[Addressables] размечено: причёсок и их расцветок — {hair} адресов, " +
                  $"арта вещей — {wear} адресов, отдельных иконок — {icons}, " +
                  $"протезов — {prosthetics}.\n" +
                  "Теперь каталоги должны хранить СТРОКИ вместо ссылок — иначе всё это " +
                  "по-прежнему поедет в билд.");
    }

    private static int MarkProsthetics(AddressableAssetSettings settings)
    {
        if (!Directory.Exists(ProstheticRoot))
        {
            Debug.LogError($"[Addressables] нет папки {ProstheticRoot} — протезы не размечены.");
            return 0;
        }

        var group = GetOrCreateGroup(settings, ProstheticGroup);
        var schema = group.GetSchema<BundledAssetGroupSchema>();
        if (schema != null)
        {
            schema.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogetherByLabel;
            EditorUtility.SetDirty(schema);
        }

        settings.AddLabel(ProstheticLabel, false);
        var marked = 0;
        foreach (var file in Directory.GetFiles(ProstheticRoot, "*.fbx"))
        {
            var path = file.Replace('\\', '/');
            if (!TryProstheticAddress(path, out var address))
            {
                Debug.LogWarning($"[Addressables] неизвестное имя модели протеза: {path}");
                continue;
            }

            ConfigureProstheticImporter(path);
            if (!Mark(settings, group, path, address))
            {
                continue;
            }

            var entry = settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(path));
            entry?.SetLabel(ProstheticLabel, true, false, false);
            marked++;
        }

        return marked;
    }

    private static bool TryProstheticAddress(string path, out string address)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var parts = name.Split('_');
        if (parts.Length == 4 && parts[0] == "prosthetic" &&
            parts[1] is "arm" or "leg" &&
            parts[2] is "wood" or "mechanical" &&
            parts[3] is "l" or "r")
        {
            address = $"prosthetic/{parts[1]}/{parts[2]}/{parts[3]}";
            return true;
        }

        address = string.Empty;
        return false;
    }

    private static void ConfigureProstheticImporter(string path)
    {
        if (AssetImporter.GetAtPath(path) is not ModelImporter importer)
        {
            return;
        }

        var changed = false;
        if (!Mathf.Approximately(importer.globalScale, 1f))
        {
            importer.globalScale = 1f;
            changed = true;
        }
        if (importer.importAnimation)
        {
            importer.importAnimation = false;
            changed = true;
        }
        if (!importer.preserveHierarchy)
        {
            importer.preserveHierarchy = true;
            changed = true;
        }
        if (importer.isReadable)
        {
            importer.isReadable = false;
            changed = true;
        }
        if (!importer.useFileScale)
        {
            importer.useFileScale = true;
            changed = true;
        }
        if (importer.animationType != ModelImporterAnimationType.None)
        {
            importer.animationType = ModelImporterAnimationType.None;
            changed = true;
        }

        if (changed)
        {
            importer.SaveAndReimport();
        }
    }

    // Wear icons are already packed beside their garment by
    // HexLivePerItemBundles. Core inventory icons (food, resources and tools)
    // have no wear prefab and used to remain completely unaddressed after the
    // Resources move. Put only those otherwise-unowned PNGs into one small
    // standalone group; do not pull the 600+ garment icons out of their
    // per-item bundles.
    private static int MarkStandaloneIcons(AddressableAssetSettings settings)
    {
        if (!Directory.Exists(IconRoot))
        {
            Debug.LogWarning($"[Addressables] нет папки {IconRoot} — отдельные иконки не размечены.");
            return 0;
        }

        var group = GetOrCreateGroup(settings, IconGroup);
        var marked = 0;
        foreach (var file in Directory.GetFiles(IconRoot, "*.png"))
        {
            var path = file.Replace('\\', '/');
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid))
            {
                continue;
            }

            var expected = "icon/" + Path.GetFileNameWithoutExtension(path);
            var existing = settings.FindAssetEntry(guid);
            if (existing != null && existing.address == expected)
            {
                continue;
            }

            if (existing != null)
            {
                existing.address = expected;
                marked++;
            }
            else if (Mark(settings, group, path, expected))
            {
                marked++;
            }
        }

        return marked;
    }

    private static int MarkHair(AddressableAssetSettings settings)
    {
        var group = GetOrCreateGroup(settings, HairGroup);
        var marked = 0;

        foreach (var directory in Directory.GetDirectories(HairRoot))
        {
            var name = Path.GetFileName(directory);
            var prefab = $"{HairRoot}/{name}/{name}.prefab";
            if (File.Exists(prefab))
            {
                marked += Mark(settings, group, prefab, HairAddress(name)) ? 1 : 0;
            }

            var materials = $"{HairRoot}/{name}/Materials";
            if (!Directory.Exists(materials))
            {
                continue;
            }

            foreach (var colourFolder in Directory.GetDirectories(materials))
            {
                var colour = Path.GetFileName(colourFolder);
                foreach (var file in Directory.GetFiles(colourFolder, "*.mat"))
                {
                    var surface = Path.GetFileNameWithoutExtension(file);
                    var path = file.Replace('\\', '/');
                    marked += Mark(settings, group, path, HairColourAddress(name, colour, surface)) ? 1 : 0;
                }
            }
        }

        return marked;
    }

    // Арт вещи — это ПАПКА в Resources с префабом внутри. Адресуется префаб;
    // текстуры и меши едут за ним как зависимости, их метить не нужно и вредно:
    // помеченная зависимость выносится в свой бандл и перестаёт разделяться.
    private static int MarkWear(AddressableAssetSettings settings)
    {
        if (!Directory.Exists(WearRoot))
        {
            Debug.LogWarning($"[Addressables] нет папки {WearRoot} — арт вещей не размечен.");
            return 0;
        }

        var group = GetOrCreateGroup(settings, WearGroup);
        var marked = 0;

        foreach (var directory in Directory.GetDirectories(WearRoot))
        {
            var artId = Path.GetFileName(directory);
            foreach (var file in Directory.GetFiles(directory, "*.prefab"))
            {
                var path = file.Replace('\\', '/');
                marked += Mark(settings, group, path, WearAddress(artId)) ? 1 : 0;
            }
        }

        return marked;
    }

    private static AddressableAssetGroup GetOrCreateGroup(AddressableAssetSettings settings, string name)
    {
        var group = settings.FindGroup(name);
        if (group == null)
        {
            group = settings.CreateGroup(name, false, false, false, null,
                typeof(UnityEditor.AddressableAssets.Settings.GroupSchemas.BundledAssetGroupSchema),
                typeof(UnityEditor.AddressableAssets.Settings.GroupSchemas.ContentUpdateGroupSchema));
        }

        // Каждая группа — наружу, в папку рядом с игрой.
        HexLiveAddressablesSetup.PointGroupOutside(settings, group);
        return group;
    }

    private static bool Mark(AddressableAssetSettings settings, AddressableAssetGroup group,
                             string path, string address)
    {
        var guid = AssetDatabase.AssetPathToGUID(path);
        if (string.IsNullOrEmpty(guid))
        {
            Debug.LogWarning($"[Addressables] не нашёл ассет: {path}");
            return false;
        }

        var entry = settings.CreateOrMoveEntry(guid, group, false, false);
        if (entry == null)
        {
            return false;
        }

        entry.address = address;
        return true;
    }

    // Проверка ПО СОДЕРЖИМОМУ, а не по факту «меню отработало»: адрес,
    // которого нет, ломается только в билде и молча — загрузка вернёт null,
    // и девушка выйдет лысой без единой ошибки в консоли.
    [MenuItem("HexLive/Addressables/Проверить адреса")]
    public static void Validate()
    {
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null)
        {
            Debug.LogError("[Addressables] настроек нет.");
            return;
        }

        var addresses = new HashSet<string>();
        var duplicate = false;
        foreach (var group in settings.groups)
        {
            if (group == null) continue;
            foreach (var entry in group.entries)
            {
                if (entry != null && !addresses.Add(entry.address))
                {
                    duplicate = true;
                    Debug.LogError($"[Addressables] адрес ДВАЖДЫ: {entry.address}");
                }
            }
        }

        var missing = new List<string>();
        var catalog = HexLive.UnityPresentation.Wearing.ActorAppearanceCatalog.Instance;
        if (catalog != null)
        {
            foreach (var hairId in catalog.hairstyles)
            {
                if (string.IsNullOrEmpty(hairId)) continue;
                if (!addresses.Contains(HairAddress(hairId)))
                {
                    missing.Add(HairAddress(hairId));
                }
            }
        }

        if (Directory.Exists(IconRoot))
        {
            foreach (var file in Directory.GetFiles(IconRoot, "*.png"))
            {
                var path = file.Replace('\\', '/');
                var expected = "icon/" + Path.GetFileNameWithoutExtension(path);
                var entry = settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(path));
                if (entry == null || entry.address != expected)
                {
                    missing.Add(expected);
                }
            }
        }

        var prostheticGroup = settings.FindGroup(ProstheticGroup);
        if (prostheticGroup == null)
        {
            missing.Add(ProstheticGroup);
        }
        else
        {
            var prostheticFiles = Directory.Exists(ProstheticRoot)
                ? Directory.GetFiles(ProstheticRoot, "*.fbx")
                : System.Array.Empty<string>();
            if (prostheticFiles.Length != ExpectedProstheticAddresses.Length ||
                prostheticGroup.entries.Count != ExpectedProstheticAddresses.Length)
            {
                missing.Add($"{ProstheticGroup} [expected 8 assets, files=" +
                            $"{prostheticFiles.Length}, entries={prostheticGroup.entries.Count}]");
            }

            foreach (var expected in ExpectedProstheticAddresses)
            {
                var entry = prostheticGroup.entries.FirstOrDefault(candidate =>
                    candidate != null && candidate.address == expected);
                if (entry == null || !entry.labels.Contains(ProstheticLabel) ||
                    !entry.AssetPath.StartsWith(ProstheticRoot + "/"))
                {
                    missing.Add(expected);
                }
            }

            foreach (var file in prostheticFiles)
            {
                var path = file.Replace('\\', '/');
                if (!TryProstheticAddress(path, out var expected))
                {
                    missing.Add(path);
                    continue;
                }

                var entry = settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(path));
                if (entry == null || !prostheticGroup.entries.Contains(entry) ||
                    entry.address != expected || !entry.labels.Contains(ProstheticLabel))
                {
                    missing.Add(expected);
                }

                if (AssetImporter.GetAtPath(path) is not ModelImporter importer ||
                    !Mathf.Approximately(importer.globalScale, 1f) || importer.importAnimation ||
                    importer.animationType != ModelImporterAnimationType.None ||
                    !importer.preserveHierarchy || importer.isReadable || !importer.useFileScale)
                {
                    missing.Add(expected + " [import]");
                }
            }

            var schema = prostheticGroup.GetSchema<BundledAssetGroupSchema>();
            if (schema == null ||
                schema.BundleMode != BundledAssetGroupSchema.BundlePackingMode.PackTogetherByLabel ||
                schema.BuildPath.GetName(settings) != HexLiveAddressablesSetup.BuildPathVariable ||
                schema.LoadPath.GetName(settings) != HexLiveAddressablesSetup.LoadPathVariable)
            {
                missing.Add(ProstheticGroup + " [bundle mode/build/load path]");
            }
        }

        if (Directory.Exists(LegacyProstheticResources))
        {
            missing.Add(LegacyProstheticResources + " [must be external]");
        }

        if (missing.Count > 0 || duplicate)
        {
            Debug.LogError($"[Addressables] ассеты БЕЗ адреса ({missing.Count}): " +
                           string.Join(", ", missing));
        }
        else
        {
            Debug.Log($"[Addressables] адресов всего {addresses.Count}, дублей нет, " +
                      "причёски, иконки и восемь протезов адресуемы.");
        }
    }
}
#endif
