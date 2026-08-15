using System.Collections.Generic;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// Бортовой самописец: последние N событий КАЖДОГО NPC, spec §30.14.
///
/// <para>
/// Зачем отдельно от <see cref="SimulationEventBuffer"/>. Общее кольцо держит
/// 2048 записей, а симуляция эмитит ~200 событий в тик — то есть глобальная
/// история живёт около ОДИННАДЦАТИ ТИКОВ. Для «что вообще происходит» этого
/// хватает, а для «почему ВОТ ЭТА застряла» — нет: к моменту, когда застой
/// заметен, всё, что его объясняет, вытеснено чужой болтовнёй тысячу раз.
/// </para>
/// <para>
/// Здесь наоборот: у каждого NPC своё маленькое кольцо, и вытесняют его только
/// ЕГО СОБСТВЕННЫЕ события. У зависшей девушки, которая молчит уже 2872 тика
/// (§102), в кольце так и лежат последние решения ПЕРЕД тем, как она замерла —
/// ровно то, что тогда добывалось ручным printf'ом раз в 120 тиков.
/// </para>
/// <para>
/// Живёт только в отладке: <c>WorldState.FlightRecorder</c> — обычное nullable
/// поле, оно НЕ сериализуется в сейв и НЕ едет в снапшоте, поэтому ни кодек, ни
/// формат сохранения об этом классе не знают. В сборке игрока поле остаётся
/// null, и запись стоит одной проверки на null.
/// </para>
/// </summary>
public sealed class FlightRecorder
{
    public readonly struct Entry
    {
        public Entry(int tick, string type, string message)
        {
            Tick = tick;
            Type = type;
            Message = message;
        }

        public int Tick { get; }

        public string Type { get; }

        public string Message { get; }

        public override string ToString() => "[" + Tick + "] " + Type + " " + Message;
    }

    private sealed class Ring
    {
        public readonly Entry[] Items;
        public int Count;
        public int Next;

        public Ring(int capacity) => Items = new Entry[capacity];
    }

    private readonly Dictionary<int, Ring> _rings = new Dictionary<int, Ring>();
    private readonly int _capacity;

    /// <summary>
    /// Что писать. Пусто = писать всё, что несёт EntityId. Набор задаётся
    /// снаружи, а не зашит здесь: «интересное» у соака, у панели и у охоты за
    /// конкретным багом разное, и зашитый список пришлось бы править под каждый
    /// случай — той же болезнью болел вайтлист событий.
    /// </summary>
    public readonly HashSet<string> Types = new HashSet<string>(System.StringComparer.Ordinal);

    /// <summary>
    /// Разумный набор по умолчанию: РЕШЕНИЯ и ОТКАЗЫ, без потока сознания.
    /// <para>
    /// Он не случайный. §63 разбирал 25-дневные соаки, где журнал урона у всех
    /// смертей читался одинаково («истощение»), и ПОЧЕМУ говорил только хвост
    /// поведения — `GoalSelected` / `PlanFailed` / `ExecFailed` /
    /// `InteractionStarted`. Здесь тот же набор плюс то, чем оплачен §102:
    /// прерывания, обрывы плана и провалы пути.
    /// </para>
    /// <para>
    /// Почему не «писать всё»: одно решение эмитит ~55 <c>GoalScored</c>, и
    /// кольцо на 64 записи забивается ОДНИМ проходом аукциона — хвост есть, а
    /// истории в нём нет. Оценки смотрят в панели, где они разложены по
    /// модификаторам; сюда они не помещаются по смыслу, а не по размеру.
    /// </para>
    /// </summary>
    public static readonly string[] BehaviorTypes =
    {
        "GoalSelected",
        "GoalInterrupted",
        "GoalCooldownSet",
        "PlanStarted",
        "PlanFailed",
        "PlanAborted",
        "InteractionStarted",
        "ExecFailed",
        "ExecWaitingForArrival",
        "PathBlocked",
        "PathFailed",
        "StuckDetected",
        // §122: петля — это то, что объясняет ПРЕДЫДУЩИЕ записи хвоста, а не
        // ещё одна строка шума. Без неё читающий хвост видит десять PlanFailed
        // подряд и должен сам догадаться, что это один и тот же круг.
        "LoopDetected",
        // §30.15: death analysis needs the decisions around danger, not only
        // the terminal cause. Deliberately omit the per-tick "Dying" pulse —
        // it would evict the fight it is meant to explain.
        "DogFight",
        "FleeStarted",
        "FleeUnavailable",
        "FleeStalled",
        "Collapsed",
        "BledOut",
        "VitalPartDestroyed",
        "Drowned",
        "Bandaged",
        "Medicated",
        "Rescued",
        "FriendGuard",
        "HelpMoan",
    };

    public FlightRecorder(int capacityPerNpc = 64) => _capacity = capacityPerNpc;

    /// <summary>Самописец с набором <see cref="BehaviorTypes"/>.</summary>
    public static FlightRecorder ForBehavior(int capacityPerNpc = 64)
    {
        var recorder = new FlightRecorder(capacityPerNpc);
        foreach (var type in BehaviorTypes)
        {
            recorder.Types.Add(type);
        }

        return recorder;
    }

    public void Record(int entityId, int tick, string type, string message)
    {
        if (Types.Count > 0 && !Types.Contains(type))
        {
            return;
        }

        if (!_rings.TryGetValue(entityId, out var ring))
        {
            ring = new Ring(_capacity);
            _rings[entityId] = ring;
        }

        ring.Items[ring.Next] = new Entry(tick, type, message);
        ring.Next = (ring.Next + 1) % _capacity;
        if (ring.Count < _capacity)
        {
            ring.Count++;
        }
    }

    /// <summary>Хвост в порядке «сначала старое», как читают лог.</summary>
    public List<Entry> Tail(int entityId, int max = int.MaxValue)
    {
        var tail = new List<Entry>();
        if (!_rings.TryGetValue(entityId, out var ring) || ring.Count == 0)
        {
            return tail;
        }

        var take = ring.Count < max ? ring.Count : max;
        var first = (ring.Next - take + _capacity * 2) % _capacity;
        for (var i = 0; i < take; i++)
        {
            tail.Add(ring.Items[(first + i) % _capacity]);
        }

        return tail;
    }

    public void Forget(int entityId) => _rings.Remove(entityId);

    public void Clear() => _rings.Clear();
}

}
