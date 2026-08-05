using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace HexLive.UnityPresentation.Wearing
{

// Загрузка причёсок и их расцветок ПО АДРЕСУ, а не по ссылке.
//
// Адрес строится по тому же правилу, что и разметка (HexLiveAddressablesContent):
//   hair/<Причёска>                      — префаб
//   hair/<Причёска>/<Цвет>/<Поверхность> — материал расцветки
//
// Правило живёт в двух местах — в редакторной разметке и здесь — и это цена
// того, что рантайм не должен знать про UnityEditor. Расхождение ловится
// проверкой «HexLive ▸ Addressables ▸ Проверить адреса», потому что иначе оно
// молчит: несуществующий адрес возвращает null, и девушка выходит лысой без
// единой ошибки в консоли.
//
// В РЕДАКТОРЕ бандлы не нужны: Play Mode Script = Use Asset Database, и тот же
// код берёт ассеты прямо из проекта. Поэтому путь ОДИН, без «если редактор».
//
// Загруженное КЭШИРУЕТСЯ и не выгружается: причёсок 16, а материал одного
// цвета носит обычно не одна девушка. Выгружать по одной — значит грузить их
// заново на каждую смену причёски.
public static class HairContent
{
    private static readonly Dictionary<string, AsyncOperationHandle<GameObject>> Hair = new();

    // Учёт ведёт ContentQueue — там же, где одежда и иконки.
    private static readonly Dictionary<string, AsyncOperationHandle<Material>> Materials = new();

    public static string HairAddress(string hair) => $"hair/{hair}";

    public static string ColourAddress(string hair, string colour, string surface) =>
        $"hair/{hair}/{colour}/{surface}";

    public static string WearAddress(string artId) => $"wear/{artId}";

    /// <summary>
    /// Префаб причёски. Корутина, а не async/await: вызывающий — MonoBehaviour,
    /// которому надо просто дождаться и продолжить, а исключения в async void
    /// в Unity теряются молча.
    /// </summary>
    public static IEnumerator LoadHair(string hairId, System.Action<Wear> done)
    {
        if (string.IsNullOrEmpty(hairId))
        {
            done(null);
            yield break;
        }

        var address = HairAddress(hairId);
        if (!Hair.TryGetValue(address, out var handle))
        {
            handle = Addressables.LoadAssetAsync<GameObject>(address);
            Hair[address] = handle;
        }

        if (!handle.IsDone)
        {
            Garments.ContentQueue.Begin(Garments.ContentQueue.Kind.Hair);
            yield return handle;
            Garments.ContentQueue.End(Garments.ContentQueue.Kind.Hair);
        }

        if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null)
        {
            Debug.LogWarning($"[HairContent] нет контента по адресу «{address}» — " +
                             "проверь «Разметить гардероб и волосы» и что контент собран.");
            done(null);
            yield break;
        }

        done(handle.Result.GetComponent<Wear>());
    }

    /// <summary>
    /// Материалы одной расцветки, по ИМЕНАМ ПОВЕРХНОСТЕЙ. Поверхности, которых
    /// в расцветке нет, сюда просто не попадают — прототипный материал на них
    /// и останется.
    /// </summary>
    public static IEnumerator LoadColour(string hair, ActorAppearanceCatalog.HairColour colour,
                                         System.Action<Dictionary<string, Material>> done)
    {
        var result = new Dictionary<string, Material>();
        if (colour == null)
        {
            done(result);
            yield break;
        }

        foreach (var surface in colour.surfaces)
        {
            var address = ColourAddress(hair, colour.colour, surface);
            if (!Materials.TryGetValue(address, out var handle))
            {
                handle = Addressables.LoadAssetAsync<Material>(address);
                Materials[address] = handle;
            }

            if (!handle.IsDone)
            {
                Garments.ContentQueue.Begin(Garments.ContentQueue.Kind.HairColour);
                yield return handle;
                Garments.ContentQueue.End(Garments.ContentQueue.Kind.HairColour);
            }

            if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result != null)
            {
                result[surface] = handle.Result;
            }
        }

        done(result);
    }
}

}
