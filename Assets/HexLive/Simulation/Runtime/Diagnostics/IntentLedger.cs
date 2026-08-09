using System.Collections.Generic;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Runtime
{

/// <summary>Чем кончилось намерение. Всё, кроме <c>Completed</c>, — «не дошла».</summary>
public enum IntentOutcome
{
    /// <summary>Ещё идёт.</summary>
    Open,

    /// <summary>Дело сделано: план дошёл до <c>PlanStatus.Completed</c>.</summary>
    Completed,

    /// <summary>План провалился (<c>Failed</c>/<c>Invalid</c>) — цель не достигнута.</summary>
    Failed,

    /// <summary>Бросила ради другого дела, не доведя и не провалив явно.</summary>
    Abandoned,
}

/// <summary>Одна попытка: «хотела ЭТО от ЭТОГО, и вот чем кончилось».</summary>
public readonly struct IntentRecord
{
    public IntentRecord(int openedTick, int closedTick, GoalType goal,
        ObjectId? target, EntityId? agent, IntentOutcome outcome)
    {
        OpenedTick = openedTick;
        ClosedTick = closedTick;
        Goal = goal;
        Target = target;
        Agent = agent;
        Outcome = outcome;
    }

    public int OpenedTick { get; }

    /// <summary>0, пока запись открыта.</summary>
    public int ClosedTick { get; }

    public GoalType Goal { get; }

    /// <summary>Объект, на который был нацелен план (<c>Plan.TargetObjectId</c>).</summary>
    public ObjectId? Target { get; }

    /// <summary>Персонаж-цель (<c>Plan.TargetAgentId</c>) — для помощи, спасения, абьюза.</summary>
    public EntityId? Agent { get; }

    public IntentOutcome Outcome { get; }

    public IntentRecord Close(int tick, IntentOutcome outcome) =>
        new IntentRecord(OpenedTick, tick, Goal, Target, Agent, outcome);

    /// <summary>Совпадает ли ПРИЦЕЛ — цель и то, на что она наведена.</summary>
    public bool SameAim(in IntentRecord other) =>
        Goal == other.Goal &&
        Target?.Value == other.Target?.Value &&
        Agent?.Value == other.Agent?.Value;

    public override string ToString() =>
        Goal + AimSuffix() + " " + OpenedTick + ".." +
        (ClosedTick == 0 ? "…" : ClosedTick.ToString()) + " " + Outcome;

    public string AimSuffix()
    {
        if (Target is { } obj)
        {
            return "@obj" + obj.Value;
        }

        return Agent is { } npc ? "@npc" + npc.Value : string.Empty;
    }
}

/// <summary>
/// Реестр намерений, spec §122: что NPC хотела и чем это КОНЧИЛОСЬ.
///
/// <para>
/// ⭐ Зачем он вообще. Симуляция умела отвечать «чем она занята» и не умела —
/// «двигается ли она куда-нибудь». Двадцать раз выбрать цель и ни разу её не
/// завершить механически неотличимо от работы: цель есть, план строится, ноги
/// идут. Ровно поэтому <see cref="StuckDiagnosticSystem"/> (§30.15) видит только
/// ДЕДЛОК — четыре формы «стоит», — а петля (ЛАЙВЛОК) проходит все его проверки
/// насквозь. Прогресс надо где-то записывать, иначе его нельзя измерить.
/// </para>
/// <para>
/// ⚠️ Почему это НАБЛЮДАТЕЛЬ, а не запись из мест провала. Провал плана сносится
/// через <c>PlanInterruption.Abort</c> — 96 вызовов из 26 файлов, плюс ~70 мест,
/// ставящих <c>PlanStatus.Failed</c>. Просить каждое из них «ещё и записать
/// попытку» значит завести 166 мест, где можно забыть, — той самой болезнью
/// болел <c>Memory.Shun</c> (ставится в 7 местах, проверяется в 18), и баг #67
/// был буквально «GetWater забыл проверить shun». Здесь вместо этого читается
/// состояние, которое уже есть: <c>Mind.CurrentGoal</c>, <c>Plan.Status</c>,
/// <c>Plan.Target*</c>. Забыть нечего.
/// </para>
/// <para>
/// Считается ИЗ СОСТОЯНИЯ, а не разбором текста событий — та же причина, что у
/// <c>SoakMetrics</c>: <c>Message</c> это формат для людей и для истории колонии,
/// и парсить его ради метрики значит завести у неизменяемой строки второго
/// потребителя.
/// </para>
/// <para>
/// Живёт на <c>WorldState.IntentLedger</c>, НЕ сериализуется и НЕ едет в
/// снапшоте — конвенция <see cref="FlightRecorder"/> и <c>Memory.ShunnedUntil</c>:
/// инструмент наблюдения, а не состояние мира. После загрузки отсчёт начинается
/// заново, и это безобидно.
/// </para>
/// <para>
/// ⚠️ В отличие от самописца включён ВСЕГДА, а не только в редакторе. Самописца
/// кормит КАЖДОЕ trace-событие (~200 в тик), а здесь на NPC приходится сравнение
/// трёх полей за тик и кольцо на 32 записи. В релизной сборке игрока детект и
/// автовыход обязаны работать — иначе петля видна только разработчику.
/// </para>
/// </summary>
public sealed class IntentLedger
{
    /// <summary>
    /// Глубина памяти на NPC. 32 попытки — это заведомо больше, чем нужно
    /// подписям §122 (5 повторов, 6 чередований), и заведомо меньше, чем стоило
    /// бы считать перфом.
    /// </summary>
    public const int DefaultCapacity = 32;

    private sealed class Ring
    {
        public readonly IntentRecord[] Items;
        public int Count;
        public int Next;

        /// <summary>Открытая запись живёт ОТДЕЛЬНО от кольца: пока она не
        /// закрыта, её исход неизвестен, а в кольце лежат только состоявшиеся
        /// факты. Иначе «ещё идёт» пришлось бы каждый тик переписывать поверх
        /// последнего слота.</summary>
        public IntentRecord Open;
        public bool HasOpen;

        public Ring(int capacity) => Items = new IntentRecord[capacity];
    }

    private readonly Dictionary<int, Ring> _rings = new Dictionary<int, Ring>();
    private readonly int _capacity;

    public IntentLedger(int capacityPerNpc = DefaultCapacity) => _capacity = capacityPerNpc;

    /// <summary>Открыть намерение. Уже открытое сначала закрывается как брошенное.</summary>
    public void Open(int entityId, int tick, GoalType goal, ObjectId? target, EntityId? agent)
    {
        var ring = RingFor(entityId);
        if (ring.HasOpen)
        {
            Close(entityId, tick, IntentOutcome.Abandoned);
            ring = RingFor(entityId);
        }

        ring.Open = new IntentRecord(tick, 0, goal, target, agent, IntentOutcome.Open);
        ring.HasOpen = true;
    }

    /// <summary>Закрыть текущее намерение. Без открытого — ничего не делает.</summary>
    public void Close(int entityId, int tick, IntentOutcome outcome)
    {
        if (!_rings.TryGetValue(entityId, out var ring) || !ring.HasOpen)
        {
            return;
        }

        var closed = ring.Open.Close(tick, outcome);
        ring.Items[ring.Next] = closed;
        ring.Next = (ring.Next + 1) % _capacity;
        if (ring.Count < _capacity)
        {
            ring.Count++;
        }

        ring.HasOpen = false;
        ring.Open = default;
    }

    /// <summary>Переприцелиться, не меняя цели: старая попытка закрывается,
    /// открывается новая. Это и есть «пошла к другому кокосу».</summary>
    public void Retarget(int entityId, int tick, GoalType goal, ObjectId? target, EntityId? agent)
    {
        Close(entityId, tick, IntentOutcome.Abandoned);
        Open(entityId, tick, goal, target, agent);
    }

    public bool TryGetOpen(int entityId, out IntentRecord open)
    {
        if (_rings.TryGetValue(entityId, out var ring) && ring.HasOpen)
        {
            open = ring.Open;
            return true;
        }

        open = default;
        return false;
    }

    /// <summary>
    /// Последний прицел: открытый, а если открытого нет — последний закрытый.
    /// <para>
    /// ⚠️ Падать назад на закрытый ОБЯЗАТЕЛЬНО. Петля большую часть времени
    /// проводит ровно в промежутке «прошлая попытка провалилась, следующая ещё
    /// не построена», и сторож, который смотрит только на открытую запись,
    /// систематически проверял бы её именно в те моменты, когда смотреть не на
    /// что. Разбор идёт по своему таймеру и в этот промежуток попадает часто.
    /// </para>
    /// </summary>
    public bool TryGetLatestAim(int entityId, out IntentRecord aim)
    {
        if (_rings.TryGetValue(entityId, out var ring))
        {
            if (ring.HasOpen)
            {
                aim = ring.Open;
                return true;
            }

            if (ring.Count > 0)
            {
                aim = ring.Items[(ring.Next - 1 + _capacity) % _capacity];
                return true;
            }
        }

        aim = default;
        return false;
    }

    /// <summary>Закрытые попытки в порядке «сначала старое», как читают лог.</summary>
    public List<IntentRecord> Tail(int entityId, int max = int.MaxValue)
    {
        var tail = new List<IntentRecord>();
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

    /// <summary>
    /// Сколько раз этот прицел брали и сколько раз довели, начиная с
    /// <paramref name="sinceTick"/>. Открытая попытка считается ПОПЫТКОЙ, но не
    /// завершением: иначе петля, застрявшая внутри последней попытки, выглядела
    /// бы на один заход короче, чем она есть.
    /// </summary>
    public void CountAim(int entityId, in IntentRecord aim, int sinceTick,
        out int attempts, out int completions)
    {
        attempts = 0;
        completions = 0;
        if (!_rings.TryGetValue(entityId, out var ring))
        {
            return;
        }

        for (var i = 0; i < ring.Count; i++)
        {
            var record = ring.Items[i];
            if (record.OpenedTick < sinceTick || !record.SameAim(aim))
            {
                continue;
            }

            attempts++;
            if (record.Outcome == IntentOutcome.Completed)
            {
                completions++;
            }
        }

        if (ring.HasOpen && ring.Open.SameAim(aim) && ring.Open.OpenedTick >= sinceTick)
        {
            attempts++;
        }
    }

    /// <summary>
    /// Сколько раз эту ЦЕЛЬ (безразлично, на что нацеленную) брали и доводили с
    /// <paramref name="sinceTick"/>. Нужно подписи <c>NeedStarved</c>: там
    /// виновата вся ветка снабжения, а не конкретный кокос.
    /// </summary>
    public void CountGoal(int entityId, GoalType goal, int sinceTick,
        out int attempts, out int completions)
    {
        attempts = 0;
        completions = 0;
        if (!_rings.TryGetValue(entityId, out var ring))
        {
            return;
        }

        for (var i = 0; i < ring.Count; i++)
        {
            var record = ring.Items[i];
            if (record.OpenedTick < sinceTick || record.Goal != goal)
            {
                continue;
            }

            attempts++;
            if (record.Outcome == IntentOutcome.Completed)
            {
                completions++;
            }
        }

        if (ring.HasOpen && ring.Open.Goal == goal && ring.Open.OpenedTick >= sinceTick)
        {
            attempts++;
        }
    }

    public void Forget(int entityId) => _rings.Remove(entityId);

    public void Clear() => _rings.Clear();

    private Ring RingFor(int entityId)
    {
        if (!_rings.TryGetValue(entityId, out var ring))
        {
            ring = new Ring(_capacity);
            _rings[entityId] = ring;
        }

        return ring;
    }
}

}
