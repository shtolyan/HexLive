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

    public static string Address(string id) => $"icon/{id}";

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
                    Begin(id, Address(slug), fallbackToSlug: false);
                    return;
                }

                // Отрицательный ответ кэшируется наравне с найденным: вещь без
                // иконки спросят ещё много раз.
                Cache[id] = null;
                return;
            }

            Addressables.LoadAssetAsync<Sprite>(address).Completed += loaded =>
            {
                Cache[id] = loaded.Status == AsyncOperationStatus.Succeeded ? loaded.Result : null;
            };
        };
    }
}

}
