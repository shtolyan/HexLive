using System;
using System.Collections.Generic;
using HexLive.Simulation.Runtime;

namespace HexLive.Server.Mcp
{

/// <summary>
/// Зеркало хроники для тянущего агента (§144.6).
/// <para>
/// ⭐ Зачем зеркало, а не чтение <c>world.Events</c> напрямую. Кольцо симуляции
/// держит 2048 записей при ИЗМЕРЕННЫХ ~174 событиях в тик (см. комментарий в
/// <see cref="WorldHost"/> о 177.8/174.4) — это 11.8 тика, около трёх секунд
/// реального времени на скорости 1×. Агент, который думает над ответом дольше
/// трёх секунд — то есть любой, — получал бы «дыру» почти на каждом опросе и
/// не увидел бы ровно того, ради чего пришёл: драки, разговора, обморока.
/// </para>
/// <para>
/// Зеркало хранит ТОЛЬКО то, что прошло фильтр, поэтому те же 1024 ячейки — это
/// сотни тиков, а не одиннадцать: болтовню ИИ (~97% объёма) сюда не пускают, и
/// вытеснить ею событие боя невозможно.
/// </para>
/// <para>
/// Seq не переизобретается: копируется из <see cref="SimulationEvent.Seq"/>,
/// поэтому вотермарка агента сравнима с вотермаркой зрителя, и два потребителя
/// не разъедутся в понимании «после какого места».
/// </para>
/// </summary>
public sealed class McpEventLog
{
    /// <summary>
    /// Сотни тиков хроники против одиннадцати у сырого кольца. Числа сверх этого
    /// не помогают: агент, отставший на минуты, обязан перечитать состояние, а не
    /// доигрывать ленту.
    /// </summary>
    public const int DefaultCapacity = 1024;

    /// <summary>
    /// Типы, которые адресованы КОНТРОЛЛЕРУ, а не миру. В
    /// <c>GameEventTypes</c> их намеренно нет: попади они туда — и отказ приказа
    /// полез бы в хронику колонии, в дневники NPC (§136) и каждому зрителю.
    /// Здесь же они на месте: это ответ на вопрос «что стало с моим приказом».
    /// <para>
    /// ⚠️ Сегодня из трёх доходит ОДИН: <c>ManualOrderRejected</c> идёт через
    /// <c>Trace.Emit</c>, а <c>ManualOrderAccepted</c> и <c>ManualOrderFinished</c> —
    /// через <c>Trace.Debug</c>, который на сервере выключен по умолчанию. Имена
    /// перечислены заранее, чтобы разгейтить их отдельным осознанным коммитом, а
    /// не дописывать сюда задним числом. До тех пор «приказ умер через 200 тиков»
    /// отвечает не лента, а состояние.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> ControllerVisible =
        new(StringComparer.Ordinal)
        {
            "ManualOrderAccepted",
            "ManualOrderRejected",
            "ManualOrderFinished",
            "ItemRequestResult",
        };

    private readonly List<EventRecord> _events = new();
    private readonly int _capacity;

    public McpEventLog(int capacity = DefaultCapacity)
    {
        _capacity = capacity > 0 ? capacity : DefaultCapacity;
        SessionEpoch = Guid.NewGuid().ToString("N")[..8];
    }

    /// <summary>
    /// Вершина ПРОСМОТРЕННОГО, а не сохранённого: seq кольца на момент
    /// последнего слива, включая всё, что фильтр отбросил.
    /// <para>
    /// ⭐ Разница не косметическая. Считай сюда только пропущенные записи — и
    /// вотермарка застрянет на последнем видимом событии, а агент будет
    /// пересматривать ту же болтовню ИИ (97% объёма) на каждом вызове, вечно.
    /// Зритель избегает этого тем же приёмом: берёт вотермарку из
    /// <c>buffer.HighestSeq</c>, а не из отданной пачки.
    /// </para>
    /// </summary>
    public long HighestSeq { get; private set; }

    /// <summary>Seq самой старой ЕЩЁ ХРАНИМОЙ записи; ниже неё — потеряно навсегда.</summary>
    public long OldestHeld => _events.Count > 0 ? _events[0].Seq : HighestSeq + 1;

