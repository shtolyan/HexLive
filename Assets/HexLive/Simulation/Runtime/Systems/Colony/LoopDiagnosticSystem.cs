using System.Collections.Generic;
using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// Spec §122: «эта бегает по кругу» — сказанное вслух.
///
/// <para>
/// ⭐ Почему <see cref="StuckDiagnosticSystem"/> этого не ловит. Тот сторож ищет
/// ДЕДЛОК: четыре формы «стоит» — цель есть и не двигается, шаг просрочен, цели
/// нет в кризис, «иду», но с места не сдвинулась. А NPC в петле — ЛАЙВЛОК: цель
/// есть, план строится, ноги идут, позиция меняется, свой <c>EndTick</c> не
/// просрочен. Она проходит все четыре проверки насквозь. Хуже: <c>PositionFrozen</c>
/// специально сбрасывает своё окно при смене шага плана и цели маршрута — то
/// есть петля его ГАСИТ. Сторож смотрит прямо на неё и молчит по построению.
/// </para>
/// <para>
/// Отличие мерки принципиальное: застой меряется ВРЕМЕНЕМ БЕЗДЕЙСТВИЯ, петля —
/// ЧИСЛОМ ПОПЫТОК БЕЗ ЗАВЕРШЕНИЙ. Второе взять было неоткуда, пока не появился
/// <see cref="IntentLedger"/>: <c>PlanInterruption.Abort</c> (96 вызовов из 26
/// файлов) аккуратно убирает за собой и не записывает о провале НИЧЕГО, так что
/// «двадцать провалов подряд» просто не существовало как факт.
/// </para>
/// <para>
/// Три подписи, потому что им нужны РАЗНЫЕ ответы: у <c>Sisyphus</c> виноват
/// объект (баги #67, #78, #91), у <c>Oscillation</c> — пара целей (#90, #51), у
/// <c>NeedStarved</c> — вся ветка снабжения (#88).
/// </para>
/// <para>
/// ⚠️ <b>Фаза 1: система ничего не меняет.</b> Только читает состояние, ведёт
/// реестр и эмитит. Реестр и наблюдение живут ВНЕ мира (<c>WorldState.IntentLedger</c>
/// не сериализуется и не едет в снапшоте), поэтому golden-трасса обязана
/// остаться байт-в-байт той же. Лестница выхода — фаза 2, и она войдёт сюда же,
/// уже под <see cref="LoopPolicy"/> из каталога целей.
/// </para>
/// <para>
/// Слой <b>Fast</b>, в отличие от §30.15. Не по важности, а по необходимости:
/// Slow идёт раз в 16 тиков, а цель успевает быть выбранной и брошенной за
/// четыре. Реестр обязан видеть КАЖДЫЙ переход, иначе завершение, случившееся
/// между двумя замерами, потеряется — а на счёте завершений держится всё.
/// Дорогая часть (разбор подписей) при этом идёт по своему таймеру.
/// Регистрируется последней, поэтому среди Fast-систем видит тик уже устоявшимся.
/// </para>
/// </summary>
public sealed class LoopDiagnosticSystem : ISimulationSystem
{
    public string Name => nameof(LoopDiagnosticSystem);

    public TickLayer Layer => TickLayer.Fast;

    public const string ReasonSisyphus = "Sisyphus";
    public const string ReasonOscillation = "Oscillation";
    public const string ReasonNeedStarved = "NeedStarved";

    /// <summary>Как часто разбирать подписи. Сам замер идёт каждый тик, а разбор
    /// кольца — нет: он O(32) на NPC и ничего не теряет от того, что случится на
    /// четыре тика позже. Совпадает с Medium-интервалом не случайно — чаще, чем
    /// принимаются решения, смотреть незачем.</summary>
    private const int ScanEveryTicks = 4;

    /// <summary>Что мы видели у этой NPC в прошлый тик.</summary>
    private struct Watch
    {
        public GoalType PlanGoal;
        public ObjectId? Target;
        public EntityId? Agent;
        public PlanStatus Status;
        public bool Seen;

