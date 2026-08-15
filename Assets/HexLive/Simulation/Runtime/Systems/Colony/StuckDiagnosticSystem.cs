using System.Collections.Generic;
using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// Spec §30.15: «эта никуда не движется» — сказанное вслух.
///
/// <para>
/// Повод — §102. Чужак простоял напротив жертвы <b>2872 тика подряд</b>, и всё
/// это время симуляция об этом НЕ СКАЗАЛА НИЧЕГО: у <c>RunAbuse</c> четыре голых
/// <c>return</c> до всякой трассировки, а гейты <c>DecisionSystem</c> молчат по
/// построению. Застой — единственное состояние, которое нельзя заметить, слушая
/// события, потому что застой это и есть их отсутствие. Значит его надо
/// проверять, а не ждать сообщения.
/// </para>
/// <para>
/// ⚠️ <b>Система ничего не меняет.</b> Она читает состояние и эмитит; ни одного
/// присваивания в мир. Это её контракт: диагностика, которая лечит, — уже не
/// диагностика, и golden-трасса обязана остаться байт-в-байт той же после её
/// появления. Отсюда же место в конце регистра: наблюдателю всё равно, кто
/// отработал раньше, а вот ему мешать нельзя никому.
/// </para>
/// <para>
/// Состояние наблюдения (с какого тика длится подозрение) живёт ВНУТРИ системы,
/// а не на NPC: оно не влияет на симуляцию, не должно попадать в сейв и в
/// снапшот и не переживает загрузку — самое большее, что случится, это
/// повторный отсчёт с нуля после загрузки.
/// </para>
/// </summary>
public sealed class StuckDiagnosticSystem : ISimulationSystem
{
    public string Name => nameof(StuckDiagnosticSystem);

    public TickLayer Layer => TickLayer.Slow;

    /// <summary>Что мы подозреваем и с какого тика.</summary>
    private struct Watch
    {
        public string Reason;
        public GoalType Goal;
        public int SinceTick;
        public int LastEmitTick;

        // Для PositionFrozen: где стояла, когда начали смотреть.
        public Float2 Anchor;
        public float RotationDegrees;
        public int MovementPathIndex;
        public int PlanStepIndex;
        public JunctionId? TargetJunction;
        public int LastCompletedTick;
    }

    private readonly Dictionary<int, Watch> _watch = new Dictionary<int, Watch>();
    private readonly List<int> _gone = new List<int>();

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            // §121: ручная колонистка стоит без цели, пока игрок не прикажет —
            // это не «застряла», а ровно то, что он велел. Сторож её пропускает,
            // иначе каждый простой между приказами шёл бы в отчёт как баг.
            if (WatchdogExclusions.IsPlayerDriven(npc))
            {
                continue;
            }

