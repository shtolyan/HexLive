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

    // Enter Play Mode can run without a domain reload. Addressables tears its
    // ResourceManager down between runs, but these managed dictionaries would
    // otherwise retain handles whose InternalOp no longer exists.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Hair.Clear();
        Materials.Clear();
    }

    public static string HairAddress(string hair) => $"hair/{hair}";

    public static string ColourAddress(string hair, string colour, string surface) =>
        $"hair/{hair}/{colour}/{surface}";

    public static string WearAddress(string artId) => $"wear/{artId}";

    /// <summary>
    /// §41.3: заказать причёску заранее, НЕ дожидаясь. Кладёт хэндл в тот же
    /// кэш, из которого читает <see cref="LoadHair"/>, — поэтому актриса,
    /// собранная позже, подхватит уже приехавший префаб вместо своей загрузки.
    ///
    /// Учёт в очереди обязателен: без Begin/End занавес не знал бы, что ждёт
    /// причёску, и упал бы раньше — с лысой головой на кадр.
    /// </summary>
    public static void Prewarm(string hairId)
    {
        if (string.IsNullOrEmpty(hairId))
        {
            return;
        }

        var address = HairAddress(hairId);
        if (Hair.TryGetValue(address, out var cached))
        {
            if (cached.IsValid())
            {
                return;
            }

            Hair.Remove(address);
        }

        var handle = Addressables.LoadAssetAsync<GameObject>(address);
        Hair[address] = handle;
        Garments.ContentQueue.Begin(Garments.ContentQueue.Kind.Hair);
        handle.Completed += _ => Garments.ContentQueue.End(Garments.ContentQueue.Kind.Hair);
    }

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
        if (Hair.TryGetValue(address, out var cached) && !cached.IsValid())
        {
            Hair.Remove(address);
        }

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

        // A subsystem reset can invalidate an operation while a coroutine is
        // being unwound. Never inspect Status/Result until validity is known.
        if (!handle.IsValid())
        {
            Hair.Remove(address);
            Debug.LogWarning($"[HairContent] операция загрузки «{address}» была сброшена.");
            done(null);
            yield break;
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
            if (Materials.TryGetValue(address, out var cached) && !cached.IsValid())
            {
                Materials.Remove(address);
            }

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

            if (!handle.IsValid())
            {
                Materials.Remove(address);
                continue;
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
