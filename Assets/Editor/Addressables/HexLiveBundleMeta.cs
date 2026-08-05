#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Wearing.Garments;
using UnityEditor;
using UnityEngine;

// ---------------------------------------------------------------------------
//  Мета-файл рядом с бандлом: что это за вещь, не открывая сам бандл.
//
//  Бандл — это арт, он тяжёлый и грузится, только когда вещь надевают. Но чтобы
//  РЕШИТЬ, надевать ли, игре надо знать про вещь заранее: как называется, что
//  закрывает, какой слой, какие статы. Открывать ради этого бандл — значит
//  тащить меши и текстуры вещи, которую ещё не выбрали.
//
//  Отсюда правило: у каждого бандла есть файл-близнец с тем же именем и
//  расширением .json. Он крошечный, читается обычным текстом и лежит в той же
//  папке — положил вещь, положил её мету, игра узнала о вещи целиком.
//
//  Формат намеренно плоский и человекочитаемый: его будут править руками и
//  читать глазами, когда что-то не сойдётся.
//
//  Пишется при сборке контента (HexLive ▸ Addressables ▸ Собрать контент).
// ---------------------------------------------------------------------------
public static class HexLiveBundleMeta
{
    private const string DefinitionRoot = "Assets/HexLive/UnityPresentation/Wearing/Garments/Assets";

    /// <summary>
    /// Разложить меты по собранным бандлам. Имя меты = имя бандла: связь по
    /// имени файла, без всякого реестра, — чтобы «положил два файла» было
    /// достаточно и ничего третьего искать не пришлось.
    /// </summary>
    [UnityEditor.MenuItem("HexLive/Addressables/Написать меты рядом с бандлами")]
    public static void WriteBesideBuilt()
    {
        var root = Path.GetDirectoryName(Application.dataPath);
        WriteBeside(Path.Combine(root ?? ".", "Build", "AddressableContent",
            EditorUserBuildSettings.activeBuildTarget.ToString()));
    }

    public static int WriteBeside(string contentFolder)
    {
        if (!Directory.Exists(contentFolder))
        {
            Debug.LogWarning($"[Мета] нет папки {contentFolder} — меты не написаны.");
            return 0;
        }

        var byArt = CollectByArt();
        var written = 0;

        foreach (var bundle in Directory.GetFiles(contentFolder, "*.bundle", SearchOption.AllDirectories))
        {
            // Addressables называет бандл так: «<группа>_assets_wear.<artid>_<хеш>»,
            // и имя метки в нём приведено к НИЖНЕМУ регистру. Поэтому ищем
            // вхождение, а не начало строки, и сравниваем без регистра —
            // первый прогон на StartsWith дал ровно ноль файлов.
            var name = Path.GetFileName(bundle).ToLowerInvariant();
            var artId = byArt.Keys
                .OrderByDescending(id => id.Length)
                .FirstOrDefault(id => name.Contains($"wear.{id.ToLowerInvariant()}_"));
            if (artId == null)
            {
                continue;
            }

            var json = Json(artId, byArt[artId]);
            File.WriteAllText(Path.ChangeExtension(bundle, ".json"), json, new UTF8Encoding(false));
            written++;
        }

        Debug.Log($"[Мета] написано файлов: {written} (рядом с бандлами, имя в имя).");
        return written;
    }