            Examine(world, npc);
        }

        // Мёртвые и ушедшие не должны копиться в словаре живого процесса.
        _gone.Clear();
        foreach (var id in _watch.Keys)
        {
            if (!world.Entities.Npcs.ContainsKey(new EntityId(id)))
            {
                _gone.Add(id);
            }
        }

        foreach (var id in _gone)
        {
            _watch.Remove(id);
        }
    }

    private void Examine(WorldState world, NPCState npc)
    {
        var id = npc.Id.Value;

        // Без сознания — не застой, а сюжет. Спящая и в коме обязаны лежать
        // неподвижно, и жаловаться на это значит утопить настоящие находки.
        // §110: рыдающая лежит неподвижно ровно так же, как спящая.
        if (WatchdogExclusions.IsAuthoredStillness(world, npc) ||
            WatchdogExclusions.IsIntentionalHold(world, npc))
        {
            _watch.Remove(id);
            return;
        }

        var reason = Diagnose(world, npc);
        if (reason == null)
        {
            _watch.Remove(id);
            return;
        }

        if (!_watch.TryGetValue(id, out var watch) ||
            watch.Reason != reason ||
            watch.Goal != npc.Mind.CurrentGoal)
        {
            StartWatch(world, npc, reason);
            return;
        }

        // «Идёт» — не обещание двигать координату КАЖДЫЙ тик. Она может
        // доворачиваться на месте, сменить ногу плана или продвинуть индекс
        // длинного пути. Всё это наблюдаемый прогресс, поэтому окно неподвижности
        // начинается заново. Раньше сторож помнил только Anchor: несколько
        // честных маршрутов подряд возле одной точки складывались в одно окно и
        // давали ложный PositionFrozen.
        if ((reason == ReasonFrozen && MadeMovementProgress(watch, npc)) ||
            (reason == ReasonIdle &&
             (Moved(watch.Anchor, npc.Position) ||
              watch.LastCompletedTick != npc.Execution.LastCompletedTick)))
        {
            StartWatch(world, npc, reason);
            return;
        }

        var held = world.Tick - watch.SinceTick;
        if (held < Threshold(reason))
        {
            return;
        }

        // PositionFrozen звучит только если она И ПРАВДА не сдвинулась: «идёт»
        // и «стоит на месте» вместе — это ловушка §102, а «идёт и движется» —
        // просто долгая дорога.
        if (reason == ReasonFrozen && Moved(watch.Anchor, npc.Position))
        {
            _watch.Remove(id);
            return;
        }

        var firstTime = watch.LastEmitTick == 0;
        if (!firstTime && world.Tick - watch.LastEmitTick < AiBalance.StuckRepeatEmitTicks)
        {
            return;
        }

        watch.LastEmitTick = world.Tick;
        _watch[id] = watch;

        // Формат Key=Value — как у остальной новой диагностики: разбирается
        // глазами и грепом, и ничей парсер на него не завязан.
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "StuckDetected",
                $"Reason={reason} Ticks={held} Goal={npc.Mind.CurrentGoal} " +
                $"Exec={npc.Execution.Status} Moving={(npc.Movement.IsMoving ? 1 : 0)} " +
                $"Step={npc.Plan.CurrentStepIndex}/{npc.Plan.Steps.Count} " +
                $"Plan={npc.Plan.Status} Pos={Trace.FormatPos(npc.Position)} " +
                $"Junction={Trace.FormatJunction(npc.CurrentJunction)} " +
                $"Target={Trace.FormatJunction(npc.Plan.TargetJunctionId)} " +
                $"{(firstTime ? "ONSET" : "STILL")}");
        }
    }

    private const string ReasonIdle = "IdleWithGoal";
    private const string ReasonStep = "StepOverrun";
    private const string ReasonGoalless = "GoallessCrisis";
    private const string ReasonFrozen = "PositionFrozen";

    private static string Diagnose(WorldState world, NPCState npc)
    {
        var goal = npc.Mind.CurrentGoal;

        if (goal == GoalType.None)
        {
            // Ничего не хочет, пока нужда кричит: аукцион не рождает НИЧЕГО.
            // Это тот самый «Goal=None ×362 циклов» из §63.
            return InCrisis(npc) ? ReasonGoalless : null;
        }

        // Idle is the authored decision to stand still. A completed Idle plan
        // has exactly the same mechanical shape as a dead plan (no execution,
        // no movement), but it is the fallback doing its job, not a hang.
        if (goal == GoalType.Idle)
        {
            return null;
        }

        // Перерасход меряется от СОБСТВЕННОГО конца взаимодействия, а не общим
        // числом тиков: честные длительности разнятся на порядки (замах — тики,
        // сон — тысячи), и любой единый потолок либо проспит первое, либо
        // оболжёт второе. А вот «просрочило свой же EndTick» ошибкой быть не
        // может.
        //
        // Здесь только УСЛОВИЕ, без запаса: «сколько терпеть» решает выдержка
        // ниже, одна на все причины. Запас, продублированный в обоих местах,
        // складывался — до жалобы проходило вдвое больше, чем сказано в ручке,
        // и это ловилось только тестом.
        //
        // Проверяется ПЕРВОЙ: занятая делом не «идёт», и порядок ниже иначе
        // закрыл бы этой ветке дорогу.
        if (npc.Execution.Status == ExecutionStatus.InProgress &&
            npc.Execution.EndTick > 0 &&
            world.Tick > npc.Execution.EndTick)
        {
            return ReasonStep;
        }

        // ⭐ Подпись §102: цель есть, взаимодействие не идёт, и она никуда не
        // идёт. Три «нет» разом — состояние, из которого сама она не выйдет.
        if (npc.Execution.Status == ExecutionStatus.None && !npc.Movement.IsMoving)
        {
            return ReasonIdle;
        }

        // Говорит, что идёт. Само по себе это норма — потому подозрение и
        // проверяется через StuckFrozenTicks замером «сдвинулась ли», а не
        // сразу жалобой.
        if (npc.Movement.IsMoving)
        {
            return ReasonFrozen;
        }

        return null;
    }

    /// <summary>
    /// §30.16: the exact sample counted by the soak's stuck-time metric. This
    /// is public so diagnostics and the headless runner cannot grow two subtly
    /// different definitions again. Authored stillness, manual control,
    /// reactive holds and the deliberate Idle goal are never a broken stall.
    /// </summary>
    public static bool CountsAsIdleWithGoal(WorldState world, NPCState npc) =>
        !WatchdogExclusions.IsPlayerDriven(npc) &&
        !WatchdogExclusions.IsAuthoredStillness(world, npc) &&
        !WatchdogExclusions.IsIntentionalHold(world, npc) &&
        npc.Mind.CurrentGoal is not (GoalType.None or GoalType.Idle) &&
        npc.Execution.Status == ExecutionStatus.None &&
        !npc.Movement.IsMoving &&
        (npc.Execution.LastCompletedTick < 0 ||
         world.Tick - npc.Execution.LastCompletedTick >= AiBalance.StuckIdleTicks);

    private static int Threshold(string reason)
    {
        if (reason == ReasonIdle)
        {
            return AiBalance.StuckIdleTicks;
        }

        if (reason == ReasonGoalless)
        {
            return AiBalance.StuckGoallessTicks;
        }

        if (reason == ReasonFrozen)
        {
            return AiBalance.StuckFrozenTicks;
        }

        return AiBalance.StuckStepTicks;
    }

    /// <summary>Сдвинулась ли заметно. Порог — четверть гекса: меньше это дрожь
    /// поворота и шага на месте, а не дорога.</summary>
    private static bool Moved(Float2 from, Float2 to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var limit = HexSpatialMath.HexRadius * 0.25f;
        return dx * dx + dy * dy > limit * limit;
    }

    private void StartWatch(WorldState world, NPCState npc, string reason)
    {
        _watch[npc.Id.Value] = new Watch
        {
            Reason = reason,
            Goal = npc.Mind.CurrentGoal,
            SinceTick = world.Tick,
            LastEmitTick = 0,
            Anchor = npc.Position,
            RotationDegrees = npc.RotationDegrees,
            MovementPathIndex = npc.Movement.PathIndex,
            PlanStepIndex = npc.Plan.CurrentStepIndex,
            TargetJunction = npc.Plan.TargetJunctionId,
            LastCompletedTick = npc.Execution.LastCompletedTick,
        };
    }

    private static bool MadeMovementProgress(Watch watch, NPCState npc) =>
        Moved(watch.Anchor, npc.Position) ||
        MathUtil.Abs(MathUtil.DeltaAngle(watch.RotationDegrees, npc.RotationDegrees)) > 0.5f ||
        watch.MovementPathIndex != npc.Movement.PathIndex ||
        watch.PlanStepIndex != npc.Plan.CurrentStepIndex ||
        watch.TargetJunction != npc.Plan.TargetJunctionId;

    // §122: правило «стоит по сюжету» переехало в WatchdogExclusions — сторожей
    // стало два, и одинаковое условие в двух файлах правилось бы в одном.

    internal static bool InCrisis(NPCState npc) =>
        npc.Needs.Hunger >= SimBalance.StarvingEnterThreshold ||
        npc.Needs.Thirst >= SimBalance.StarvingEnterThreshold ||
        npc.Needs.Energy <= 1f - SimBalance.StarvingEnterThreshold;
}

}