        /// <summary>С какого тика нужда держится выше кризисной черты. 0 — не держится.</summary>
        public int HungerCrisisSince;
        public int ThirstCrisisSince;
        public int EnergyCrisisSince;

        /// <summary>Что уже доложено и когда, чтобы не повторяться каждый разбор.</summary>
        public string ReportedReason;
        public int LastEmitTick;

        /// <summary>§122 фаза 2: на какой ступени лестницы этот круг. 0 — только
        /// доложено, ничего не сделано.</summary>
        public int Rung;

        /// <summary>Тик последнего ДЕЙСТВИЯ лестницы. Обрезает окно счёта, чтобы
        /// попытки «до лечения» не толкали следующую ступень.</summary>
        public int LastActionTick;
    }

    private readonly Dictionary<int, Watch> _watch = new Dictionary<int, Watch>();
    private readonly List<int> _gone = new List<int>();

    public void Run(WorldState world)
    {
        var scan = world.Tick % ScanEveryTicks == 0;

        foreach (var npc in world.Entities.Npcs.Values)
        {
            Sample(world, npc);

            if (scan)
            {
                Examine(world, npc);
            }
        }

        // Мёртвые и ушедшие не должны копиться в словарях живого процесса.
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
            world.IntentLedger.Forget(id);
        }
    }

    // ── Замер: ведём реестр намерений ────────────────────────────────────

    /// <summary>
    /// Переводит состояние в записи реестра.
    ///
    /// <para>
    /// Намерение открывается не когда ВЫБРАНА цель, а когда под неё ПОСТРОЕН
    /// план (<c>PlanStatus.Active</c>). Разница существенная: между выбором и
    /// планом цель ещё ни на что не нацелена, и открытая там запись получила бы
    /// пустой прицел, а потом сменила бы его на настоящий — одна честная попытка
    /// посчиталась бы за две. Момент «план активен» — это ровно то, что трасса
    /// зовёт <c>PlanStarted</c>.
    /// </para>
    /// </summary>
    private void Sample(WorldState world, NPCState npc)
    {
        var id = npc.Id.Value;
        _watch.TryGetValue(id, out var watch);

        var status = npc.Plan.Status;
        var goal = npc.Plan.Goal;
        var target = npc.Plan.TargetObjectId;
        var agent = npc.Plan.TargetAgentId;
        var counts = GoalCatalog.LoopFor(goal) != LoopPolicy.Ambient;

        if (!watch.Seen)
        {
            // Первый взгляд на эту NPC: ничего не закрываем, только запоминаем.
            watch.Seen = true;
        }
        else if (status == PlanStatus.Active && counts)
        {
            var wasActive = watch.Status == PlanStatus.Active &&
                GoalCatalog.LoopFor(watch.PlanGoal) != LoopPolicy.Ambient;

            if (!wasActive)
            {
                // План только что ожил — новая попытка.
                world.IntentLedger.Open(id, world.Tick, goal, target, agent);
            }
            else if (watch.PlanGoal != goal)
            {
                world.IntentLedger.Close(id, world.Tick, IntentOutcome.Abandoned);
                world.IntentLedger.Open(id, world.Tick, goal, target, agent);
            }
            else if (watch.Target?.Value != target?.Value ||
                     watch.Agent?.Value != agent?.Value)
            {
                // Та же цель, другой прицел — «пошла к другому кокосу».
                world.IntentLedger.Retarget(id, world.Tick, goal, target, agent);
            }
        }
        else if (watch.Status == PlanStatus.Active)
        {
            // План перестал быть активным — вот и исход попытки.
            world.IntentLedger.Close(id, world.Tick, OutcomeOf(status));
        }

        watch.PlanGoal = goal;
        watch.Target = target;
        watch.Agent = agent;
        watch.Status = status;

        UpdateCrisisClocks(world, npc, ref watch);
        _watch[id] = watch;
    }

    /// <summary>
    /// Исход берётся из статуса плана, а не из того, ПОЧЕМУ мы закрываем запись:
    /// <c>PlanInterruption.Abort</c> ставит <c>Invalid</c>, планировщик —
    /// <c>Failed</c>, а исполнитель на успехе — <c>Completed</c>. Всё остальное
    /// (в том числе смена цели поверх живого плана) — «бросила».
    /// </summary>
    private static IntentOutcome OutcomeOf(PlanStatus status) => status switch
    {
        PlanStatus.Completed => IntentOutcome.Completed,
        PlanStatus.Failed => IntentOutcome.Failed,
        PlanStatus.Invalid => IntentOutcome.Failed,
        _ => IntentOutcome.Abandoned,
    };

    private static void UpdateCrisisClocks(WorldState world, NPCState npc, ref Watch watch)
    {
        Tick(npc.Needs.Hunger >= SimBalance.StarvingEnterThreshold,
            world.Tick, ref watch.HungerCrisisSince);
        Tick(npc.Needs.Thirst >= SimBalance.StarvingEnterThreshold,
            world.Tick, ref watch.ThirstCrisisSince);
        Tick(npc.Needs.Energy <= 1f - SimBalance.StarvingEnterThreshold,
            world.Tick, ref watch.EnergyCrisisSince);

        static void Tick(bool inCrisis, int tick, ref int since)
        {
            if (!inCrisis)
            {
                since = 0;
            }
            else if (since == 0)
            {
                // 1-based: тик 0 — валидный старт, а 0 значит «не держится».
                since = tick + 1;
            }
        }
    }

    // ── Разбор: три подписи ──────────────────────────────────────────────

    private void Examine(WorldState world, NPCState npc)
    {
        var id = npc.Id.Value;

        if (WatchdogExclusions.IsAuthoredStillness(world, npc) ||
            WatchdogExclusions.IsPlayerDriven(npc))
        {
            world.IntentLedger.ClearLooping(id);
            Forget(id, world.Tick);
            return;
        }

        _watch.TryGetValue(id, out var seen);

        // ⚠️ Окно счёта не заглядывает раньше последнего ДЕЙСТВИЯ лестницы.
        // Без этого попытки, ради которых ступень и была сделана, продолжают
        // считаться после неё, порог остаётся пробитым, и следующая ступень
        // срабатывает не потому, что лечение не помогло, а потому, что мы
        // помним болезнь. Лестница пробегала бы до конца за три разбора.
        var since = world.Tick - AiBalance.LoopWindowTicks;
        if (seen.LastActionTick > since)
        {
            since = seen.LastActionTick;
        }

        var records = world.IntentLedger.Tail(id);

        var offender = GoalType.None;
        var reason = DiagnoseSisyphus(world, npc, since, out var detail, ref offender) ??
                     DiagnoseOscillation(records, since, out detail, ref offender) ??
                     DiagnoseNeedStarved(world, npc, since, out detail, ref offender);

        if (reason == null)
        {
            world.IntentLedger.ClearLooping(id);
            Forget(id, world.Tick);
            return;
        }

        // Метка «крутится сейчас» ставится ДО глушилки повторов: доля времени в
        // петлях не должна зависеть от того, как часто мы об этом говорим.
        world.IntentLedger.MarkLooping(id, world.Tick);

        var watch = seen;
        var firstTime = watch.ReportedReason != reason;
        if (!firstTime && world.Tick - watch.LastEmitTick < AiBalance.LoopRepeatEmitTicks)
        {
            return;
        }

        // ⚠️ Ступень НЕ сбрасывается просто потому, что круг доложен заново.
        //
        // Первая версия обнуляла её на каждом ONSET — и лестница физически не
        // могла подняться выше первой ступени. Механика такая: ступень
        // отрабатывает, окно счёта обрезается по LastActionTick, улик временно
        // не хватает, подпись пропадает, Forget через LoopRepeatEmitTicks стирает
        // память — и следующее срабатывание приходит как ONSET, с нуля. NPC
        // вечно получала бы отворот от объекта и никогда — глушение цели, то
        // есть ровно ту ступень, ради которой лестница и заведена.
        //
        // Ступень считается СРАБОТАВШЕЙ, если целое окно не понадобилось
        // действовать. Это и есть проверка «лечение помогло», а не «жалоба
        // на секунду замолчала».
        if (watch.LastActionTick != 0 &&
            world.Tick - watch.LastActionTick > AiBalance.LoopWindowTicks)
        {
            watch.Rung = 0;
            watch.LastActionTick = 0;
        }

        watch.ReportedReason = reason;
        watch.LastEmitTick = world.Tick;
        _watch[id] = watch;

        // §122: политика цели попадает в СООБЩЕНИЕ, а не только в решение
        // лестницы. Читающий трассу должен видеть, что липкая сцена не была
        // разорвана НАМЕРЕННО (§81.14), а не гадать, почему сторож смолчал.
        //
        // ⚠️ Берётся у ЗАКРУТИВШЕЙСЯ цели, а не у текущей. Первый прогон печатал
        // «Aim=GatherTools … Policy=Ambient», потому что к моменту разбора NPC
        // успевала уйти в Idle: политика в сообщении описывала не ту цель, о
        // которой сообщение, и читалась как «сторож сам себе противоречит».
        var policy = GoalCatalog.LoopFor(offender);

        // Формат Key=Value — как у StuckDetected: разбирается глазами и грепом,
        // и ничей парсер на него не завязан.
        Trace.Emit(world, npc.Id, "LoopDetected",
            $"Reason={reason} {detail} Goal={npc.Mind.CurrentGoal} Policy={policy} " +
            $"Exec={npc.Execution.Status} Plan={npc.Plan.Status} " +
            $"Moving={(npc.Movement.IsMoving ? 1 : 0)} " +
            $"Pos={Trace.FormatPos(npc.Position)} " +
            $"{(firstTime ? "ONSET" : "STILL")}");

        Escalate(world, npc, reason, offender, policy);
    }

    // ── Лестница выхода (§122 фаза 2) ────────────────────────────────────

    /// <summary>
    /// Одна лестница вместо N точечных заплаток.
    ///
    /// <para>
    /// Ступени идут снизу вверх и переключаются только когда предыдущая НЕ
    /// ПОМОГЛА — то есть круг пережил её и был замечен снова. Порядок такой,
    /// потому что цена ошибки растёт: отвернуться от одного объекта почти
    /// бесплатно, а заглушить цель целиком — это отнять у колонистки способность
    /// на четверть игрового дня.
    /// </para>
    /// <para>
    /// ⚠️ Обе нижние ступени — это ОДИН И ТОТ ЖЕ <c>Memory.Shun</c>, только с
    /// разным сроком, а не новая таблица «запрет пары (цель, объект)», как
    /// задумывалось на бумаге. Такая таблица потребовала бы 18 новых мест
    /// проверки — ровно столько раз проверяется <c>IsShunned</c>, — и забытое
    /// место не падает, а молча возвращает петлю. Именно так и появился баг #67:
    /// <c>GetWater</c> не проверял shun. Механизм, который УЖЕ проверяется
    /// везде, надёжнее точного, который надо не забыть подключить.
    /// </para>
    /// </summary>
    private void Escalate(WorldState world, NPCState npc, string reason,
        GoalType offender, LoopPolicy policy)
    {
        if (!AiBalance.LoopEscapeEnabled)
        {
            return;
        }

        // ⭐ §81.14/§109.6: липкую сцену рвать нельзя ничем и никогда. Доклад
        // уже прозвучал — на нём для неё лестница и кончается.
        if (policy != LoopPolicy.Normal)
        {
            return;
        }

        // ⭐ Предохранитель кризиса стоит на ВСЕЙ лестнице, а не только на
        // глушении цели.
        //
        // Первая версия охраняла лишь третью ступень — и замер это наказал.
        // Отворот от источника еды или воды выглядит безобидно («сходи к
        // другому кокосу»), но голодная колонистка, у которой отвернули
        // ближайший источник, пересекает кризисную черту, после чего срабатывает
        // NeedStarved — то есть лестница СОБСТВЕННЫМИ руками делает ту петлю,
        // которую диагностирует. На шести сидах ступень 1 в одиночку дала −3%
        // времени в петлях и худшую выживаемость из всех вариантов: 13 против
        // 16 без лестницы.
        //
        // Правило одно на все ступени: пока нужда кричит, вмешиваться в её
        // обслуживание нельзя ничем. Круг всё равно доложен — пусть его чинит
        // человек, а не сторож, отбирающий у голодной последний кокос.
        if (ServesCrisisNeed(npc, offender))
        {
            Trace.Emit(world, npc.Id, "LoopEscapeHeld",
                $"Reason={reason} Goal={offender} Why=ServesCrisisNeed");
            return;
        }

        var id = npc.Id.Value;
        _watch.TryGetValue(id, out var watch);
        var target = LoopTarget(world, npc, id);

        var next = watch.Rung + 1;
        if (next > AiBalance.LoopMaxRung)
        {
            return;
        }

        string action = null;

        if (next <= 2 && target is { } objectId)
        {
            // Ступени 1 и 2: отвернуться от объекта — сперва обычным сроком,
            // потом надолго. Пятикратно проваленный объект уже пережил обычный
            // отворот, значит шестисот тиков ему мало.
            var ticks = next == 1 ? AiBalance.ShunTicks : AiBalance.LoopHardShunTicks;
            npc.Memory.Shun(objectId, world.Tick + ticks);
            action = $"Shun=obj{objectId.Value} Ticks={ticks}";
        }
        else if (next <= 3)
        {
            // Ступень 3: заглушить саму цель, чтобы аукцион выдал второе по
            // счёту дело. Это ступень, ломающая качели: пока цель молчит,
            // соперница успевает ДОДЕЛАТЬ и разорвать чередование.
            // Предохранитель кризиса уже отработал выше — на всей лестнице.
            PlanningSystem.SetGoalCooldown(world, npc, offender,
                AiBalance.LoopGoalCooldownTicks);
            action = $"Mute={offender} Ticks={AiBalance.LoopGoalCooldownTicks}";
        }

        if (action == null)
        {
            // Выше третьей ступени фаза 2 не поднимается: принудительная чужая
            // цель и капитуляция с громким LoopUnresolved — фаза 3.
            return;
        }

        watch.Rung = next;
        watch.LastActionTick = world.Tick;
        _watch[id] = watch;

        Trace.Emit(world, npc.Id, "LoopEscalated",
            $"Reason={reason} Rung={next} {action} Goal={offender}");
    }

    /// <summary>
    /// На какой объект смотрит текущий круг. У качелей объекта нет — и это не
    /// пробел: там виноват не объект, а пара целей, и лечится он ступенью 3.
    /// </summary>
    private static ObjectId? LoopTarget(WorldState world, NPCState npc, int id) =>
        world.IntentLedger.TryGetLatestAim(id, out var aim) ? aim.Target : null;

    /// <summary>
    /// Обслуживает ли цель нужду, которая ПРЯМО СЕЙЧАС за кризисной чертой.
    /// <para>
    /// Единственный предохранитель третьей ступени, и он же — причина, по
    /// которой подпись <c>NeedStarved</c> не нуждается в собственном исключении:
    /// она по определению срабатывает во время кризиса, значит глушение её цели
    /// заблокировано здесь по построению, а не отдельным правилом, которое
    /// когда-нибудь забудут.
    /// </para>
    /// </summary>
    private static bool ServesCrisisNeed(NPCState npc, GoalType goal) => goal switch
    {
        GoalType.Eat or GoalType.GetFood =>
            npc.Needs.Hunger >= SimBalance.StarvingEnterThreshold,
        GoalType.Drink or GoalType.GetWater =>
            npc.Needs.Thirst >= SimBalance.StarvingEnterThreshold,
        GoalType.Sleep =>
            npc.Needs.Energy <= 1f - SimBalance.StarvingEnterThreshold,
        // Лечение — тоже жизнеобеспечение: заглушить его у истекающей кровью
        // значит повторить #88 намеренно. Вопрос задаётся ТОМУ ЖЕ правилу,
        // которым живёт аукцион, а не его копии.
        GoalType.TreatWounds or GoalType.GatherHerb or GoalType.CraftBandage =>
            DecisionSystem.IsBleedingCrisis(npc),
        _ => false,
    };

    /// <summary>
    /// ⭐ Сизиф: один и тот же прицел, взятый снова и снова, и ни одного
    /// завершения. Баг #67 (кокос), #78 (инструменты), #91 (взяла-положила).
    /// </summary>
    private static string DiagnoseSisyphus(WorldState world, NPCState npc, int since,
        out string detail, ref GoalType offender)
    {
        detail = string.Empty;
        if (!world.IntentLedger.TryGetLatestAim(npc.Id.Value, out var aim) ||
            GoalCatalog.LoopFor(aim.Goal) == LoopPolicy.Ambient)
        {
            return null;
        }

        // ⚠️ Сизифу нужен КОНКРЕТНЫЙ прицел. Цель без объекта и без человека
        // (помыться, поесть из рюкзака) даёт один и тот же «прицел» на все
        // заходы подряд, и подпись вырождается в «эта цель бралась пять раз» —
        // а это совсем другое утверждение и куда более слабое: воду могли
        // просто занять. Такие случаи ловит NeedStarved, и ловит по делу —
        // только когда нужда И ПРАВДА не удовлетворяется.
        if (aim.Target is null && aim.Agent is null)
        {
            return null;
        }

        world.IntentLedger.CountAim(npc.Id.Value, aim, since,
            out var attempts, out var completions);

        if (completions > 0 || attempts < AiBalance.LoopRepeatAttempts)
        {
            return null;
        }

        offender = aim.Goal;
        detail = $"Aim={aim.Goal}{aim.AimSuffix()} Attempts={attempts} Done=0";
        return ReasonSisyphus;
    }

    /// <summary>
    /// Качели: две содержательные цели сменяют друг друга, и ни одна не
    /// доводится. Баг #90 («то докопаться, то пить»), #51 (лут↔защита).
    /// <para>
    /// Считается по ДВУМ САМЫМ ЧАСТЫМ целям окна, а не по «ровно двум разным»:
    /// в живом мире между качаниями честно проскакивает третья цель, и жёсткое
    /// «ровно две» пропустило бы ровно те случаи, ради которых подпись заведена.
    /// </para>
    /// </summary>
    private static string DiagnoseOscillation(List<IntentRecord> records, int since,
        out string detail, ref GoalType offender)
    {
        detail = string.Empty;

        var counts = new Dictionary<GoalType, int>();
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (record.OpenedTick < since ||
                GoalCatalog.LoopFor(record.Goal) == LoopPolicy.Ambient)
            {
                continue;
            }

            counts.TryGetValue(record.Goal, out var seen);
            counts[record.Goal] = seen + 1;
        }

        if (counts.Count < 2)
        {
            return null;
        }

        // ⚠️ Детерминизм: порядок словаря не определён, поэтому при равном счёте
        // побеждает МЕНЬШИЙ ординал, а не тот, кто попался первым. Иначе
        // сообщение плавало бы от прогона к прогону при одинаковом поведении.
        var first = GoalType.None;
        var second = GoalType.None;
        var firstCount = 0;
        var secondCount = 0;
        foreach (var pair in counts)
        {
            if (Beats(pair.Key, pair.Value, first, firstCount))
            {
                second = first;
                secondCount = firstCount;
                first = pair.Key;
                firstCount = pair.Value;
            }
            else if (Beats(pair.Key, pair.Value, second, secondCount))
            {
                second = pair.Key;
                secondCount = pair.Value;
            }
        }

        if (secondCount == 0)
        {
            return null;
        }

        // Схлопываем подряд идущие одинаковые цели: «еда, еда, питьё» — это одно
        // качание, а не два. Считаем только переходы между этими двумя целями.
        var swings = 0;
        var previous = GoalType.None;
        var hasPrevious = false;
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (record.OpenedTick < since ||
                (record.Goal != first && record.Goal != second))
            {
                continue;
            }

            if (record.Outcome == IntentOutcome.Completed)
            {
                // Хоть одно доведённое дело — это работа, а не качели.
                return null;
            }

            if (hasPrevious && record.Goal != previous)
            {
                swings++;
            }

            previous = record.Goal;
            hasPrevious = true;
        }

        if (swings < AiBalance.LoopOscillations)
        {
            return null;
        }

        offender = first;
        detail = $"Goals={first},{second} Swings={swings} Done=0";
        return ReasonOscillation;

        static bool Beats(GoalType goal, int count, GoalType bestGoal, int bestCount) =>
            count > bestCount || (count == bestCount && count > 0 && goal < bestGoal);
    }

    /// <summary>
    /// Нужда кричит давно, а обслуживающая её цель берётся и не доводится. Баг
    /// #88: NPC так и не решила вопрос со здоровьем и умерла с планом в руках.
    /// <para>
    /// Отдельная подпись от Сизифа, потому что виноват не объект: кокосы могут
    /// каждый раз быть разные, и «отвернуться от этого» ничего не лечит.
    /// </para>
    /// </summary>
    private string DiagnoseNeedStarved(WorldState world, NPCState npc, int since,
        out string detail, ref GoalType offender)
    {
        detail = string.Empty;
        if (!_watch.TryGetValue(npc.Id.Value, out var watch))
        {
            return null;
        }

        if (Starved(world, npc, watch.HungerCrisisSince, "Hunger",
                GoalType.Eat, GoalType.GetFood, since, ref detail, ref offender) ||
            Starved(world, npc, watch.ThirstCrisisSince, "Thirst",
                GoalType.Drink, GoalType.GetWater, since, ref detail, ref offender) ||
            Starved(world, npc, watch.EnergyCrisisSince, "Energy",
                GoalType.Sleep, GoalType.Sleep, since, ref detail, ref offender))
        {
            return ReasonNeedStarved;
        }

        return null;
    }

    private static bool Starved(WorldState world, NPCState npc, int crisisSince,
        string need, GoalType direct, GoalType fetch, int since, ref string detail,
        ref GoalType offender)
    {
        if (crisisSince == 0 ||
            world.Tick - (crisisSince - 1) < AiBalance.LoopNeedStarvedTicks)
        {
            return false;
        }

        world.IntentLedger.CountGoal(npc.Id.Value, direct, since,
            out var attempts, out var completions);
        if (direct != fetch)
        {
            world.IntentLedger.CountGoal(npc.Id.Value, fetch, since,
                out var fetchAttempts, out var fetchCompletions);
            attempts += fetchAttempts;
            completions += fetchCompletions;
        }

        if (completions > 0 || attempts < AiBalance.LoopRepeatAttempts)
        {
            return false;
        }

        offender = direct;
        detail = $"Need={need} CrisisTicks={world.Tick - (crisisSince - 1)} " +
                 $"Attempts={attempts} Done=0";
        return true;
    }

    /// <summary>
    /// Забыть жалобу — но НЕ СРАЗУ.
    ///
    /// <para>
    /// ⚠️ Оплачено первым же прогоном на живом сиде 251173145. Петля по природе
    /// мигает: разбор идёт по своему таймеру и попадает то в момент, когда
    /// круг виден, то в промежуток между заходами, когда последний прицел уже
    /// другой. Сторож, стирающий память при первом же «сейчас не вижу», выдавал
    /// на ОДИН непрерывный круг девять отдельных ONSET за 200 тиков — то есть
    /// сам себе ломал и глушилку повторов, и счётчик «сколько петель было».
    /// </para>
    /// <para>
    /// Память держится ровно <c>LoopRepeatEmitTicks</c> после последней жалобы.
    /// Тогда ONSET снова значит «начался НОВЫЙ круг», а не «сторож моргнул».
    /// </para>
    /// </summary>
    private void Forget(int id, int tick)
    {
        if (!_watch.TryGetValue(id, out var watch) || watch.ReportedReason == null)
        {
            return;
        }

        if (tick - watch.LastEmitTick < AiBalance.LoopRepeatEmitTicks)
        {
            return;
        }

        watch.ReportedReason = null;
        watch.LastEmitTick = 0;
        _watch[id] = watch;
    }
}

}
