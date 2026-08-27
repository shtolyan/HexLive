using System;
using System.Collections.Generic;
using HexLive.Simulation.Debug;
using UnityEngine;

namespace HexLive.UnityPresentation.Content
{

/// <summary>
/// Резидентность тяжёлого контента по восприятию: учёт «какая сущность с живым
/// вью пользуется каким комплектом ассетов» плюс LRU-буфер скрывшихся сущностей
/// с байтовым лимитом.
/// </summary>
/// <remarks>
/// Правило резидентности одно: в памяти живёт то, что видно СЕЙЧАС, плюс пины
/// (своя девушка, выбранная, тела), плюс буфер недавно скрывшегося. Буфер — не
/// таймаут: девушки лагеря, снующие через границу восприятия, почти никогда не
/// вытесняются (каждое появление поднимает их наверх), а прошедший мимо чужак
/// уходит первым. Вытеснение случается только при переполнении лимита, поэтому
/// в спокойной игре механизм не стоит ничего.
///
/// Отслеживаются только ТЯЖЁЛЫЕ семьи: wear (одежда, надетая и лежащая), hair,
/// actor (тело + карты покраски кожи). Объекты, постройки, мобы и иконки —
/// резидентные: они маленькие, общие и нужны постоянно.
///
/// Почему освобождение двухступенчатое: у бандла с одеждой три хэндла (main +
/// метаданные расцветок + иконка), и иконка ОСТАЁТСЯ — списки инвентаря
/// показывают её для любого экземпляра вещи у кого угодно. Открытый LZ4-бандл
/// без загруженных ассетов дёшев; дорогие — извлечённые текстуры, и их
/// освобождает Resources.UnloadUnusedAssets, который запускается только когда
/// накопился порог (см. SweepThresholdBytes), а не на каждое вытеснение.
/// </remarks>
public static class ContentResidency
{
    /// <summary>Рубильник всего механизма (выгрузка и гейт на создание вью
    /// скрытых). Выключен — поведение в точности прежнее: всё, что загрузилось,
    /// живёт до конца сессии.</summary>
    public static bool Enabled = true;

    private sealed class Entry
    {
        public string[] Kits = Array.Empty<string>();
        public long Bytes;
        // -1 = видима; иначе Time.unscaledTime момента скрытия.
        public double HiddenSince = -1.0;
        public bool Pinned;
    }

    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> KitRefs = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> KitBytesCache = new(StringComparer.Ordinal);
    // Комплекты, чей refcount упал до нуля, но семья отказала (загрузка ещё в
    // полёте) — повтор на следующем проходе.
    private static readonly List<string> RetryKits = new();
    private static readonly List<string> KitScratch = new();

    private static long _budgetBytes = -1;
    private static long _releasedSinceSweep;
    private static AsyncOperation _sweep;

    // Свежескрывшихся не трогаем даже при переполнении — защита от дребезга,
    // когда лимит забит под завязку, а девушка мигает на самой границе.
    private const double MinHiddenSeconds = 10.0;
    // Накопленный вес освобождённых комплектов, после которого запускается
    // Resources.UnloadUnusedAssets (асинхронный; размазан движком по кадрам).
    private const long SweepThresholdBytes = 256L * 1024 * 1024;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Entries.Clear();
        KitRefs.Clear();
        KitBytesCache.Clear();
        RetryKits.Clear();
        KitScratch.Clear();
        _budgetBytes = -1;
        _releasedSinceSweep = 0;
        _sweep = null;
    }

    /// <summary>Лимит БУФЕРА (скрытые сущности): четверть RAM машины,
    /// зажатая в [1 ГБ, 6 ГБ]. Видимое в лимите не участвует — оно обязано
    /// быть загруженным.</summary>
    public static long BudgetBytes
    {
        get
        {
            if (_budgetBytes < 0)
            {
                var quarter = (long)SystemInfo.systemMemorySize * 1024 * 1024 / 4;
                _budgetBytes = Math.Clamp(
                    quarter, 1024L * 1024 * 1024, 6144L * 1024 * 1024);
            }
            return _budgetBytes;
        }
    }

    /// <summary>Живой вью девушки: какие комплекты держит и видима ли она.
    /// Зовётся рендерером на каждый снапшот-проход для каждой девушки С ВЬЮ.</summary>
    public static void ReportNpc(int npcId, NpcSnapshot npc, bool visible, bool pinned)
    {
        if (!Enabled)
        {
            return;
        }

        KitScratch.Clear();
        AddKit("actor/" + npc.ActorMesh);
        if (!string.IsNullOrEmpty(npc.Hairstyle))
        {
            AddKit("hair/" + npc.Hairstyle);
        }
        foreach (var worn in npc.WornItems)
        {
            if (!string.IsNullOrEmpty(worn))
            {
                AddKit("wear/" + worn);
            }
        }
        if (!string.IsNullOrEmpty(npc.HeldGarmentId))
        {
            AddKit("wear/" + npc.HeldGarmentId);
        }

        Report("npc/" + npcId, KitScratch, visible, pinned);
    }

