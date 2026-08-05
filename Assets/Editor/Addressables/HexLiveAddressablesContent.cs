#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

// ---------------------------------------------------------------------------
//  Разметка гардероба и волос адресами.
//
//  Адрес — это ДОГОВОР между кодом и контентом, поэтому он строится по правилу,
//  а не назначается руками:
//
//      hair/<Причёска>                      — префаб причёски
//      hair/<Причёска>/<Цвет>/<Поверхность> — материал расцветки
//      wear/<artId>                         — префаб арта вещи
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
    public const string WearGroup = "HexLive.Wear";

    private const string HairRoot = "Assets/ImportedActors/Hair";
    private const string WearResources = "Assets/Resources/HexLive/Wear";

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

        AssetDatabase.SaveAssets();
        Debug.Log($"[Addressables] размечено: причёсок и их расцветок — {hair} адресов, " +
                  $"арта вещей — {wear} адресов.\n" +
                  "Теперь каталоги должны хранить СТРОКИ вместо ссылок — иначе всё это " +
                  "по-прежнему поедет в билд.");
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
        if (!Directory.Exists(WearResources))
        {
            Debug.LogWarning($"[Addressables] нет папки {WearResources} — арт вещей не размечен.");
            return 0;
        }

        var group = GetOrCreateGroup(settings, WearGroup);
        var marked = 0;

        foreach (var directory in Directory.GetDirectories(WearResources))
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
        foreach (var group in settings.groups)
        {
            if (group == null) continue;
            foreach (var entry in group.entries)
            {
                if (entry != null && !addresses.Add(entry.address))
                {
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

        if (missing.Count > 0)
        {
            Debug.LogError($"[Addressables] причёски БЕЗ адреса ({missing.Count}): " +
                           string.Join(", ", missing));
        }
        else
        {
            Debug.Log($"[Addressables] адресов всего {addresses.Count}, дублей нет, " +
                      "все причёски каталога адресуемы.");
        }
    }
}
#endif
