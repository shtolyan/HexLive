using System.Collections.Generic;

namespace HexLive.UnityPresentation.Wearing.Garments
{

// Очередь загрузки контента: одно место, которое знает, ЧТО сейчас едет и
// СКОЛЬКО осталось.
//
// Зачем вместо счётчиков. Счётчик отвечает только «ещё не всё» — и экрану
// загрузки остаётся врать полоской и страховаться таймаутом. Очередь знает
// сделано/всего, поэтому прогресс настоящий, а ждать можно ДО КОНЦА: таймаут
// не нужен, потому что каждая начатая задача обязана завершиться.
//
// ⭐ Почему завершение гарантировано и таймаут не нужен: Addressables ЗАВЕРШАЕТ
// операцию всегда — и успехом, и провалом. Поэтому End() зовётся из обработчика
// в любом случае, включая «адреса нет». Задача, начатая без End(), — это
// ошибка в коде загрузчика, а не повод вешать страховку на экран.
//
// Подписи — по ВИДУ задачи, а не по имени файла: игроку интересно «шьём
// одежду», а не «wear/underwear.briefs_plain». Сами строки живут в I2 (§58),
// здесь только ключи.
public static class ContentQueue
{
    public enum Kind
    {
        Wear,
        Hair,
        HairColour,
        Icon,
        Prosthetic,
    }

    private static readonly Dictionary<Kind, int> Started = new();
    private static readonly Dictionary<Kind, int> Finished = new();

    // Enter Play Mode can be configured without a domain reload. In that mode
    // the dictionaries survive the previous run and make a fresh loading
    // screen observe work that belongs to an already destroyed world.
    [UnityEngine.RuntimeInitializeOnLoadMethod(
        UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => Reset();

    /// <summary>Всё загружено — очередь пуста.</summary>
    public static bool IsIdle
    {
        get
        {
            foreach (var pair in Started)
            {
                if (Finished.GetValueOrDefault(pair.Key) < pair.Value)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Доля сделанного, 0..1. Пустая очередь — это единица, а не ноль.</summary>
    public static float Progress
    {
        get
        {
            var total = 0;
            var done = 0;
            foreach (var pair in Started)
            {
                total += pair.Value;
                done += Finished.GetValueOrDefault(pair.Key);
            }

            return total == 0 ? 1f : done / (float)total;
        }
    }

    /// <summary>Сколько задач ещё в пути — для строки «12 из 40».</summary>
    public static int Remaining
    {
        get
        {
            var left = 0;
            foreach (var pair in Started)
            {
                left += pair.Value - Finished.GetValueOrDefault(pair.Key);
            }

            return left;
        }
    }

    /// <summary>
    /// Ключ I2 для подписи: берётся вид, которого осталось БОЛЬШЕ всего, —
    /// подпись должна называть то, чем занят загрузчик прямо сейчас, а не то,
    /// что случайно оказалось первым в словаре.
    /// </summary>
    public static string MessageKey
    {
        get
        {
            var busiest = Kind.Wear;
            var most = 0;
            foreach (var pair in Started)
            {
                var left = pair.Value - Finished.GetValueOrDefault(pair.Key);
                if (left > most)
                {
                    most = left;
                    busiest = pair.Key;
                }
            }

            return most == 0 ? "loading.content.done" : KeyOf(busiest);
        }
    }

    public static void Begin(Kind kind) =>
        Started[kind] = Started.GetValueOrDefault(kind) + 1;

    public static void End(Kind kind) =>
        Finished[kind] = Finished.GetValueOrDefault(kind) + 1;

    /// <summary>Забыть посчитанное — новый мир начинает с чистой полоски.</summary>
    public static void Reset()
    {
        Started.Clear();
        Finished.Clear();
    }

    private static string KeyOf(Kind kind) => kind switch
    {
        Kind.Wear => "loading.content.wear",
        Kind.Hair => "loading.content.hair",
        Kind.HairColour => "loading.content.colour",
        Kind.Icon => "loading.content.icon",
        Kind.Prosthetic => "loading.content.prosthetic",
        _ => "loading.content.done",
    };
}

}