    /// <summary>Тело с живым вью (загруженный сейв, подключившийся клиент —
    /// минуя TransferNpcToCorpse). Пин: труп не вытесняется, но его комплекты
    /// обязаны иметь ссылки — иначе вытеснение соседки выдернет общие ассеты
    /// из-под лежащего тела.</summary>
    public static void ReportCorpse(int corpseId, NpcSnapshot body)
    {
        if (!Enabled)
        {
            return;
        }

        KitScratch.Clear();
        AddKit("actor/" + body.ActorMesh);
        if (!string.IsNullOrEmpty(body.Hairstyle))
        {
            AddKit("hair/" + body.Hairstyle);
        }
        foreach (var worn in body.WornItems)
        {
            if (!string.IsNullOrEmpty(worn))
            {
                AddKit("wear/" + worn);
            }
        }

        Report("corpse/" + corpseId, KitScratch, visible: true, pinned: true);
    }

    /// <summary>Живой вью лежащей/висящей одежды.</summary>
    public static void ReportGroundGarment(int objectId, string definitionId, bool visible)
    {
        if (!Enabled || string.IsNullOrEmpty(definitionId))
        {
            return;
        }

        KitScratch.Clear();
        KitScratch.Add("wear/" + definitionId);
        Report("obj/" + objectId, KitScratch, visible, pinned: false);
    }

    /// <summary>Вью уничтожен (обычный деспаун или вытеснение) — снять ссылки.</summary>
    public static void ForgetNpc(int npcId) => Forget("npc/" + npcId);

    public static void ForgetObject(int objectId) => Forget("obj/" + objectId);

    public static void ForgetCorpse(int npcId) => Forget("corpse/" + npcId);

    /// <summary>§28.15C: вью погибшей переезжает в реестр тел как есть. Тело
    /// не вытесняется (их мало, и «моргнуть трупом» хуже экономии) — запись
    /// переезжает под corpse-ключ с пином.</summary>
    public static void TransferNpcToCorpse(int npcId)
    {
        var npcKey = "npc/" + npcId;
        if (!Entries.TryGetValue(npcKey, out var entry))
        {
            return;
        }

        Entries.Remove(npcKey);
        entry.Pinned = true;
        entry.HiddenSince = -1.0;
        Entries["corpse/" + npcId] = entry;
    }

    /// <summary>Конец снапшот-прохода: вытеснить из буфера лишнее.
    /// <paramref name="evictView"/> — рендерер уничтожает вью сущности и
    /// возвращает true; ссылки на комплекты снимаются здесь после этого.</summary>
    public static void EndPass(Func<string, bool> evictView)
    {
        if (!Enabled)
        {
            return;
        }

        // Повтор комплектов, чьи семьи в прошлый раз были заняты загрузкой.
        if (RetryKits.Count > 0)
        {
            var retry = RetryKits.ToArray();
            RetryKits.Clear();
            foreach (var kit in retry)
            {
                // За время ожидания комплект могли снова взять в работу.
                if (!KitRefs.ContainsKey(kit))
                {
                    ReleaseKit(kit);
                }
            }
        }

        if (_sweep is { isDone: true })
        {
            _sweep = null;
        }

        // Под занавесом не вытесняем: гейт загрузки ждёт тела ростера, и
        // выгрузка из-под него — это зависание #146 по новому адресу.
        if (UI.LoadingScreen.IsActive)
        {
            return;
        }

        var hiddenBytes = 0L;
        foreach (var entry in Entries.Values)
        {
            if (!entry.Pinned && entry.HiddenSince >= 0.0)
            {
                hiddenBytes += entry.Bytes;
            }
        }

        var guard = 0;
        while (hiddenBytes > BudgetBytes && guard++ < 32)
        {
            string oldestKey = null;
            Entry oldest = null;
            foreach (var pair in Entries)
            {
                if (pair.Value.Pinned || pair.Value.HiddenSince < 0.0)
                {
                    continue;
                }
                if (oldest == null || pair.Value.HiddenSince < oldest.HiddenSince)
                {
                    oldest = pair.Value;
                    oldestKey = pair.Key;
                }
            }

            if (oldest == null ||
                Time.unscaledTimeAsDouble - oldest.HiddenSince < MinHiddenSeconds)
            {
                break;
            }

            if (!evictView(oldestKey))
            {
                break;
            }

            Debug.Log($"[Residency] вытеснение {oldestKey}: " +
                $"{oldest.Bytes / (1024 * 1024)} МБ, буфер был " +
                $"{hiddenBytes / (1024 * 1024)}/{BudgetBytes / (1024 * 1024)} МБ");
            hiddenBytes -= oldest.Bytes;
            Forget(oldestKey);
        }

        if (_releasedSinceSweep >= SweepThresholdBytes && _sweep == null)
        {
            Debug.Log($"[Residency] Resources.UnloadUnusedAssets после " +
                $"{_releasedSinceSweep / (1024 * 1024)} МБ освобождённых комплектов");
            _releasedSinceSweep = 0;
            _sweep = Resources.UnloadUnusedAssets();
        }
    }

