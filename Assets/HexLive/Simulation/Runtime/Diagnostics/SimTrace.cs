using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

/// <summary>§30.17: ТРАССИРОВКА — OPT-IN. Поток событий разделён на два яруса.
/// <para>
/// ХРОНИКА (<see cref="Trace.Emit"/>) — события белого списка
/// <c>GameEventTypes</c>, ответы на приказы игрока и причины смерти: это часть
/// игры (лента колонии, звук §67, речь, серверный канал, DeathRecord.Cause),
/// она пишется ВСЕГДА и гейта не имеет.
/// </para>
/// <para>
/// ДИАГНОСТИКА (<see cref="Trace.Debug"/>) — всё остальное: восприятие,
/// скоринг, шаги пути, вехи планов. По умолчанию НЕ ПИШЕТСЯ ВООБЩЕ: замерено
/// 204 события и ~38 КБ строк на тик в прототипном мире из четырёх девушек,
/// из которых 95% дают две точки. Гейт стоит НА МЕСТЕ ВЫЗОВА, до интерполяции,
/// поэтому при выключенном флаге строка не строится вовсе — проверка внутри
/// метода срезала бы только объект события, а строка стоит вчетверо больше.
/// </para>
/// <para>
/// ИНВАРИАНТ: положение флагов НЕ МЕНЯЕТ ПОВЕДЕНИЕ МИРА. Ни одна система не
/// читает <c>world.Events</c> (последний такой читатель — причина смерти —
/// переведён на состояние), поэтому golden-трасса обязана совпадать при любом
/// положении переключателей.
/// </para></summary>
public static class SimTrace
{
    /// <summary>Мастер-выключатель диагностики. По умолчанию ВЫКЛЮЧЕН: игра не
    /// платит за то, чего никто не читает. Включают осознанно — hexsoak, тесты,
    /// сервер, редактор, тумблер дебаг-панели, флаг плеера.</summary>
    public static bool Enabled;

    /// <summary>Болтовня восприятия (<c>PerceivedObject</c>, ~159 событий на
    /// тик — 78% всего потока). Подканал: не включается вместе с мастером,
    /// её просят поимённо.</summary>
    public static bool Perception;

    /// <summary>Полный вывод блоков оценки (<c>GoalScored</c>, ~34 события на
    /// тик — 17% потока). Нужен ровно одному потребителю: эталонной трассе
    /// <c>golden_trace.sh --preset scores</c>, которая стережёт порядок
    /// float-операций в DecisionSystem.</summary>
    public static bool Scores;

    /// <summary>Легаси-имя мастера. §30.17 переименовал флаг, но презентация и
    /// сервер выставляют его по-старому; оставлено, пока обе стороны не
    /// переедут на <see cref="Enabled"/>.</summary>
    public static bool Verbose
    {
        get => Enabled;
        set => Enabled = value;
    }

    /// <summary>Включить всё: мастер и оба подканала. Ровно это делает
    /// hexsoak и любой инструмент, которому трасса и есть продукт.</summary>
    public static void EnableAll()
    {
        Enabled = true;
        Perception = true;
        Scores = true;
    }
}

internal static class Trace
{
    /// <summary>§30.17: ДИАГНОСТИЧЕСКОЕ событие. Вызов ОБЯЗАН стоять под
    /// <c>if (SimTrace.Enabled)</c> (или под подканалом) — иначе интерполяция
    /// аргумента выполнится всё равно, и гейт не сэкономит ничего, кроме
    /// объекта события. За этим следит линт <c>TraceGateLint</c>.</summary>
    public static void Debug(WorldState world, EntityId entityId, string type, string message) =>
        Emit(world, entityId, type, message);

    /// <summary>Диагностическое событие без владельца — см. <see cref="Debug"/>.</summary>
    public static void DebugSystem(WorldState world, string type, string message) =>
        EmitSystem(world, type, message);

    public static void Emit(WorldState world, EntityId entityId, string type, string message)
    {
        world.Events.Add(new SimulationEvent
        {
            Tick = world.Tick,
            EntityId = entityId.Value,
            Type = type,
            Message = message
        });

        // Единственная точка, которая видит КАЖДОЕ событие ровно один раз —
        // поэтому самописец висит здесь, а не у потребителей. В сборке игрока
        // поле null, и это стоит одной проверки (spec §30.14).
        world.FlightRecorder?.Record(entityId.Value, world.Tick, type, message);

        // §136: и по той же причине здесь же кормится дневник. Он сам отсеет
        // всё, что не «хроника», — отладочная болтовня до записи не доходит.
        Journal.NpcJournalIntake.Offer(world, entityId, type, message);

        // §30.17: по той же причине здесь же ставится штамп причины смерти.
        // Он ОБЯЗАН быть состоянием, а не поиском по кольцу: кольцо — это
        // диагностика, его глубина зависит от многословности трассы, а Cause
        // уходит в сейв и по проводу (см. NpcMind.DeathCauseText).
        if (DeathCauses.Contains(type) &&
            world.Entities.Npcs.TryGetValue(entityId, out var dying))
        {
            dying.Mind.DeathCauseText = type + ": " + message;
            dying.Mind.DeathCauseTick = world.Tick;
        }
    }

    /// <summary>§30.17: события, которые ОБЪЯСНЯЮТ смерть. Список один на игру
    /// и живёт здесь, рядом со штампом; MobSystem читает готовую строку.
    /// Все они — «хроника», а не болтовня: гейт трассировки их не касается,
    /// иначе причина смерти зависела бы от положения отладочного флага.</summary>
    private static readonly System.Collections.Generic.HashSet<string> DeathCauses =
        new(System.StringComparer.Ordinal)
        {
            "BledOut",
            "DogFight",
            "Drowned",
            "Heatstroke",
            "Hypothermia",
            "LimbSevered",
            "PreyFoughtBack",
            "Preyed",
            // §72: без этих двух каждая смерть в рейде записывалась бы как
            // выведенное истощение — и соак врал бы про ту самую механику,
            // которую им тюнят.
            "RaidFoughtBack",
            "RaidStruck",
            "SharkBite",
            "StarvedToDeath",
            "Sunburn",
            "VitalPartDestroyed",
        };

    public static void EmitSystem(WorldState world, string type, string message)
    {
        world.Events.Add(new SimulationEvent
        {
            Tick = world.Tick,
            EntityId = null,
            Type = type,
            Message = message
        });

        // §136: системное событие тоже бывает личным — «убита», «зверь унёс
        // ногу». Хозяина записи в этом случае указывает каталог: у события нет
        // EntityId, но человек в сообщении есть.
        Journal.NpcJournalIntake.Offer(world, null, type, message);
    }

    public static string FormatTile(TileCoord? tile) => tile is null ? "-" : $"{tile.Value.Q},{tile.Value.R}";

    public static string FormatNeeds(NPCNeeds n) =>
        $"H={n.Hunger:F2} W={n.Thirst:F2} E={n.Energy:F2} C={n.Comfort:F2} S={n.Social:F2} T={n.ThermalDiscomfort:F2}";

    public static string FormatPos(Float2 p) => $"({p.X:F2},{p.Y:F2})";

    public static string FormatJunction(JunctionId? j) => j is null ? "-" : j.Value.Value.ToString();
}

}
