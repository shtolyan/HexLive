using System.Collections.Generic;
using System.IO;
using HexLive.Simulation.Content;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing.Garments
{

// Чтение мет-файлов, лежащих РЯДОМ с бандлами.
//
// Это и есть замыкание всей затеи: бандл несёт арт, мета несёт данные, а игра
// на старте читает меты и узнаёт о вещах, которых не было в момент её сборки.
// Положил два файла в папку контента — и вещь в игре, без пересборки exe.
//
// Почему файлом рядом, а не внутри бандла: чтобы РЕШИТЬ, надевать ли эту вещь
// (трусы это или лифчик, чей слой, какие статы), не нужно открывать бандл — а
// открыть его значит поднять меши и текстуры вещи, которую ещё не выбрали.
//
// Мета ПЕРЕКРЫВАЕТ то, что собрано в коде и ассетах: контент — последнее слово,
// иначе правку статов пришлось бы выпускать вместе с игрой.
public static class WardrobeMeta
{
    // Тот же единый корень, из которого Addressables берёт бандлы. На macOS
    // он лежит рядом с .app (не внутри пакета), поэтому несколько новых билдов
    // в одной папке используют один комплект контента.
    private static string ContentRoot => ExternalContentPath.Root;

    /// <summary>Сколько вещей приехало метами — для проверки и лога.</summary>
    public static int LoadedItems { get; private set; }

    private static bool _loaded;

    /// <summary>
    /// Прочитать все меты и влить в таблицы. Зовётся на старте один раз; повтор
    /// — тихий no-op, чтобы перезаход в мир не перезаливал таблицы поверх себя.
    /// </summary>
    public static void Load()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        var root = Path.GetFullPath(ContentRoot);
        if (!Directory.Exists(root))
        {
            // Законный случай: в редакторе контент не собран, играем на том,
            // что в проекте. Не ошибка и не предупреждение.
            return;
        }

        var byId = new Dictionary<string, GarmentParams>();
        var files = 0;

        foreach (var path in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            // Рядом лежат служебные файлы самих Addressables — их не трогаем.
            if (name.StartsWith("catalog") || name.StartsWith("settings") ||
                name.StartsWith("link") || name.StartsWith("AddressablesLink"))
            {
                continue;
            }

            MetaFile meta;
            try
            {
                meta = JsonUtility.FromJson<MetaFile>(File.ReadAllText(path));
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Мета] {name} не читается: {e.Message}");
                continue;
            }

            if (meta?.items == null)
            {
                continue;
            }

            files++;
            foreach (var item in meta.items)
            {
                if (item == null || string.IsNullOrEmpty(item.id))
                {
                    continue;
                }

                byId[item.id] = ToParams(item, meta.artId);
                RegisterSlots(item);
            }
        }

        if (byId.Count == 0)
        {
            return;
        }

        // Всё, чего в метах нет, остаётся из кода и ассетов: комплект чужака
        // §72 контентом не раздаётся и должен пережить эту заливку.
        var merged = new List<GarmentParams>(byId.Count);
        foreach (var known in GarmentLibrary.Active)
        {
            if (!byId.ContainsKey(known.Id))
            {
                merged.Add(known);
            }
        }

        merged.AddRange(byId.Values);
        GarmentLibrary.Override(merged);

        LoadedItems = byId.Count;
        Debug.Log($"[Мета] прочитано файлов {files}, вещей {LoadedItems}; " +
                  $"в таблице всего {merged.Count}.");
    }

    private static void RegisterSlots(MetaItem item)
    {
        if (item.slots == null || item.slots.Length == 0)
        {
            return;
        }

        var slots = new List<WearSlot>(item.slots.Length);
        foreach (var name in item.slots)
        {
            if (System.Enum.TryParse<WearSlot>(name, out var slot))
            {
                slots.Add(slot);
            }
        }

        if (slots.Count > 0)
        {
            WearSlotCatalog.Register(item.id, slots.ToArray());
        }
    }

    private static GarmentParams ToParams(MetaItem item, string artId)
    {
        var covers = new List<BodyPart>();
        if (item.covers != null)
        {
            foreach (var name in item.covers)
            {
                if (System.Enum.TryParse<BodyPart>(name, out var part))
                {
                    covers.Add(part);
                }
            }
        }

        System.Enum.TryParse<WearLayer>(item.layer, out var layer);
        System.Enum.TryParse<GarmentSex>(item.sex, out var sex);

        return new GarmentParams(
            item.id,
            string.IsNullOrEmpty(item.nameEn) ? item.id : item.nameEn,
            layer,
            item.warmth,
            item.armor,
            item.thermalDelta,
            item.dressDurationTicks > 0 ? item.dressDurationTicks : 8,
            item.capacity,
            sex,
            covers.ToArray())
        {
            PrototypeId = string.IsNullOrEmpty(artId) ? item.id : artId,
        };
    }

    // JsonUtility требует конкретных полей; лишнее в файле он молча игнорирует,
    // что здесь и нужно — мету будут дополнять, а старая игра должна её пережить.
    [System.Serializable]
    private sealed class MetaFile
    {
        public string artId;
        public MetaItem[] items;
    }

    [System.Serializable]
    private sealed class MetaItem
    {
        public string id;
        public string layer;
        public string[] covers;
        public string[] slots;
        public float warmth;
        public float armor;
        public float thermalDelta;
        public int dressDurationTicks;
        public int capacity;
        public string sex;
        public string nameEn;
        public string nameRu;
        public string descEn;
        public string descRu;
    }
}

}