    private static void AddKit(string kit)
    {
        if (!KitScratch.Contains(kit))
        {
            KitScratch.Add(kit);
        }
    }

    private static void Report(string entityKey, List<string> kits, bool visible, bool pinned)
    {
        if (!Entries.TryGetValue(entityKey, out var entry))
        {
            entry = new Entry();
            Entries[entityKey] = entry;
        }

        var sameKits = entry.Kits.Length == kits.Count;
        if (sameKits)
        {
            for (var i = 0; i < kits.Count; i++)
            {
                if (!string.Equals(entry.Kits[i], kits[i], StringComparison.Ordinal))
                {
                    sameKits = false;
                    break;
                }
            }
        }

        if (!sameKits)
        {
            // Переоделась/сменила причёску: сперва новые ссылки, потом снятие
            // старых — общий комплект не должен провалиться через ноль.
            var next = kits.ToArray();
            foreach (var kit in next)
            {
                AddRef(kit);
            }
            foreach (var kit in entry.Kits)
            {
                ReleaseRef(kit);
            }
            entry.Kits = next;
            entry.Bytes = 0;
            foreach (var kit in next)
            {
                entry.Bytes += KitBytes(kit);
            }
        }

        entry.Pinned = pinned;
        if (visible)
        {
            entry.HiddenSince = -1.0;
        }
        else if (entry.HiddenSince < 0.0)
        {
            entry.HiddenSince = Time.unscaledTimeAsDouble;
        }
    }

    private static void Forget(string entityKey)
    {
        if (!Entries.TryGetValue(entityKey, out var entry))
        {
            return;
        }

        Entries.Remove(entityKey);
        foreach (var kit in entry.Kits)
        {
            ReleaseRef(kit);
        }
    }

    private static void AddRef(string kit)
    {
        KitRefs.TryGetValue(kit, out var count);
        KitRefs[kit] = count + 1;
    }

    private static void ReleaseRef(string kit)
    {
        if (!KitRefs.TryGetValue(kit, out var count))
        {
            return;
        }

        if (count > 1)
        {
            KitRefs[kit] = count - 1;
            return;
        }

        KitRefs.Remove(kit);
        ReleaseKit(kit);
    }

    private static void ReleaseKit(string kit)
    {
        var slash = kit.IndexOf('/');
        if (slash <= 0)
        {
            return;
        }

        var family = kit.Substring(0, slash);
        var id = kit.Substring(slash + 1);
        var released = family switch
        {
            // Иконка (третий хэндл того же бандла) намеренно НЕ трогается:
            // списки инвентаря показывают её для любого экземпляра вещи.
            "wear" => Wearing.ActorWardrobe.Evict(id) &&
                Wearing.Garments.GarmentVariants.Evict(id),
            "hair" => Wearing.HairContent.Evict(id),
            "actor" => EvictActor(id),
            _ => true,
        };

        if (!released)
        {
            RetryKits.Add(kit);
            return;
        }

        _releasedSinceSweep += KitBytes(kit);
    }

    private static bool EvictActor(string actorMesh)
    {
        if (!ContentPrefabCache.Evict("actor", actorMesh))
        {
            return false;
        }

        // Карты покраски кожи этой актрисы — данные её тела; уходят вместе.
        AtomicResources.EvictPath("HexLive/PaintMaps/skin_" + actorMesh);
        AtomicResources.EvictPath("HexLive/PaintMaps/skinpos_" + actorMesh);
        Wearing.PaintPointMap.Evict("skin_" + actorMesh);
        Wearing.SkinPositionMapSet.Evict("skinpos_" + actorMesh);
        return true;
    }

    private static long KitBytes(string kit)
    {
        if (KitBytesCache.TryGetValue(kit, out var cached))
        {
            return cached;
        }

        var slash = kit.IndexOf('/');
        var family = kit.Substring(0, slash);
        var id = kit.Substring(slash + 1);
        if (family == "wear")
        {
            // Скрафченная вещь без своего арта весит бандл донора.
            id = Wearing.WearArtAliases.ArtId(id);
        }

        long bytes = 0;
        if (ContentAssetService.Instance.TryGetRecord(family, id, out var record))
        {
            bytes = record.variant?.size ?? 0;
        }

        KitBytesCache[kit] = bytes;
        return bytes;
    }
}

}
