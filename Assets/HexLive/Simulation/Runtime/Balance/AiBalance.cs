namespace HexLive.Simulation.Runtime
{

// Cognition knobs that used to be private consts inside Perception/Decision:
// how far she sees, how long spatial memory lives, and the goal-lock
// hysteresis that keeps the auction from thrashing. Static (like SimBalance)
// so the CharacterBalance config asset can push tuned values at boot; the
// systems read them through `=> AiBalance.X` shims at the old const names.
public static class AiBalance
{
    public static int PerceptionRadiusTiles = 3;
    // How long a seen object / danger mark lingers in memory. 2400 ticks =
    // 10 real minutes; MeatRawSpoilTicks (12000, §54.17) comfortably
    // outlives it. Plain ticks, so it does NOT follow the visual clock.
    public static int MemoryTtlTicks = 2400;

    // §27.18A r2: потолок НЕПОСТОЯННОЙ памяти об объектах (лагерные записи
    // IsPermanent в него не считаются и не вытесняются). При превышении
    // забываются самые давние по LastSeenTick. Это и есть гарантия, что цена
    // тика ИИ не растёт с размером острова: Perception.Objects — это память.
    public static int MaxKnownObjects = 256;

    // Spec 23.16/35.4: a freshly won goal is locked this long; only a
    // clearly better bid (LockOverrideDelta) may break the lock, and after
    // it expires a challenger still needs SwitchDelta of margin.
    public static int GoalLockTicks = 24;
    public static float LockOverrideDelta = 0.5f;
    public static float SwitchDelta = 0.15f;

    // §28.15: how long the invited listener waits for the talk to start.
    public static int TalkWaitTimeoutTicks = 120;

    // ── §50.9: «спуститься, пока ноги держат» ────────────────────────────
    // Порог прыжка — 0.75 на КАЖДУЮ ногу (NpcState.LegSupportsJump), то есть
    // пара укусов превращает возвышенность в ловушку: мир без прыжка — это
    // ~половина узлов острова, а уступ может быть и тремя узлами (seed 987654:
    // Ines умерла от жажды на компоненте из 3 узлов с бинтами в рюкзаке).
    // Инстинкт: нога уже потрёпана, а плоская компонента подо мной крошечная —
    // уходить на большую землю, ПОКА прыжок ещё возможен.
    public static bool SafeGroundRetreatEnabled = true;

    // Нога «потрёпана», когда её функция ниже этого (0.75 — сам порог прыжка;
    // 0.9 даёт запас в одну-две раны на реакцию, пока спуск ещё возможен).
    public static float SafeGroundLegAlert = 0.9f;

    // Компонента МЕНЬШЕ этого числа узлов считается уступом-ловушкой. Материк
    // после честного графа — тысячи узлов; терраса горы — десятки.
    public static int SafeGroundIsletMaxJunctions = 96;

    // spec §30.15 — when to say out loud "this one is not getting anywhere".
    //
    // These live HERE, in an already-registered balance class, on purpose: a NEW
    // static class needs a row in BalanceReflection.BalanceClasses, and without
    // it the knobs still mirror into the asset and still pass the coverage gate
    // while silently never being exported or applied (see the Spec81 scar
    // comment there). Cheaper to join a registered bag than to remember.

    /// <summary>Goal set, no interaction running, not moving — the §102
    /// signature. 40 ticks is ten seconds of world time: long enough that a
    /// normal hand-off between plan and path never trips it.</summary>
    public static int StuckIdleTicks = 40;

    /// <summary>One plan step held for this long. Generous: the longest honest
    /// interactions (building, sleeping) run for hundreds of ticks.</summary>
    public static int StuckStepTicks = 600;

    /// <summary>No goal at all while some need is past its crisis line — she is
    /// not idle by choice, the auction is failing to produce anything.</summary>
    public static int StuckGoallessTicks = 120;

    /// <summary>Says it is walking, but has not actually moved. The pursue-loop
    /// signature: a path that is rebuilt every tick and never advances.</summary>
    public static int StuckFrozenTicks = 60;

    /// <summary>While a stall persists, repeat the complaint no more often than
    /// this. Onset is always reported; the repeat is what makes a long freeze
    /// visible in a trace tail without drowning it.</summary>
    public static int StuckRepeatEmitTicks = 200;

    // ── spec §122 — ПЕТЛЯ (лайвлок), а не застой ─────────────────────────
    //
    // Ручки стоят ЗДЕСЬ, рядом со Stuck*, по той же причине, что описана выше:
    // новый статический класс требует строки в BalanceReflection.BalanceClasses,
    // и без неё ручки зеркалятся в ассет, проходят гейт покрытия и молча ничего
    // не делают. Дешевле присоединиться к зарегистрированному мешку.
    //
    // ⚠️ Разница со Stuck* принципиальная и её легко потерять при тюнинге:
    // застой меряется ВРЕМЕНЕМ БЕЗДЕЙСТВИЯ, петля — ЧИСЛОМ ПОПЫТОК БЕЗ
    // ЗАВЕРШЕНИЙ. В петле NPC деятельна, и никакой порог «сколько стоит» её не
    // поймает.

    /// <summary>Окно, внутри которого считаются попытки. 1200 тиков = полдня
    /// игрового цикла событий (EventCycleTicks 2400): достаточно длинное, чтобы
    /// вместить неспешную честную работу с походами, и достаточно короткое,
    /// чтобы вчерашняя неудача не тянулась в сегодняшний счёт.</summary>
    public static int LoopWindowTicks = 1200;

    // Bug #326: «тихое» зависание — план Active, исполнитель пуст, тело стоит
    // столько тиков подряд (400 = 100 реальных секунд на 4 Гц). Дальше план
    // сносится, и лестница петель работает как по обычному провалу.
    public static int LoopStalledTicks = 400;

    /// <summary>Sisyphus: столько раз взялась за ОДИН И ТОТ ЖЕ прицел (цель +
    /// объект) внутри окна, не доведя ни разу. Четыре — уже бюджет ретраев
    /// маршрута (PathFailureRetryAttempts), пятая попытка это разговор о другом:
    /// не «дорога временно занята», а «сюда ходить бессмысленно».</summary>
    public static int LoopRepeatAttempts = 5;

    /// <summary>Oscillation: столько раз содержательная цель сменилась на другую
    /// содержательную и обратно (A→B→A считается за два), ни одна не завершена.
    /// Шесть — три полных качания.</summary>
    public static int LoopOscillations = 6;

    /// <summary>NeedStarved: столько тиков нужда держится выше кризисной черты,
    /// пока обслуживающая её цель берётся и не доводится. Полный игровой цикл
    /// событий (2400): за это время честная цепочка «дойти-взять-съесть»
    /// успевает отработать много раз.</summary>
    public static int LoopNeedStarvedTicks = 2400;

    /// <summary>Пока петля держится, повторять жалобу не чаще этого. Втрое реже
    /// StuckRepeatEmitTicks: петля по природе длиннее застоя.</summary>
    public static int LoopRepeatEmitTicks = 600;

    // ── §122 фаза 2: лестница выхода ─────────────────────────────────────

    /// <summary>
    /// Выключатель автовыхода. Диагностика (<c>LoopDetected</c>) работает всегда;
    /// этот флаг гасит только ДЕЙСТВИЯ.
    /// <para>
    /// Ручка заведена не «на всякий случай», а как инструмент замера: §35.6
    /// помнит, как вариант abort-on-Blocked выглядел логично и дал 466 пустых
    /// отмен стирки с тройным churn. Единственный способ отличить лечение от
    /// такого — прогнать ОДИН И ТОТ ЖЕ сид с флагом и без.
    /// </para>
    /// </summary>
    public static bool LoopEscapeEnabled = true;

    /// <summary>
    /// Докуда лестнице позволено подниматься: 0 — только доклад, 1 — отворот,
    /// 2 — долгий отворот, 3 — глушение цели.
    /// <para>
    /// Потолок заведён замером, а не осторожностью. Первый A/B фазы 2 показал,
    /// что лестница целиком срезает время в петлях на 46%, но неоднородно: на
    /// сидах с тяжёлыми кругами −95%, а на почти здоровых — рост в 5-7 раз.
    /// Отличать «какая ступень лечит» от «какая вредит» усреднением нельзя,
    /// поэтому у ступеней есть потолок, и он подобран прогоном.
    /// </para>
    /// </summary>
    public static int LoopMaxRung = 3;

    /// <summary>
    /// Вторая ступень: тот же <c>Memory.Shun</c>, но надолго. Пять обычных
    /// отворотов (<c>ShunTicks</c> 600) — этот объект уже пережил, значит
    /// шестисот тиков ему мало.
    /// </summary>
    public static int LoopHardShunTicks = 3000;

    /// <summary>
    /// Третья ступень: цель уходит на кулдаун целиком, чтобы аукцион выдал
    /// второе по счёту дело. Заметно длиннее <c>FailureCooldownTicks</c> (40):
    /// сорока тиков петля не замечает — она их и так переживала пять раз подряд.
    /// </summary>
    public static int LoopGoalCooldownTicks = 900;

    // ── Длительности, которые были продублированы числом ─────────────────
    //
    // Каждая из них стояла ГОЛЫМ ЛИТЕРАЛОМ в двух-трёх местах сразу. Такое
    // число нельзя «подкрутить»: правка одного места из трёх не ломает сборку
    // и не роняет тест — она просто заводит два разных правила там, где было
    // одно, и расхождение всплывает месяцами позже как необъяснимое поведение.
    // Ровно так уже разъезжались фильтры кокоса и мерки дистанции.

    /// <summary>
    /// Насколько NPC «отворачивается» от источника, который по прибытии
    /// оказался занят: чтобы не топтаться вокруг него, а поискать другой.
    /// </summary>
    public static int ShunTicks = 600;

    /// <summary>Посидев, не садится снова столько тиков (§35.4 анти-дребезг).</summary>
    public static int SitCooldownTicks = 240;

    /// <summary>Одевшись, не переодевается столько тиков.</summary>
    public static int DressCooldownTicks = 160;

    /// <summary>
    /// Spec 41.5: только что проснулась — стоит и приходит в себя, цели ждут.
    /// Длительность выведена из клипа вставания (баг #1: меньше — сим уже
    /// везёт тело, пока клип доигрывает, и ноги скользят по земле):
    /// GetUp = «Situp To Idle.fbx», 132 кадра @ 30 fps = 4.40 с = 17.6 тика
    /// (тик 0.25 с) → 18. Цепочка падения короче (StandUp = «X Bot@Standing
    /// Up», 50 кадров = 1.67 с) и покрыта тем же числом.
    /// </summary>
    public static int WakeGraceTicks = 18;

    /// <summary>
    /// Цель, чей план не удалось построить, отдыхает столько тиков. Тот же
    /// срок применяется к завершённому уходу за собой (мытьё, стирка), поэтому
    /// он и стоит здесь, а не приватной константой планировщика.
    /// </summary>
    public static int FailureCooldownTicks = 40;

    /// <summary>
    /// A route may be temporarily sealed by another actor, so one empty search
    /// is not final. After this many consecutive empty searches for the same
    /// junction the plan must fail and release its target instead of running a
    /// full graph search every fast tick forever. This is an invariant-sized
    /// retry budget, not a balance dial.
    /// </summary>
    public const int PathFailureRetryAttempts = 4;

    // ── Пороги аукциона, стоявшие одним и тем же числом в разных смыслах ──
    //
    // Литерал 0.35f встречался в DecisionSystem семнадцать раз и означал ПЯТЬ
    // разных вещей: порог жажды, порог холода, тягу к стройке, базовую ставку
    // пошива и ещё пару разовых. Тюнить такое нельзя: ищущий «порог жажды»
    // находит семнадцать совпадений и правит не то. Разъехавшиеся смыслы,
    // спрятанные за одинаковым числом, — та же болезнь, что у мерок дистанции.

    /// <summary>
    /// Жажда, выше которой она идёт пить (и готова идти ЗА водой).
    /// Сестра <c>SimBalance.GetFoodHungerThreshold</c>, которая уже была ручкой,
    /// пока порог жажды оставался голым числом в трёх местах.
    /// </summary>
    public static float DrinkThirstThreshold = 0.35f;

    /// <summary>
    /// Тепловой комфорт, ниже которого считается, что она мёрзнет: отмечает
    /// «замерзала недавно» и открывает розжиг трением.
    /// </summary>
    public static float FreezingComfortThreshold = -0.35f;

    /// <summary>
    /// Насколько недостающий материал тянет к стройплощадке. Одно число на все
    /// четыре материала (листья, палки, брёвна, верёвка) — они и должны тянуть
    /// одинаково, иначе бригада перекашивается на один вид добычи (§64.9: у
    /// брёвен тяги не было вовсе, и четыре сида простояли на 0/4 ~22 000 тиков).
    /// </summary>
    public static float BuildSiteMaterialPull = 0.35f;

    /// <summary>
    /// §140.1: здоровье, ниже которого прогулка (Explore) больше не
    /// предлагается. Ползущая не гуляет ни при каком здоровье — это отдельное
    /// условие; здесь порог для стоящей на ногах, но потрёпанной.
    /// </summary>
    public static float ExploreHealthFloor = 0.6f;

    /// <summary>
    /// §140.2: здоровье, ниже которого «домой» доступно само по себе, без
    /// нужды. Ниже порога прогулки: сначала перестаём отпускать гулять, и
    /// только потом начинаем гнать домой — иначе цель зажигалась бы у каждой
    /// царапины.
    /// </summary>
    public static float HomewardHealthFloor = 0.5f;

    /// <summary>
    /// §140.2: жажда/голод, с которых «домой» открывается по НУЖДЕ — но только
    /// когда аукцион не выставил ни одного предложения по этой нужде. Порог
    /// смертельный, а не «проголодалась»: пока еда или вода достижимы, идти
    /// надо к ним, а не к очагу.
    /// </summary>
    public static float HomewardNeedThreshold = 0.75f;

    /// <summary>
    /// §140.2: постоянный вес «домой» для разбитого тела. 0.55 ставит его выше
    /// быта и прогулок, но ниже жажды/голода на смертельном уровне — те бьют
    /// своим собственным значением.
    /// </summary>
    public static float HomewardBrokenBodyUrgency = 0.55f;

    /// <summary>
    /// §143: постоянная тяга к ДОСТИЖИМОМУ режущему, когда своего нет. Клинок
    /// на этом острове не «ещё один инструмент»: без него не вскрыть кокос, то
    /// есть нет ни воды, ни еды. 0.35 ставит поход за ножом вровень с бытовыми
    /// делами (GatherWood 0.2, GatherStone 0.28, крафты 0.28-0.34), но НИЖЕ
    /// настоящей нужды — жажда 0.9 и голод по-прежнему бьют его своим весом.
    /// Аварийная надбавка остаётся сверху: она про «уже умираю», эта — про
    /// «не доводи до этого». 0 выключает.
    /// </summary>
    public static float BladePickupPull = 0.35f;
}

}