    /// <summary>
    /// Меняется при перезапуске процесса и при загрузке сейва. Без него агент,
    /// держащий вотермарку 800, не отличит нормальный ход от чужого мира,
    /// успевшего дойти до 900.
    /// </summary>
    public string SessionEpoch { get; private set; }

    public static bool IsVisible(SimulationEvent simulationEvent) =>
        simulationEvent != null &&
        (GameEventTypes.IsPlayerVisible(simulationEvent) ||
         ControllerVisible.Contains(simulationEvent.Type));

    /// <summary>
    /// Забирает из кольца симуляции всё новее <paramref name="drainedSeq"/> и
    /// возвращает новую позицию слива. Вызывается ТОЛЬКО под замком мира.
    /// </summary>
    public long Drain(SimulationEventBuffer buffer, long drainedSeq)
    {
        if (buffer is null)
        {
            return drainedSeq;
        }

        var items = buffer.Items;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Seq <= drainedSeq || !IsVisible(item))
            {
                continue;
            }

            // Message копируется ДОСЛОВНО: из него достают id зверя и разбирают
            // Kind=/Cause=[…]. Третий потребитель не имеет права его причёсывать.
            _events.Add(new EventRecord(
                item.Seq, item.Tick, item.Type, item.Message, item.EntityId));
        }

        var overflow = _events.Count - _capacity;
        if (overflow > 0)
        {
            _events.RemoveRange(0, overflow);
        }

        if (buffer.HighestSeq > HighestSeq)
        {
            HighestSeq = buffer.HighestSeq;
        }

        // ⭐ Слив двигается до вершины кольца, а НЕ до последней принятой записи.
        // Иначе отфильтрованная болтовня пересматривалась бы на каждом вызове —
        // те самые 97% объёма, ради отсечения которых фильтр и существует.
        return buffer.HighestSeq;
    }

    /// <summary>
    /// Загрузка сейва: мир стал другим, а хроника осталась бы от прежнего. Без
    /// этого агент получил бы «дерево срублено» про дерево, которое в
    /// восстановленном мире стоит.
    /// </summary>
    public void Reset()
    {
        _events.Clear();
        HighestSeq = 0;
        SessionEpoch = Guid.NewGuid().ToString("N")[..8];
    }

    /// <param name="sinceSeq">
    /// <c>null</c> — агент только пришёл: отдаём пустой список и текущую
    /// вотермарку, а не всё накопленное. Это НЕ то же самое, что 0: ноль значит
    /// «с самого начала того, что храним», и его передают осознанно.
    /// </param>
    public EventBatch Read(long? sinceSeq, int limit, int? entityId)
    {
        if (limit <= 0)
        {
            limit = 100;
        }

        if (sinceSeq is not { } since)
        {
            return new EventBatch(
                Array.Empty<EventRecord>(), HighestSeq, OldestHeld, false, false, false);
        }

        var rows = new List<EventRecord>();
        var truncated = false;

        for (var i = 0; i < _events.Count; i++)
        {
            var e = _events[i];
            if (e.Seq <= since)
            {
                continue;
            }

            if (entityId is { } wanted && e.EntityId != wanted)
            {
                continue;
            }

            if (rows.Count >= limit)
            {
                truncated = true;
                break;
            }

            rows.Add(e);
        }

        // Вотермарка идёт до вершины — мимо отфильтрованного. Но НЕ мимо того,
        // что не поместилось в limit: те записи агенту не отдали, и объявить их
        // прочитанными значит потерять их молча.
        var watermark = truncated && rows.Count > 0 ? rows[^1].Seq : HighestSeq;

        return new EventBatch(
            rows,
            watermark,
            OldestHeld,
            // Кольцо подрезало мимо позиции агента: пропущенное не вернуть, и
            // доигрывать остаток нельзя — вниз по течению это дубликаты.
            Gap: since + 1 < OldestHeld,
            // Seq откатился ниже позиции агента — это чужой мир, а не наш.
            SessionReset: HighestSeq < since,
            Truncated: truncated);
    }
}

}