    // artId -> все вещи, которые носят этот арт (прототип и его расцветки).
    private static Dictionary<string, List<GarmentDefinition>> CollectByArt()
    {
        var shipped = new HashSet<string>();
        var catalog = Resources.Load<GarmentCatalog>(GarmentCatalog.ResourcePath);
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

        var result = new Dictionary<string, List<GarmentDefinition>>();
        foreach (var guid in AssetDatabase.FindAssets("t:GarmentDefinition", new[] { DefinitionRoot }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var definition = AssetDatabase.LoadAssetAtPath<GarmentDefinition>(path);
            if (definition == null || string.IsNullOrEmpty(definition.id))
            {
                continue;
            }

            // Черновики и снятые вещи (их на диске сотня) в игру не едут.
            if (shipped.Count > 0 && !shipped.Contains(definition.id))
            {
                continue;
            }

            if (!result.TryGetValue(definition.ArtId, out var list))
            {
                list = new List<GarmentDefinition>();
                result[definition.ArtId] = list;
            }

            list.Add(definition);
        }

        return result;
    }

    private static string Json(string artId, List<GarmentDefinition> items)
    {
        var text = new StringBuilder();
        text.Append("{\n  \"artId\": ").Append(Quote(artId)).Append(",\n  \"items\": [\n");

        items.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
        for (var i = 0; i < items.Count; i++)
        {
            var d = items[i];
            var slug = ItemInfo.Slug(d.id);
            text.Append("    {\n");
            text.Append("      \"id\": ").Append(Quote(d.id)).Append(",\n");
            text.Append("      \"layer\": ").Append(Quote(d.layer.ToString())).Append(",\n");
            text.Append("      \"covers\": ").Append(Names(d.covers.Select(c => c.ToString()))).Append(",\n");
            text.Append("      \"slots\": ").Append(Names(
                WearSlotCatalog.For(d.id).Select(s => s.ToString()))).Append(",\n");
            text.Append("      \"warmth\": ").Append(Num(d.warmth)).Append(",\n");
            text.Append("      \"armor\": ").Append(Num(d.armor)).Append(",\n");
            text.Append("      \"thermalDelta\": ").Append(Num(d.thermalDelta)).Append(",\n");
            text.Append("      \"capacity\": ").Append(Capacity(d)).Append(",\n");
            text.Append("      \"sex\": ").Append(Quote(Sex(d.id))).Append(",\n");
            text.Append("      \"nameEn\": ").Append(Quote(Term($"item.{slug}.name", "English"))).Append(",\n");
            text.Append("      \"nameRu\": ").Append(Quote(Term($"item.{slug}.name", "Russian"))).Append(",\n");
            text.Append("      \"descEn\": ").Append(Quote(Term($"item.{slug}.desc", "English"))).Append(",\n");
            text.Append("      \"descRu\": ").Append(Quote(Term($"item.{slug}.desc", "Russian"))).Append("\n");
            text.Append(i + 1 < items.Count ? "    },\n" : "    }\n");
        }

        text.Append("  ]\n}\n");
        return text.ToString();
    }

    // Карманы и пол в ассете вещи не лежат — они приходят из кодовых умолчаний
    // (то же самое делает GarmentTuning.BackfillCapacity на старте). Мета
    // обязана нести их сама: у контента кодовых умолчаний не будет.
    private static string Capacity(GarmentDefinition definition)
    {
        if (definition.capacity > 0)
        {
            return definition.capacity.ToString();
        }

        foreach (var d in GarmentLibrary.Defaults)
        {
            if (d.Id == definition.id)
            {
                return d.Capacity.ToString();
            }
        }

        return "0";
    }

    private static string Sex(string id)
    {
        foreach (var d in GarmentLibrary.Defaults)
        {
            if (d.Id == id)
            {
                return d.Sex.ToString();
            }
        }

        return GarmentSex.Any.ToString();
    }

    // Источник строк берётся ИЗ АССЕТА: в редакторе вне игры LocalizationManager
    // ещё не поднят, и через него все строки уехали бы пустыми.
    private static I2.Loc.LanguageSourceData _strings;

    private static string Term(string key, string language)
    {
        if (_strings == null)
        {
            var asset = Resources.Load<I2.Loc.LanguageSourceAsset>("I2Languages");
            _strings = asset != null ? asset.mSource : null;
        }

        var data = _strings?.GetTermData(key);
        if (data == null)
        {
            return string.Empty;
        }

        var index = _strings.GetLanguageIndex(language);
        return index >= 0 && index < data.Languages.Length ? data.Languages[index] ?? string.Empty : string.Empty;
    }

    private static string Names(IEnumerable<string> values) =>
        "[" + string.Join(", ", values.Select(Quote)) + "]";

    private static string Num(float value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string Quote(string value) =>
        "\"" + (value ?? string.Empty)
            .Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\n", " ").Replace("\r", " ") + "\"";
}
#endif
