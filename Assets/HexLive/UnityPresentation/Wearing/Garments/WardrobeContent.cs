using System.Collections.Generic;
using HexLive.Simulation.Content;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace HexLive.UnityPresentation.Wearing.Garments
{

// Гардероб как КОНТЕНТ: что положили в папку, то игра и знает.
//
// Порядок загрузки — три ступени, и каждая отвечает на свой вопрос:
//
//   1. КАТАЛОГ Addressables    — какие бандлы вообще есть. Крошечный, на старте.
//   2. ОГЛАВЛЕНИЕ (WardrobeIndex) — какие вещи бывают и как себя ведут: id,
//      статы, слоты, строки. Тоже на старте и тоже маленькое: ни одного меша.
//   3. БАНДЛ ВЕЩИ             — арт, иконка, материалы. Только когда вещь
//      понадобилась, и один раз: дальше отдаёт кэш.
//
// Без второй ступени «положил файл — вещь появилась» не работает: игра строит
// свой гардероб на старте, и вещь, которой в оглавлении нет, для неё не
// существует, сколько бандлов ни лежит рядом.
//
// Почему оглавление — отдельный ассет, а не чтение определений из бандлов:
// определение лежит в бандле СВОЕЙ вещи, вместе с артом. Прочитать все
// определения — значит поднять все бандлы, то есть весь гардероб. Ровно то, от
// чего уходили.
public static class WardrobeContent
{
    private static bool _loaded;

    /// <summary>Сколько вещей приехало контентом (для проверок и логов).</summary>
    public static int LoadedItems { get; private set; }

    /// <summary>
    /// Ступени 1-2. Вызывается на старте ОДИН раз; повтор — тихий no-op, чтобы
    /// перезаход в мир не перезаливал таблицы поверх самих себя.
    /// </summary>
    public static void LoadIndex()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        // Сначала спрашиваем КАТАЛОГ, есть ли такой адрес вообще, и только
        // потом грузим. Иначе отсутствующий адрес — это исключение и красная
        // консоль на каждом запуске без собранного контента, а это законный
        // случай, а не поломка.
        var locations = Addressables.LoadResourceLocationsAsync(WardrobeIndex.Address);
        locations.WaitForCompletion();
        var found = locations.Status == AsyncOperationStatus.Succeeded &&
                    locations.Result != null && locations.Result.Count > 0;
        Addressables.Release(locations);

        if (!found)
        {
            // НЕ ошибка: контента рядом может не быть вовсе (запуск из
            // редактора без собранного контента). Игра тогда живёт на своих
            // кодовых умолчаниях — ровно как до перехода.
            Debug.Log($"[Гардероб] оглавления «{WardrobeIndex.Address}» нет — " +
                      "играем на кодовых умолчаниях. Собери контент, если ждал вещи из папки.");
            return;
        }

        // Синхронно и намеренно: это СТАРТ, дальше по коду мир уже строится из
        // этих таблиц. Файл маленький — строки и числа, ни одного меша.
        var handle = Addressables.LoadAssetAsync<WardrobeIndex>(WardrobeIndex.Address);
        var index = handle.WaitForCompletion();
        if (index == null)
        {
            Debug.LogWarning("[Гардероб] оглавление есть в каталоге, но не прочиталось.");
            return;
        }

        Apply(index);
    }

    // Оглавление ПЕРЕКРЫВАЕТ кодовые умолчания, а не дополняет их: контент —
    // последнее слово. Иначе правку статов пришлось бы выпускать вместе с exe.
    private static void Apply(WardrobeIndex index)
    {
        var garments = new List<GarmentParams>(index.items.Count);
        var byId = new Dictionary<string, GarmentParams>(index.items.Count);

        foreach (var row in index.items)
        {
            if (row == null || string.IsNullOrEmpty(row.id))
            {
                continue;
            }

            var garment = WardrobeIndex.ToParams(row);
            byId[row.id] = garment;

            if (row.slots.Count > 0)
            {
                var slots = new WearSlot[row.slots.Count];
                for (var i = 0; i < row.slots.Count; i++)
                {
                    slots[i] = (WearSlot)row.slots[i];
                }

                WearSlotCatalog.Register(row.id, slots);
            }
        }

        // Кодовые умолчания остаются для всего, чего в оглавлении нет: чужак
        // §72 и прочее, что живёт в коде и контентом не раздаётся.
        foreach (var d in GarmentLibrary.Defaults)
        {
            if (!byId.ContainsKey(d.Id))
            {
                garments.Add(d);
            }
        }

        garments.AddRange(byId.Values);
        GarmentLibrary.Override(garments);

        LoadedItems = byId.Count;
        Debug.Log($"[Гардероб] из контента: {LoadedItems} вещей " +
                  $"(в таблице всего {garments.Count} с учётом кодовых умолчаний).");

        ContentLocalization.Register(index);
    }
}

}
