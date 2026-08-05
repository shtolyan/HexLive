using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

// ⭐ ЕДИНСТВЕННАЯ дверь к арту одежды. Кто бы ни спрашивал — спрашивает здесь.
//
// Через неё проходят ВСЕ случаи, и это не совпадение, а то, ради чего она одна:
//   * девушки на старте — цикл одевания в NpcActorView;
//   * чужак §72 со своим тактическим комплектом — тот же цикл;
//   * новый колонист, приходящий по ходу игры, — у него свой NpcActorView;
//   * вещь, выброшенная морем или висящая на стойке, — GarmentDropFactory.
//
// Поэтому «поддержать ленивую загрузку» отдельно ни в одном из этих мест не
// надо и НЕ НАДО ПИСАТЬ: она живёт тут, разом для всех.
//
// Как грузит: по адресу wear/<artId> через Addressables, СИНХРОННО и с кэшем.
// Синхронно намеренно — вызывающие ждут готовый список здесь и сейчас, ровно
// как ждали прежний Resources.LoadAll, который блокировал так же. Кэш держит
// результат навсегда, включая пустой ответ: вещь, которой нет, спросят ещё
// много раз, и каждый промах стоил бы попытки открыть бандл.
//
// Прогрев (GarmentDropFactory.Prewarm) зовёт этот же метод за занавесом
// загрузки — тогда первая настоящая вещь строится уже из памяти.
public static class ActorWardrobe
{
    private static readonly Dictionary<string, List<Wear>> _cache = new();

    public static IReadOnlyList<Wear> GetVisuals(string simDefinitionId)
    {
        if (_cache.TryGetValue(simDefinitionId, out var cached))
        {
            return cached;
        }

        var result = new List<Wear>();
        // §31B.4E: an item wears its PROTOTYPE's art. For all but a variant the
        // prototype is itself, so this is the same art id as before.
        //
        // Арт уехал из Resources в Addressables: в Resources он попадал в билд
        // ЦЕЛИКОМ и всегда, а теперь приезжает по адресу и только нужный.
        //
        // Загрузка СИНХРОННАЯ (WaitForCompletion) намеренно: все вызывающие —
        // цикл одевания и постройка выброшенной на землю вещи — ждут готовый
        // список здесь и сейчас, ровно как ждали Resources.LoadAll, который
        // блокировал точно так же. Цена платится в прогреве (GarmentDropFactory
        // .Prewarm за занавесом загрузки), а не переписыванием половины вида.
        var address = HairContent.WearAddress(Garments.GarmentVariants.ArtIdOf(simDefinitionId));
        var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>(address);
        var prefab = handle.WaitForCompletion();
        if (prefab != null)
        {
            var wear = prefab.GetComponent<Wear>();
            if (wear != null)
            {
                result.Add(wear);
            }
        }

        _cache[simDefinitionId] = result;
        return result;
    }
}

}
