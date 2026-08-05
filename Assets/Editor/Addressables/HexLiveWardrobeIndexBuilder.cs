#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Wearing.Garments;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

// ---------------------------------------------------------------------------
//  Сборка ОГЛАВЛЕНИЯ гардероба — того маленького файла, который игра читает на
//  старте, чтобы узнать, какие вещи вообще бывают.
//
//  Оглавление лежит в СВОЕЙ группе и своём бандле, и это принципиально: если
//  положить его к вещам, чтение оглавления потянуло бы за собой чужой арт.
//  Здесь только строки и числа — ни меша, ни текстуры.
//
//  Гонять после КАЖДОЙ новой партии одежды. Вещь, которой нет в оглавлении, для
//  игры не существует, сколько бандлов ни лежит рядом с exe.
//
//  Menu: HexLive ▸ Addressables ▸ Собрать оглавление гардероба
// ---------------------------------------------------------------------------
public static class HexLiveWardrobeIndexBuilder
{
    private const string IndexGroup = "HexLive.WardrobeIndex";
    private const string IndexPath = "Assets/HexLiveContent/WardrobeIndex.asset";
    private const string DefinitionRoot = "Assets/HexLive/UnityPresentation/Wearing/Garments/Assets";

    [MenuItem("HexLive/Addressables/Собрать оглавление гардероба")]
    public static void Build()
    {
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null)
        {
            Debug.LogError("[Гардероб] сначала «Настроить проект».");
            return;
        }

        var index = AssetDatabase.LoadAssetAtPath<WardrobeIndex>(IndexPath);
        if (index == null)
        {
            index = ScriptableObject.CreateInstance<WardrobeIndex>();
            AssetDatabase.CreateAsset(index, IndexPath);
        }

        index.items.Clear();

        // Берутся ТОЛЬКО вещи из каталога: определение, лежащее ассетом, но не
        // попавшее в каталог, — это черновик или снятая вещь, и в игру ей не
        // надо (таких на диске сотня).
        var catalog = Resources.Load<GarmentCatalog>(GarmentCatalog.ResourcePath);
        var shipped = new HashSet<string>();
        if (catalog != null)
        {
            foreach (var definition in catalog.garments)
            {
                if (definition != null && !string.IsNullOrEmpty(definition.id))
                {
                    shipped.Add(definition.id);
                }
            }
        }

        foreach (var guid in AssetDatabase.FindAssets("t:GarmentDefinition", new[] { DefinitionRoot }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var definition = AssetDatabase.LoadAssetAtPath<GarmentDefinition>(path);
            if (definition == null || string.IsNullOrEmpty(definition.id) ||
                (shipped.Count > 0 && !shipped.Contains(definition.id)))
            {
                continue;
            }

            index.items.Add(RowOf(definition));
        }

        index.items.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
        EditorUtility.SetDirty(index);
        AssetDatabase.SaveAssets();

        MarkAddressable(settings);

        var withStrings = index.items.Count(r => !string.IsNullOrEmpty(r.nameRu));
        var withSlots = index.items.Count(r => r.slots.Count > 0);
        Debug.Log($"[Гардероб] оглавление собрано: {index.items.Count} вещей, " +
                  $"со строками {withStrings}, со слотами {withSlots} -> {IndexPath}");
    }

    private static WardrobeIndex.Row RowOf(GarmentDefinition definition)
    {
        var row = new WardrobeIndex.Row
        {
            id = definition.id,
            artId = definition.ArtId,
            displayName = definition.displayName,
            layer = (int)definition.layer,
            warmth = definition.warmth,
            armor = definition.armor,
            thermalDelta = definition.thermalDelta,
            dressDurationTicks = definition.dressDurationTicks,
            capacity = definition.capacity,
        };

        foreach (var part in definition.covers)
        {
            row.covers.Add((int)part);
        }

        // Пол и карманы в ассете не хранятся — они приходят из кодовых
        // умолчаний (GarmentTuning.BackfillCapacity делает то же самое на
        // старте). Оглавление обязано нести их само: у контента кодовых
        // умолчаний не будет.
        foreach (var d in GarmentLibrary.Defaults)
        {
            if (d.Id != definition.id)
            {
                continue;
            }

            row.sex = (int)d.Sex;
            if (row.capacity == 0)
            {
                row.capacity = d.Capacity;
            }

            break;
        }

        foreach (var slot in WearSlotCatalog.For(definition.id))
        {
            row.slots.Add((int)slot);
        }

        var slug = ItemInfo.Slug(definition.id);
        row.nameEn = Term($"item.{slug}.name", "English");
        row.nameRu = Term($"item.{slug}.name", "Russian");
        row.descEn = Term($"item.{slug}.desc", "English");
        row.descRu = Term($"item.{slug}.desc", "Russian");
        return row;
    }

    // Источник строк берётся ИЗ АССЕТА, а не из LocalizationManager.Sources:
    // в редакторе вне игры менеджер ещё не поднят, список пуст, и все строки
    // молча уехали бы пустыми (ровно это и случилось на первом прогоне —
    // «со строками 0»).
    private static I2.Loc.LanguageSourceData _strings;

    private static I2.Loc.LanguageSourceData Strings()
    {
        if (_strings != null)
        {
            return _strings;
        }

        var asset = Resources.Load<I2.Loc.LanguageSourceAsset>("I2Languages");
        _strings = asset != null ? asset.mSource : null;
        if (_strings == null)
        {
            Debug.LogWarning("[Гардероб] не нашёл Resources/I2Languages — строки в оглавление не попадут.");
        }

        return _strings;
    }

    private static string Term(string key, string language)
    {
        var source = Strings();
        var data = source?.GetTermData(key);
        if (data == null)
        {
            return string.Empty;
        }

        var index = source.GetLanguageIndex(language);
        return index >= 0 && index < data.Languages.Length ? data.Languages[index] ?? string.Empty : string.Empty;
    }

    private static void MarkAddressable(AddressableAssetSettings settings)
    {
        var group = settings.FindGroup(IndexGroup);
        if (group == null)
        {
            group = settings.CreateGroup(IndexGroup, false, false, false, null,
                typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
        }

        HexLiveAddressablesSetup.PointGroupOutside(settings, group);

        var schema = group.GetSchema<BundledAssetGroupSchema>();
        if (schema != null)
        {
            // Оглавление — один ассет, паковать его «по метке» незачем.
            schema.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
            EditorUtility.SetDirty(schema);
        }

        var guid = AssetDatabase.AssetPathToGUID(IndexPath);
        var entry = settings.CreateOrMoveEntry(guid, group, false, false);
        if (entry != null)
        {
            entry.address = WardrobeIndex.Address;
        }

        AssetDatabase.SaveAssets();
    }
}
#endif
