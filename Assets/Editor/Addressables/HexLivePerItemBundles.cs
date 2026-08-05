#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
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
//  Что попадает в бандл вещи:
//    * префаб арта,
//    * иконка прототипа И иконки всех её расцветок (расцветки делят геометрию,
//      но иконка у каждой своя),
//    * материалы расцветок,
//    * определение вещи (GarmentDefinition) — статы, слои, зоны покрытия.
//
//  Последнее — то, без чего «положил файл, и вещь появилась» не работает:
//  арт без данных игре нечего надеть, она о такой вещи просто не знает.
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
    private const string DefinitionRoot = "Assets/HexLive/UnityPresentation/Wearing/Garments/Assets";

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
    private static int LabelWear(AddressableAssetSettings settings)
    {
        var group = settings.FindGroup(HexLiveAddressablesContent.WearGroup);
        if (group == null)
        {
            Debug.LogError("[Addressables] нет группы вещей — сначала «Разметить гардероб и волосы».");
            return 0;
        }

        // Один проход по определениям вместо поиска на каждую вещь:
        // иначе это 685 сканирований по 788 ассетов.
        var definitions = LoadDefinitions();
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

            // Все вещи, которые носят ЭТОТ арт: сам прототип и его расцветки.
            foreach (var pair in definitions)
            {
                if (pair.Value.ArtId != artId)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(pair.Value.Path))
                {
                    labelled += Tag(settings, group, pair.Value.Path, label) ? 1 : 0;
                }

                var icon = IconPath(pair.Key);
                if (!string.IsNullOrEmpty(icon))
                {
                    labelled += Tag(settings, group, icon, label) ? 1 : 0;
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
    private readonly struct Known
    {
        public Known(string artId, string path) { ArtId = artId; Path = path; }
        public string ArtId { get; }
        public string Path { get; }
    }

    private static Dictionary<string, Known> LoadDefinitions()
    {
        var result = new Dictionary<string, Known>();
        foreach (var guid in AssetDatabase.FindAssets("t:GarmentDefinition", new[] { DefinitionRoot }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var definition = AssetDatabase.LoadAssetAtPath<
                HexLive.UnityPresentation.Wearing.Garments.GarmentDefinition>(path);
            if (definition != null && !string.IsNullOrEmpty(definition.id))
            {
                result[definition.id] = new Known(definition.ArtId, path);
            }
        }

        return result;
    }

    // Иконка названа по id вещи — и по слагу тоже бывает, поэтому проверяются
    // оба имени, ровно как их ищет UI.
    private static string IconPath(string itemId)
    {
        foreach (var root in new[] { IconRoot, "Assets/Resources/HexLive/UI/Items" })
        {
            foreach (var name in new[] { itemId, HexLive.Simulation.Content.ItemInfo.Slug(itemId) })
            {
                var path = $"{root}/{name}.png";
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

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

        if (string.IsNullOrEmpty(entry.address) || entry.address.StartsWith("Assets/"))
        {
            // У ассета, попавшего сюда впервые (иконка, определение), адреса
            // ещё нет — по умолчанию Addressables ставит путь. Путь как адрес
            // не годится: он поменяется при первом же переезде папки.
            entry.address = DefaultAddress(path);
        }

        entry.SetLabel(label, true, false, false);
        return true;
    }

    private static string DefaultAddress(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (path.StartsWith(DefinitionRoot))
        {
            return $"item/{name}";
        }

        return path.EndsWith(".png") ? $"icon/{name}" : name;
    }
}
#endif
