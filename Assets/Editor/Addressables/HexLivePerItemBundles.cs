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
//  Один бандл на вещь — отдельный файл, в котором лежит ВСЁ про эту вещь.
//
//  Зачем именно так. Упаковка по умолчанию складывает всю группу в один файл:
//  поменял одно платье — пересобрал и раздал весь гардероб. При упаковке
//  «файл на вещь» меняется один маленький файл, и его достаточно положить в
//  папку контента.
//
//  Как это делается в Addressables: группе ставится режим PackTogetherByLabel,
//  а каждому ассету вещи вешается СВОЯ метка `wear.<artId>`. Всё, что помечено
//  одной меткой, уезжает в один бандл. Метка тут работает не как «тег для
//  поиска», а как ИМЯ БАНДЛА.
//
//  В бандл вещи кладётся ТОЛЬКО её арт — префаб и всё, что он тянет за собой
//  (меши, текстуры, материалы). Ни иконок, ни определений, ни строк: игре
//  достаточно ИМЕНИ бандла, чтобы знать, что такая вещь есть, а имя она берёт
//  из каталога Addressables, который и так читает на старте.
//
//  ⚠️ Цена решения: общие зависимости (шейдер, общая текстура) ДУБЛИРУЮТСЯ в
//  каждый бандл, который их использует. Это и есть плата за независимые файлы,
//  и её надо мерить после сборки, а не оценивать на глаз.
//
//  Menu: HexLive ▸ Addressables ▸ Разложить по бандлам (файл на вещь)
// ---------------------------------------------------------------------------
public static class HexLivePerItemBundles
{
    private const string WearRoot = "Assets/HexLiveContent/Wear";
    private const string IconRoot = "Assets/HexLiveContent/Icons";
    private const string HairRoot = "Assets/ImportedActors/Hair";

    public static string WearLabel(string artId) => $"wear.{artId}";
    public static string HairLabel(string hair) => $"hair.{hair}";

    [MenuItem("HexLive/Addressables/Разложить по бандлам (файл на вещь)")]
    public static void Pack()
    {
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null)
        {
            Debug.LogError("[Addressables] сначала «Настроить проект».");
            return;
        }

        // Сначала чистка: в группе могли остаться записи от прежней затеи
        // (иконки, определения). Лишняя запись — это лишний ассет в бандле, а
        // молча раздувшийся бандл потом ищи.
        var dropped = DropForeign(settings, HexLiveAddressablesContent.WearGroup, "wear/", "icon/");
        dropped += DropForeign(settings, HexLiveAddressablesContent.HairGroup, "hair/");

        var wear = LabelWear(settings);
        var hair = LabelHair(settings);
        ByLabel(settings, HexLiveAddressablesContent.WearGroup);
        ByLabel(settings, HexLiveAddressablesContent.HairGroup);

