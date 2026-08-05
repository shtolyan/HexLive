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
// Загрузка синхронная и кэшируется НАВСЕГДА, включая отрицательный ответ:
// список вещей в UI перерисовывается постоянно, и повторный промах по адресу
// стоил бы дороже самой иконки.
public static class ItemIcons
{
    private static readonly Dictionary<string, Sprite> Cache = new();

    public static string Address(string id) => $"icon/{id}";

    /// <summary>Иконка вещи или null — тогда UI рисует свой запасной значок.</summary>
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

        // Имя файла — это id вещи, но исторически встречается и слаг, поэтому
        // проверяются оба: ровно так их искал прежний Resources.Load.
        var sprite = ByAddress(Address(id)) ?? ByAddress(Address(ItemInfo.Slug(id)));
        Cache[id] = sprite;
        return sprite;
    }

    private static Sprite ByAddress(string address)
    {
        // Сначала спрашиваем каталог, есть ли такой адрес: это дешевле, чем
        // ловить исключение, и не красит консоль на каждой вещи без иконки.
        var locations = Addressables.LoadResourceLocationsAsync(address);
        locations.WaitForCompletion();
        var found = locations.Status == AsyncOperationStatus.Succeeded &&
                    locations.Result != null && locations.Result.Count > 0;
        Addressables.Release(locations);

        if (!found)
        {
            return null;
        }

        return Addressables.LoadAssetAsync<Sprite>(address).WaitForCompletion();
    }
}

}
