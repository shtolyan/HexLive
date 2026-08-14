using System.Collections.Generic;
using HexLive.Simulation.Content;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace HexLive.UnityPresentation.Wearing.Garments
{

// ⭐ ЕДИНСТВЕННАЯ дверь к иконкам вещей — как ActorWardrobe к их арту.
//
// Иконка едет в бандле СВОЕЙ вещи, поэтому у вещи, положенной в папку после
// сборки игры, иконка появляется вместе с ней. В Resources все семьсот штук
// попадали в билд безусловно, нужны они кому-то или нет.
//
// ⚠️ ЗАГРУЗКА НИКОГДА НЕ БЛОКИРУЕТ, и это выстрадано. Сначала здесь стоял
// WaitForCompletion — и повесил игру намертво: экран загрузки прогревает панель
// персонажа, панель просит иконку, а блокирующее ожидание Addressables ВНУТРИ
// корутины не разрешается никогда. Корутина экрана застревала, шторка не
// уничтожалась, мир оставался на паузе (timeScale = 0): персонажи скользили без
// анимации, камера не двигалась, никто не выбирался. И ни одной ошибки в логе —
// это был не сбой, а тупик.
//
// Поэтому правило: иконка отдаётся из кэша либо не отдаётся вовсе. Загрузка
// уходит в фон, и на следующей перерисовке иконка уже на месте — а UI и так
// перерисовывается постоянно.
public static class ItemIcons
{
    private static readonly Dictionary<string, Sprite> Cache = new();
    private static readonly HashSet<string> Loading = new();
    private static bool _prewarmedAll;

    public static string Address(string id) => $"icon/{id}";

    /// <summary>
    /// §41.3: ВСЕ иконки сразу, за занавесом загрузки.
    ///
    /// В отличие от одежды, «какие понадобятся» тут спрашивать не нужно и
    /// вредно: иконки собраны в ОДИН бандл на 0.73 МБ (одежда — 712 бандлов и
    /// 1.99 ГБ, поэтому её греют строго по надетому). Как только он смонтирован,
    /// каждая следующая иконка достаётся почти бесплатно, зато список вещей,
    /// добыча и рюкзак открываются сразу нарисованными, а не досоздают спрайты
    /// по одному на первой перерисовке.
    ///
    /// Адреса берутся прямо из каталога Addressables, а не собираются из id, —
    /// поэтому промаха по несуществующему адресу здесь быть не может (тот самый
    /// InvalidKeyException, из-за которого Load сначала спрашивает локации).
    /// Обратная сторона: если файл назван слагом, а не id, в кэш попадёт слаг —
    /// такую иконку допросит обычный ленивый путь, он умеет оба имени.
    /// </summary>
    public static void PrewarmAll()
    {
        if (_prewarmedAll)
        {
            return;
        }

        _prewarmedAll = true;

        // ⚠️ Каталог поднимается ЛЕНИВО, и на непроинициализированных
        // Addressables список ключей пуст — скан «успешно» не нашёл бы ни одной
        // иконки и больше не повторился. InitializeAsync идемпотентен и уже
        // поднятый каталог отдаёт сразу.
        //
        // Пара Begin/End оборачивает сам скан не для красоты: без неё очередь
        // между вызовом и первым ответом каталога выглядит ПУСТОЙ, и занавес
        // успел бы упасть раньше, чем в неё легла хоть одна иконка.
        ContentQueue.Begin(ContentQueue.Kind.Icon);
        Addressables.InitializeAsync().Completed += _ =>
        {
            ScanCatalogForIcons();
            ContentQueue.End(ContentQueue.Kind.Icon);
        };
    }

    private static void ScanCatalogForIcons()
    {
        foreach (var locator in Addressables.ResourceLocators)
        {
            foreach (var key in locator.Keys)
            {
                if (key is not string address ||
                    !address.StartsWith("icon/", System.StringComparison.Ordinal))
                {
                    continue;
                }

                var id = address.Substring("icon/".Length);
                if (id.Length == 0 || Cache.ContainsKey(id) || !Loading.Add(id))
                {
                    continue;
                }

                ContentQueue.Begin(ContentQueue.Kind.Icon);
                Addressables.LoadAssetAsync<Sprite>(address).Completed += loaded =>
                {
                    Cache[id] = loaded.Status == AsyncOperationStatus.Succeeded
                        ? loaded.Result
                        : null;
                    ContentQueue.End(ContentQueue.Kind.Icon);
                };
            }
        }
    }

    /// <summary>
    /// Иконка вещи, если она уже в памяти. Иначе null — и запуск фоновой
    /// загрузки, чтобы в следующий раз была. Вызывающий рисует свой запасной
    /// значок и не думает об этом.
    /// </summary>
    public static Sprite Load(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        if (Cache.TryGetValue(id, out var cached))
        {
            return cached;
        }

        if (Loading.Add(id))
        {
            Begin(id, Address(id), fallbackToSlug: true);
        }

        return null;
    }

    private static void Begin(string id, string address, bool fallbackToSlug)
    {
        ContentQueue.Begin(ContentQueue.Kind.Icon);
        Addressables.LoadResourceLocationsAsync(address).Completed += found =>
        {
            var exists = found.Status == AsyncOperationStatus.Succeeded &&
                         found.Result != null && found.Result.Count > 0;
            Addressables.Release(found);

            if (!exists)
            {
                // Имя файла — это id вещи, но исторически встречается и слаг:
                // проверяем оба, ровно как искал прежний Resources.Load.
                var slug = ItemInfo.Slug(id);
                if (fallbackToSlug && slug != id)
                {
                    ContentQueue.End(ContentQueue.Kind.Icon);
                    Begin(id, Address(slug), fallbackToSlug: false);
                    return;
                }

                // Отрицательный ответ кэшируется наравне с найденным: вещь без
                // иконки спросят ещё много раз.
                Cache[id] = null;
                ContentQueue.End(ContentQueue.Kind.Icon);
                return;
            }

            Addressables.LoadAssetAsync<Sprite>(address).Completed += loaded =>
            {
                Cache[id] = loaded.Status == AsyncOperationStatus.Succeeded ? loaded.Result : null;
                ContentQueue.End(ContentQueue.Kind.Icon);
            };
        };
    }
}

}