        AssetDatabase.SaveAssets();
        Debug.Log($"[Addressables] бандлов будет: вещей {wear}, причёсок {hair}.\n" +
                  "Каждый — отдельный файл: поменял одну вещь, положил один файл.");
    }

    // Метка на всё, что принадлежит ОДНОЙ вещи. Ключ связи — artId: расцветка
    // носит арт своего прототипа, поэтому её иконка и её материалы едут в тот
    // же бандл, что и геометрия, а не в свой собственный.
    // Всё, что не адресуется нашим правилом, группе не принадлежит.
    private static int DropForeign(AddressableAssetSettings settings, string groupName,
                                   params string[] prefixes)
    {
        var group = settings.FindGroup(groupName);
        if (group == null)
        {
            return 0;
        }

        var doomed = group.entries
            .Where(e => e != null && (string.IsNullOrEmpty(e.address) ||
                                      !prefixes.Any(p => e.address.StartsWith(p))))
            .ToList();

        foreach (var entry in doomed)
        {
            settings.RemoveAssetEntry(entry.guid, false);
        }

        return doomed.Count;
    }

    private static int LabelWear(AddressableAssetSettings settings)
    {
        var group = settings.FindGroup(HexLiveAddressablesContent.WearGroup);
        if (group == null)
        {
            Debug.LogError("[Addressables] нет группы вещей — сначала «Разметить гардероб и волосы».");
            return 0;
        }

        var labelled = 0;

        foreach (var directory in Directory.GetDirectories(WearRoot))
        {
            var artId = Path.GetFileName(directory);
            var label = WearLabel(artId);
            settings.AddLabel(label, false);

            foreach (var file in Directory.GetFiles(directory, "*.prefab"))
            {
                labelled += Tag(settings, group, file.Replace('\\', '/'), label) ? 1 : 0;
            }

            // Иконки этой вещи — её собственная и всех её расцветок. Метка ТА ЖЕ,
            // поэтому они ложатся в бандл своей вещи, а не в отдельный.
            // Узнаются по ИМЕНИ ФАЙЛА: иконка расцветки названа её id, а он
            // начинается с id прототипа. Реестр для этого не нужен.
            if (Directory.Exists(IconRoot))
            {
                foreach (var icon in Directory.GetFiles(IconRoot, artId + "*.png"))
                {
                    var iconPath = icon.Replace('\\', '/');
                    var iconName = Path.GetFileNameWithoutExtension(iconPath);
                    labelled += TagIcon(settings, group, iconPath, label, "icon/" + iconName) ? 1 : 0;
                }
            }

        }

        return Directory.GetDirectories(WearRoot).Length;
    }

    private static int LabelHair(AddressableAssetSettings settings)
    {
        var group = settings.FindGroup(HexLiveAddressablesContent.HairGroup);
        if (group == null)
        {
            return 0;
        }

        var count = 0;
        foreach (var directory in Directory.GetDirectories(HairRoot))
        {
            var name = Path.GetFileName(directory);
            var label = HairLabel(name);
            settings.AddLabel(label, false);

            var prefab = $"{HairRoot}/{name}/{name}.prefab";
            if (File.Exists(prefab))
            {
                Tag(settings, group, prefab, label);
                count++;
            }

            var materials = $"{HairRoot}/{name}/Materials";
            if (!Directory.Exists(materials))
            {
                continue;
            }

            foreach (var colourFolder in Directory.GetDirectories(materials))
            {
                foreach (var file in Directory.GetFiles(colourFolder, "*.mat"))
                {
                    Tag(settings, group, file.Replace('\\', '/'), label);
                }
            }
        }

        return count;
    }

    // id вещи -> artId. Читается из ассетов определений: prototypeId пустой
    // значит «арт свой», иначе арт прототипа (§31B.4E).
    private static void ByLabel(AddressableAssetSettings settings, string groupName)
    {
        var group = settings.FindGroup(groupName);
        var schema = group != null ? group.GetSchema<BundledAssetGroupSchema>() : null;
        if (schema == null)
        {
            return;
        }

        schema.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogetherByLabel;
        EditorUtility.SetDirty(schema);
    }

    // Иконке нужен СВОЙ адрес (icon/<id>) — правило адреса вещи её не описывает.
    // Метка при этом та же, что у вещи: бандл один на вещь.
    private static bool TagIcon(AddressableAssetSettings settings, AddressableAssetGroup group,
                                string path, string label, string address)
    {
        var guid = AssetDatabase.AssetPathToGUID(path);
        if (string.IsNullOrEmpty(guid))
        {
            return false;
        }

        var entry = settings.CreateOrMoveEntry(guid, group, false, false);
        if (entry == null)
        {
            return false;
        }

        entry.address = address;
        entry.SetLabel(label, true, false, false);
        return true;
    }

    private static bool Tag(AddressableAssetSettings settings, AddressableAssetGroup group,
                            string path, string label)
    {
        var guid = AssetDatabase.AssetPathToGUID(path);
        if (string.IsNullOrEmpty(guid))
        {
            return false;
        }

        var entry = settings.CreateOrMoveEntry(guid, group, false, false);
        if (entry == null)
        {
            return false;
        }

        entry.SetLabel(label, true, false, false);
        return true;
    }

}
#endif
