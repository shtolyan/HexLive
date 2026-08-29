using System.Collections.Generic;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.AI
{

/// <summary>
/// Persistent ownership phase for bathing's undress → body bath → redress
/// transaction (§40.6). LaundryBatch remains for old saves only.
/// Values are serialized explicitly; append only.
/// </summary>
public enum PersonalCarePhase
{
    None = 0,
    LaundryBatch = 1,
    Bathing = 2,
    Redress = 3
}

public sealed class NPCMind
{
    public GoalType CurrentGoal { get; set; } = GoalType.None;

    // Spec §64: the dream this NPC currently aspires to — the first entry in the
    // colony dream queue she hasn't personally fulfilled (campfire is fulfilled
    // for everyone at once; a bed is fulfilled per-NPC). Written each Slow tick
    // by DreamSystem, surfaced in the character panel. DERIVED display state —
    // recomputed on load, so it need not be serialized.
    public DreamType CurrentDream { get; set; } = DreamType.Campfire;

    // Emergency appraisal (spec 23.17): hysteresis 0.85 enter / 0.60 clear.
    public bool IsStarving { get; set; }

    // Spec 29E.1: same hysteresis pattern for water.
    public bool IsDehydrated { get; set; }

    // Spec 35.4: overheating latch — enter at CoolOffEnterThreshold, clear at
    // CoolOffClearThreshold. Gates CoolOff availability so it doesn't flicker
    // around the entry edge and re-win at zero margin every tick.
    public bool IsOverheated { get; set; }

    // §71: is she RUNNING this tick? Set by MovementSystem from the urgency
    // reasons (Defend / Flee / adrenaline / desperate for food or water) and
    // read by the view to pick the gait clip. Derived every tick, so it is not
    // serialized.
    public bool IsRunning { get; set; }

    // §71: the breath latch. Set when a run empties NPCNeeds.Breath, cleared
    // once it recovers to BreathReArm — the hysteresis that stops her
    // flickering between walk and run at the threshold. Derived, not serialized.
    public bool BreathSpent { get; set; }

    // Spec 35.4: how many times the in-place cool-off dwell has re-armed without
    // the goal actually clearing (safety cap against an infinite dwell on a
    // fallback tile that never cools). Reset when CoolOff is (re)selected.
    public int CoolRearmCount { get; set; }

    // §137: праздный отдых сидя. Оба поля ТРАНЗИЕНТНЫ и намеренно не пишутся в
    // сейв (как PlayDeadUntilTick): после загрузки она просто стоит и сядет
    // заново — это безобидно, а блоб трогать не приходится.
    /// <summary>Сколько раз такт отдыха перевзвёлся, не поднимая её на ноги.</summary>
    public int RestRearmCount { get; set; }

    /// <summary>Встала с отдыха — до этого тика снова не садится (§137).</summary>
    public int RestCooldownUntilTick { get; set; }

    // Spec 28.15C: mourning period and which bodies were already grieved for.
    public int GrievingUntilTick { get; set; }

    // Spec 40.13: knocked out — utterly spent stamina plus starvation or
    // blood loss drops the body; it lies unable to act until this tick, then
    // rises. 0 = conscious.
    public int FaintedUntilTick { get; set; }

    // Spec §110: рыдает — стресс-вариант краха §40.13. Она В СОЗНАНИИ (это
    // не обморок): ложится и плачет до этого тика, боль/урон обрывают сразу,
    // утешение (§53 Console) укорачивает. 0 = не плачет.
    public int CryingUntilTick { get; set; }

    // §105.14: притворяется мёртвой — очнулась, а враг рядом, и вставать
    // значит огрести снова; лежит и не шевелится до этого тика. Окно
    // перевзводит Slow-тик NeedsDecaySystem, пока враг в радиусе; SinceTick —
    // старт притворства, от него считается потолок PlayDeadMaxTicks.
    // ОБА НЕ сериализуются НАМЕРЕННО (как DyingTickStamp): перезагрузка
    // просто поднимет её — безобидно, а блоб трогать не приходится.
    public int PlayDeadUntilTick { get; set; }
    public int PlayDeadSinceTick { get; set; }

    // §30.17: ПРИЧИНА СМЕРТИ ЖИВЁТ В СОСТОЯНИИ, а не выкапывается из кольца
    // трасс. Раньше её искал MobSystem.FindRecentDeathCauseEvent, сканируя
    // world.Events на 240 тиков назад, — и это делало диагностику ЧАСТЬЮ
    // симуляции: кольцо держит 2048 записей, то есть с многословной трассой
    // ~11 тиков, а без неё сотни, поэтому один и тот же сид давал РАЗНУЮ
    // DeathRecord.Cause в редакторе, в билде и в headless-прогоне — а Cause
    // уходит в сейв и по проводу. Штамп ставится там же, где живёт самописец
    // (единственная точка, видящая каждое событие ровно один раз).
    //
    // НЕ сериализуется намеренно — как DyingTickStamp/DrowningSinceTick: это
    // передача внутри тика от удара к уборке тела. Сейв ровно между ними
    // (окно в считанные тики) вернёт обычный вывод причины по состоянию.
    public string DeathCauseText { get; set; } = string.Empty;
    public int DeathCauseTick { get; set; } = int.MinValue;

    // Spec §60: coma — the deep unconsciousness. Unlike the timed faint above,
    // a coma has no deadline: the body lies as if dead, recovering exactly as
    // in sleep, until the STAT that felled it climbs back over its wake
    // threshold. Entered when Energy hits 0 or Blood reaches the blood-loss
    // coma line.
    public ComaCause ComaCause { get; set; }

    // §60.7: тонет — тик, с которого тело лежит БЕЗ СОЗНАНИЯ в глубокой воде.
    // 0 = не тонет. Дотикает до SimBalance.DrownDeathTicks — смерть; очнулась
    // или больше не в воде (вытащили) — сбрасывается. НЕ сериализуется
    // НАМЕРЕННО (как DyingTickStamp): после загрузки отсчёт начинается заново —
    // безобидно, а блоб трогать не приходится.
    public int DrowningSinceTick { get; set; }

    // Spec §105: УМИРАЕТ. Глубже комы: кома ждёт, пока стат отрастёт, а это
    // обратный отсчёт до смерти. None = не умирает.
    public DyingCause DyingCause { get; set; }

    // §105: скрытый «запас смерти», 1 → 0. Это и есть отрицательная часть
    // шкалы: сам стат стоит на нуле (или зона на полу), а тает вот это.
    // Тает по реально прошедшим тикам, ускоряется от ударов по лежащей.
    public float DyingReserve { get; set; }

    // §105: тик, на котором запас считали в прошлый раз. Тем самым дрейн не
    // зависит ни от слоя тиков, ни от того, сколько раз за тик его позвали.
    // НЕ сериализуется: после загрузки 0 означает «перештамповать и пропустить
    // один вычет», что дешевле, чем сохранять и рисковать рассинхроном с
    // запасом (загрузка подарила бы ей целое окно дрейна одним куском).
    public int DyingTickStamp { get; set; }

    // §105: «едва живая» — штраф после спасения. Выносливость восстанавливается
    // втрое медленнее, тратится вдвое быстрее, ходит вдвое медленнее, пока не
    // истечёт этот тик. Близнец SickUntilTick / AdrenalineUntilTick.
    public int ConvalescentUntilTick { get; set; }

    // §81.10: понурая походка после сцены — держится до этого тика. Живёт
    // рядом с ConvalescentUntilTick намеренно: это такой же след в ТЕЛЕ, он
    // режет скорость и меняет клип шага, и больше ничего.
    // ⭐ Слою решений читать его НЕЛЬЗЯ — как и Breath (§71.2). Грусть меняет
    // ПОХОДКУ, а не цели: иначе побитая перестала бы есть и пить.
    public int SadWalkUntilTick { get; set; }

    // Spec 41.5: just woke up — stand and come to your senses until this
    // tick (no goal scoring), so nobody sprints off the pillow and the
    // get-up animation has room to play.
    public int WakeGraceUntilTick { get; set; }

    // §126/§49 r2: здесь жил NightSleepUntilRested — «отбой объявлен» (23:00 +
    // низкая энергия), намерение на несколько целей: собрать палку, накормить
    // очаг, пережить кризис и доспать до полного бара. Поле удалено вместе со
    // всем ночным затвором. Оно существовало только потому, что дневной порог
    // сна стоял на 0.203: лечь до 80% истощения было нельзя, и ночью человека
    // приходилось впускать в сон отдельным механизмом с часами. С порогом 0.45
    // и сном до полной энергии усталость делает то же самое сама — и без
    // топливного гейта, который умел сон ЗАПРЕТИТЬ.

    // Pain/fear spike after fresh damage. While active she should not start or
    // continue sleeping; every new hit extends the window.
    public int AdrenalineUntilTick { get; set; }

    // Spec 29C.4A (cornered-fight amendment): the first tick a mob caught her
    // in MELEE while she was fleeing. A flee only saves her if it BREAKS
    // contact; if the dog is still on top of her SimBalance.FleeStallTicks
    // later, the escape has plainly failed and she stops running to fight. 0 =
    // not currently pinned mid-flee. Transient combat bookkeeping — deliberately
    // NOT serialized (a save/load mid-flee simply grants a fresh grace window).
    public int FleeContactSinceTick { get; set; }

    // Spec 29C.4A (cornered-fight amendment): once a stalled flee converts to a
    // stand, she is COMMITTED to the fight until this tick so the medium-pass
    // flee assessment can't immediately send her running again (the re-flee
    // that produced the endless-maul loop). Re-armed every melee tick the mob
    // stays engaged, so the commitment lasts until the dog dies or breaks off.
    // Transient — not serialized. 0 = not committed.
    public int FightCommitUntilTick { get; set; }

    // Spec 29C.4A (standoff-release valve): sliding window of CONTINUOUS
    // square-up ticks — a mob tile-adjacent but never reaching melee (junction
    // gap: ledge, water, claimed ring). SinceTick anchors the window,
    // LastTick detects a broken run (the square-up branch runs on the Medium
    // layer; a gap of several passes = contact was lost and the window
    // restarts). Real melee contact resets both — the valve only ever judges
    // a stand that produced no exchange at all. Transient — not serialized.
    public int SquareUpSinceTick { get; set; }

    public int SquareUpLastTick { get; set; }

    // Spec 29C.4A: after StandoffReleaseTicks of blow-less square-up the girl
    // stops honouring the latch until this tick — she may drink, walk, plan
    // (the mob demonstrably cannot reach her). Melee contact cancels the
    // release instantly. Transient — not serialized. 0 = not released.
    public int StandoffReleaseUntilTick { get; set; }

    // Spec 29C.4B (assist hold): first tick the defender found herself already
    // ON STATION beside the attacker with nothing to do but wait for the
    // exchange. Silences the Started→Arrived replan churn (17 pairs in 68
    // ticks, seed 521091321 day 30). 0 = not holding. Transient.
    public int AssistHoldSinceTick { get; set; }

    public System.Collections.Generic.List<HexLive.Simulation.Common.ObjectId> GrievedCorpses { get; } = new();

    // Spec 28.8 v1 handshake: someone is walking over to talk to this NPC.
    // While set, this NPC accepts by waiting in place (no own plans) until
    // the initiator arrives, a timeout passes, or an emergency overrides.
    // Self-healed each decision pass.
    public HexLive.Simulation.Common.EntityId? PendingTalkFrom { get; set; }

    public int PendingTalkSinceTick { get; set; }

    // §127: the same transient handshake as PendingTalkFrom, but kept
    // separate so a romantic invitation cannot be mistaken for ordinary chat.
    public HexLive.Simulation.Common.EntityId? PendingRomanceFrom { get; set; }

    public int PendingRomanceSinceTick { get; set; }

    // Authoritative paired-scene state mirrored onto both participants and
    // persisted so a mid-scene save resumes the same authored pose and clock.
    public HexLive.Simulation.Common.EntityId? RomancePartnerNpcId { get; set; }

    public HexLive.Simulation.Common.EntityId? RomanceLeaderNpcId { get; set; }

    public string RomanceClipKey { get; set; } = string.Empty;

    public bool RomanceForced { get; set; }

    public float RomanceAnchorX { get; set; }

    public float RomanceAnchorY { get; set; }

    public float RomanceFacingDegrees { get; set; }

    public int RomanceCooldownUntilTick { get; set; }

    // §133: чужую вещь надевают только с разрешения, и спрашивают КАЖДЫЙ раз.
    // Отсюда две памятки, обе НЕ персистятся (идиома PendingTalkFrom): полученное
    // «да» на конкретную вещь — оно одноразовое и сгорает при надевании — и
    // свежее «нет», чтобы не ходить спрашивать по кругу. После загрузки сейва
    // она просто спросит заново, что ровно и требовалось.
    public System.Collections.Generic.List<WearGrant> WearGrants { get; } = new();

    public System.Collections.Generic.List<WearDenial> WearDenials { get; } = new();

    // Spec §53: someone is walking over to HELP this NPC (feed/treat/medicate/
    // console). Mirrors PendingTalkFrom — while set, the sufferer holds still so
    // the helper can reach her, until arrival, timeout, or an emergency. This is
    // separate from PendingTalkFrom so a chat and an aid claim don't clobber each
    // other. Self-healed each decision pass.
    public HexLive.Simulation.Common.EntityId? PendingAidFrom { get; set; }

    public int PendingAidSinceTick { get; set; }

    // §53.9: ВИД помощи, который назначил ИГРОК (AidPersonCommand). Пока метка
    // стоит, RunAid по прибытии не переторговывает вид по §53.3: беспомощную —
    // ту, что сама не поест и не попьёт, — кормят и поят именно тем, что выбрал
    // игрок. Без метки живая переоценка почти всегда отвечала за лежачую
    // Treat/Medicate (у неё же кровь и здоровье в полу), и приказ «Накормить»
    // молча умирал на «нечем перевязать», не тронув ни голода, ни жажды.
    // Пара ПЕРСИСТИТСЯ (сейв v60), в отличие от транзиентного PendingAidFrom:
    // цель Aid, план и его цель уже переживают сохранение, так что без неё
    // загруженная помощница доходила до подопечной и там переторговывала вид
    // по §53.3 — приказ игрока превращался в Treat или гас совсем.
    public AidKind OrderedAidKind { get; set; } = AidKind.None;

    public HexLive.Simulation.Common.EntityId? OrderedAidFor { get; set; }

    // §118.4: a fight may force a rescuer to put down the patient so both
    // hands are free. This remembers that exact patient until the combat scene
    // ends, so RescueSystem resumes the interrupted evacuation before bidding
    // on a different chore. Transient by design: the ordinary rescue scan
    // reconstructs the same intent after a load.
    public HexLive.Simulation.Common.EntityId? InterruptedRescuePatientId { get; set; }

    // Spec §53.7: the AID ERRAND. She agreed to help a housemate but had
    // nothing to give, so she is off fetching the missing supply — food for a
    // starving friend, water for a parched one, plantain for a bandage. The
    // marker keeps the fetch chore winning the auction for AidErrandTicks even
    // while the sufferer is out of sight behind her, and clears the moment the
    // supply is in hand (or the ward no longer needs it). AidErrandBid carries
    // the aid pull that spawned it so the chore keeps that weight.
    // Transient bookkeeping — deliberately NOT serialized: a load simply
    // re-decides from a full perception pass.
    public AidKind AidErrandKind { get; set; } = AidKind.None;

    public HexLive.Simulation.Common.EntityId? AidErrandFor { get; set; }

    public int AidErrandUntilTick { get; set; }

    public float AidErrandBid { get; set; }

    // §119: long-lived promise to replace one ally's concrete missing limb.
    // Unlike the short §53 aid handshake this survives ordinary replans/save.
    public HexLive.Simulation.Common.EntityId? ProstheticAidTargetId { get; set; }

    public BodyPart? ProstheticAidPart { get; set; }

    public int ProstheticAidRetryAfterTick { get; set; }

    // Reactive combat aid: when a fleeing victim calls for help, responders
    // get a short-lived Defend goal pointed at the attacker.
    public int LastHelpCryTick { get; set; } = -999999;

    // §54.14 (r2): the last tick she was genuinely freezing (ThermalComfort
    // below the §45 r5 friction-light threshold). Friction-lighting stays
    // unlocked for a grace window past this — she decided to light the fire
    // while shivering, and the walk to the pit must not revoke the decision
    // (probe: TendFire held 50+ ticks, then died mid-walk as dawn warmed her).
    public int LastFreezingTick { get; set; } = -999999;

    public int? CombatAssistDogId { get; set; }

    public HexLive.Simulation.Common.EntityId? CombatAssistAttackerNpcId { get; set; }

    // §72 raid state. All TRANSIENT (not serialized) — same call as the §29C
    // combat fields above: a reload starts him out of the hunt, which is
    // harmless, and the save format stays put.

    // Who he is stalking. Committed for the whole hunt: re-picking the weakest
    // target every rebuild (what §56 Prey does) turns a stalk into dithering.
    public HexLive.Simulation.Common.EntityId? RaidTargetNpcId { get; set; }

    // When this hunt began — feeds the hard pursuit ceiling.
    public int RaidStartedTick { get; set; }

    // Where he stood at the last plan rebuild, and since when. Two rebuilds
    // without moving = the chase is stuck, give it up (the mob chase valve).
    public HexLive.Simulation.Common.JunctionId? RaidLastJunction { get; set; }

    public int RaidStallSinceTick { get; set; }

    // No new hunt before this tick — the breather that stops him hammering the
    // colony raid after raid.
    public int RaidCooldownUntilTick { get; set; }

    // §81: кого он сейчас гнобит и на каком такте сцена.
    public HexLive.Simulation.Common.EntityId? AbuseTargetNpcId { get; set; }

    // Заявка на жертву — чтобы двое не начали одну сцену. Зеркало
    // PendingTalkFrom/PendingAidFrom, и, как они, В СЕЙВ НЕ ПИШЕТСЯ: цель Abuse
    // при сохранении обнуляется, так что недоигранной сцене неоткуда взяться.
    public HexLive.Simulation.Common.EntityId? PendingAbuseFrom { get; set; }

    public int AbuseCooldownUntilTick { get; set; }

    // §82: пауза между выгонами со двора, чтобы он не молотил одну и ту же
    // без передышки. В сейв не пишется — цель Raid при сохранении и так
    // обнуляется, недоигранной драке взяться неоткуда.
    public int TerritoryCooldownUntilTick { get; set; }

    // §117: transient-сцена выгона; Expel при сохранении сбрасывается.
    public HexLive.Simulation.Common.EntityId? ExpulsionTargetNpcId { get; set; }

    public HexLive.Simulation.Common.EntityId? PendingExpulsionFrom { get; set; }

    // 0 = подход, 1 = требовование/ответ, 2 = драка.
    public int ExpulsionPhase { get; set; }

    public int ExpulsionPhaseStartedTick { get; set; }

    public int ExpulsionProtectedUntilTick { get; set; }

    // Курсор такта хранится ЧИСЛОМ, а не выводится из времени: иначе один
    // пропущенный тик проглатывал бы удар или проигрывал его дважды.
    public int AbuseBeat { get; set; }

    public int AbuseBlows { get; set; }

    // §103: сколько ударов ОБЪЯВЛЕНО этой сценой и во сколько длин клипа они
    // разводятся. Пока это были ручки баланса, читаемые боевой системой, фразу
    // «ударь три раза» не мог произнести никто целиком — см. AI/FightScene.cs.
    public int SceneBlowsPlanned { get; set; }

    public float SceneBlowSpacingClips { get; set; }

    // §103: на каком тике прозвучал приговор. Такт добычи отсчитывается от
    // него, а не от начала сцены: приговор теперь наступает по числу ударов и
    // приходится на разные тики, а пауза перед добычей должна быть одинаковой.
    public int AbuseVerdictTick { get; set; }

    // §97: каким оружием драться СЕЙЧАС, если не самым лучшим. Пустая строка —
    // кулаки, null — «как обычно, лучшим». Сцена абьюза ставит это по глубине
    // неприязни: наезд начинается рукопашкой, а тесак достают, когда уже
    // ненавидят. В сейв не пишется — сцена не переживает сохранение.
    public string ForcedMeleeWeaponId { get; set; }

    public bool AbuseHasLoot { get; set; }

    // §81.13: здоровье на входе в сцену, у ОБЕИХ сторон. Разница на выходе и
    // есть «кто проиграл»: дельта честнее счётчика ударов — она видит и броню,
    // и пощаду §86, и тычки подошедших защитниц. В сейв не пишется, как и всё
    // сценное: сцена длится ≤60 тиков и сохранение не переживает.
    public float SceneStartHealth { get; set; }

    // §104 r12: тик, до которого ПОСЛЕДНИЙ удар сцены доигрывает свой клип.
    // Приговор, наступавший в момент попадания (хит — середина клипа), обрывал
    // прострелку и менял оружие в руке на глазах у игрока. В сейв не пишется.
    public int SceneLastBlowRestTick { get; set; }
    // §108: кого группа пошла бить. Стоит у КАЖДОЙ участницы сговора — общей
    // «группы» как объекта нет нарочно: связь держится тем, что цель у всех
    // одна, и это же делает распад группы бесплатным (умерла/отстала — просто
    // выбывает). TRANSIENT, как и остальные боевые поля: цель GroupHunt при
    // сохранении обнуляется.
    public HexLive.Simulation.Common.EntityId? GroupHuntTargetNpcId { get; set; }

    // Когда сговорились — от этого тика считается бюджет охоты.
    public int GroupHuntStartedTick { get; set; }

    // ⭐ Здоровье И худшая часть НА ВХОДЕ в расправу — образец §81.13, где
    // «кто проиграл» тоже считается разницей, а не абсолютом. Уходит та, кому
    // досталось ЗДЕСЬ. Абсолютный порог этого не отличал: колонистка со старым
    // рубцом на ноге (Worst=0,28 при здоровье 0,84) выбывала, не получив ни
    // одного удара, — то есть хромая не имела права и подойти.
    public float GroupHuntStartHealth { get; set; }

    public float GroupHuntStartWorstPart { get; set; }

    // Сколько ударов ОНА успела всадить за эту охоту — её личный вклад, для
    // трейса ухода: видно, ушла она отбив своё или сразу.
    public int GroupHuntBlowsLanded { get; set; }

    // ⭐ А это — сколько всадили ЕМУ, и мера «побили» считается отсюда. Счётчик
    // висит на ЦЕЛИ, а не складывается из охотниц, потому что охотницы уходят:
    // отступившей обнуляют её счёт, и сумма по группе съёживалась на глазах —
    // шесть ударов при пороге шесть превращались в два, и расправа записывалась
    // в провал (сид 42). Побои не рассасываются оттого, что бившая ушла.
    public int GroupHuntBlowsTaken { get; set; }

    // Передышка после охоты, любой. Проверяется ПРИ СГОВОРЕ, а не в аукционе:
    // цель реактивная, аукцион её и не спрашивает, так что кулдаун цели её бы
    // не удержал и они шли бы на него снова в тот же вечер.
    public int GroupHuntCooldownUntilTick { get; set; }

    // §111: кого он обыскивает. Коммит на всю сцену — перевыбирать ближайшее
    // тело на каждом ребилде значило бы метаться между двумя лежащими, ровно
    // как §56 Prey метается между жертвами. TRANSIENT, как весь сценный блок.
    public HexLive.Simulation.Common.EntityId? LootHelplessTargetNpcId { get; set; }

    // Заявка НА ЖЕРТВЕ — чтобы двое не сели на одно тело. Зеркало
    // PendingAbuseFrom, и снимать её обязаны ВСЕ выходы сцены: забытый клейм
    // делает тело «занятым навсегда», и больше его не тронет никто.
    public HexLive.Simulation.Common.EntityId? PendingLootedBy { get; set; }

    // Передышка: длинная после сыгранной сцены, короткая после срыва.
    public int LootHelplessCooldownUntilTick { get; set; }

    // Сколько вещей снято этой сценой. Курсор такта хранится ЧИСЛОМ, а не
    // выводится из времени: пропущенный тик иначе проглатывал бы вещь или
    // снимал её дважды (тот же урок, что у AbuseBeat).
    public int LootHelplessTakenCount { get; set; }

    // Set on BOTH sides while a human fight is live. An NPC has a single swing
    // slot, so this is also how the human exchange claims it from the animal
    // one (AnimalCombatSystem bails while it is set).
    public HexLive.Simulation.Common.EntityId? CombatOpponentNpcId { get; set; }

    // Spec §49 (water sickness v2): raw water no longer bites in one lump.
    // A positive roll opens a visible window (SickUntilTick, drives the 🤢 icon
    // + comfort malaise) and adds to a bounded damage budget
    // (SicknessDamageRemaining) the torso pays down a little each slow tick.
    // The budget cap keeps total harm ≈ the old instant model even when a
    // thirsty colony drinks raw back-to-back (overlapping windows). 0 = well.
    public int SickUntilTick { get; set; }

    public float SicknessDamageRemaining { get; set; }

    public GoalLock? GoalLock { get; set; }

    // §121: ЕЮ УПРАВЛЯЕТ ИГРОК. Пока флаг стоит, аукцион целей (DecisionSystem)
    // и планировщик её пропускают целиком: цели приходят только из очереди
    // команд. Единственное поле ручного режима, которое ПИШЕТСЯ В СЕЙВ (v28) —
    // остальное сценное, а «под чьим управлением персонаж» переживает выход
    // из игры так же, как то, во что она одета.
    public bool ManualControl { get; set; }

    // §133.9 / bug #193: player-owned outfit latch. While enabled, ordinary
    // Dress/Undress and player wear/stow/drop commands cannot change clothing.
    // §133.10 upgrades the latch into a persistent selected outfit: enabling it
    // snapshots the worn definitions below, and a periodic high-priority pull
    // restores any exact ground object temporarily removed for laundry/drying.
    public bool OutfitLocked { get; set; }

    /// <summary>
    /// §133.10: the selected outfit captured when <see cref="OutfitLocked"/>
    /// turns on. Entries remain while their garment is worn, drying, carried
    /// home, temporarily unreachable, or absent; <c>GroundObjectId</c> follows
    /// the exact loose instance whenever one exists.
    /// </summary>
    public List<DesiredOutfitPiece> DesiredOutfit { get; } = new();

    // §133.10: derived scheduler state. The desired set and its exact ground
    // ids are persistent; audit cadence and the currently actionable target
    // are cheap to rebuild after load and therefore deliberately transient.
    public int NextOutfitMaintenanceTick { get; set; }

    public HexLive.Simulation.Common.ObjectId? OutfitMaintenanceTargetObjectId { get; set; }

    // §121.7 (v46 legacy): сохранённый слот прежнего потикового таймаута.
    // Больше не участвует в поведении, но остаётся в модели, чтобы не менять
    // бинарный формат существующих сейвов.
    public int LastManualInputTick { get; set; }

    // §121.7: последнее продление реального lease ручного управления.
    // Монотонное время процесса намеренно не сериализуется; после загрузки
    // первый idle-проход начинает новое полное окно.
    public double? ManualControlLeaseRenewedAtSeconds { get; set; }

    // §121: кого ей приказано бить. Живёт ровно как боевые поля выше — в сейв
    // НЕ пишется, цель PlayerAttack при сохранении складывается в None
    // (SaveGoal), так что недоигранной драке взяться неоткуда.
    public HexLive.Simulation.Common.EntityId? ManualAttackNpcId { get; set; }

    // §121: то же для зверя. Идентификатор моба — int, а не EntityId: мобы
    // живут в отдельном списке со своей нумерацией (см. MobState.Id).
    public int? ManualAttackMobId { get; set; }

    // §121.10 (баг #270): живой приказ «собрать всё на гексе». Хранится ровно
    // то, чем описывается СЛЕДУЮЩАЯ задача очереди: какой гекс, какие предметы
    // считать однотипными, каким действием их брать и сколько подходов ещё
    // разрешено. Самих целей тут нет намеренно — список предметов гекса
    // перечитывается из мира перед каждым подходом, как того требует правило 2
    // ManualCommandExecutor: за время похода гекс мог измениться.
    //
    // В сейв НЕ пишется, как и остальная сцена ручного режима: цель
    // PlayerOrder при сохранении складывается в None, и недоигранной очереди
    // после загрузки взяться неоткуда.
    public string GatherAllDefinitionId { get; set; } = string.Empty;

    public HexLive.Simulation.Common.TileCoord? GatherAllTile { get; set; }

    public InteractionType? GatherAllInteraction { get; set; }

    public string GatherAllInteractionId { get; set; } = string.Empty;

    /// <summary>Сколько подходов очереди ещё разрешено. Бюджет ставится по
    /// числу однотипных предметов на гексе в момент приказа: он доказывает
    /// завершимость очереди даже если предмет почему-то перестал исчезать
    /// после «успешного» подбора (полный рюкзак и прочая ложь исполнителя).
    /// </summary>
    public int GatherAllRemaining { get; set; }

    // §40.6: garments doffed at the shore for a bathe. After washing her body
    // she walks back to RedressShore and puts these EXACT ground pieces back
    // on — the same clothes she took off. Holds the dropped pile's object ids
    // (stale/taken pieces are skipped); RedressShore is the junction to return
    // to. Both cleared once she is dressed again (or the pile is gone).
    public List<HexLive.Simulation.Common.ObjectId> RedressGarments { get; } = new();

    public HexLive.Simulation.Common.JunctionId? RedressShore { get; set; }

    // RedressGarments are authoritative world objects throughout one bath.
    // LaundryBatch is decoded from legacy saves as Bathing by the planner.
    public PersonalCarePhase PersonalCarePhase { get; set; }

    public HexLive.Simulation.Common.JunctionId? PersonalCareBathShore { get; set; }

    public List<GoalCooldown> Cooldowns { get; } = new();

    public List<GoalScore> LastScores { get; } = new();

    public DecisionResult LastDecision { get; set; } = new();
}

/// <summary>
/// One persistent member of §133.10's selected outfit. DefinitionId preserves
/// the chosen colourway/set identity; GroundObjectId preserves the physical
/// loose garment while it is off the body.
/// </summary>
public sealed class DesiredOutfitPiece
{
    public string DefinitionId { get; set; } = string.Empty;

    public HexLive.Simulation.Common.ObjectId? GroundObjectId { get; set; }
}

/// <summary>
/// §133: разовое «да, надень» на КОНКРЕТНУЮ вещь. Сгорает при надевании и по
/// истечении срока, поэтому за вторую вещь (и за ту же вещь во второй раз)
/// придётся спрашивать снова — прямое требование игрока.
/// </summary>
public sealed class WearGrant
{
    public HexLive.Simulation.Common.ObjectId Item { get; set; }

    public HexLive.Simulation.Common.EntityId Owner { get; set; }

    public int ExpiresTick { get; set; }
}

/// <summary>
/// §133: свежее «нет» на конкретную вещь. Без него отказ ничего не менял бы в
/// мире и просящая ходила бы спрашивать по кругу каждый тик.
/// </summary>
public sealed class WearDenial
{
    public HexLive.Simulation.Common.ObjectId Item { get; set; }

    public int UntilTick { get; set; }
}

// Spec §60: what dropped the body into a coma — and therefore which stat must
// recover past the wake threshold before it comes to. (Append-only: saves
// store ints.)
public enum ComaCause
{
    None,
    Exhaustion, // Energy drained to 0 — sleeps it off where she fell
    BloodLoss   // Blood fell below the coma line — out until it knits back
}

// Spec §105: что именно её убивает — и, значит, ЧЕМ её можно спасти. Причина
// выбирает и вид помощи (кровь/грудь → перевязка, голод → еда, жажда → вода),
// и длину окна, и характеристику, которая это окно растягивает.
// (Append-only: сейв хранит ординал.)
public enum DyingCause
{
    None,
    BloodLoss,      // кровь на нуле — самый быстрый исход, лечится перевязкой
    VitalCrushed, // грудь пробита в ноль — тоже перевязка
    Starvation,     // голод доел тело — нужна еда
    Dehydration     // жажда доела тело — нужна вода
}

public enum GoalType
{
    None,
    Eat,       // consume food from inventory
    GetFood,   // acquire food from the world (PickUp)
    Sleep,
    Sit,
    Dress,
    Socialize,   // talk to another NPC (iteration 4)
    Explore,     // wander to a random far junction (iteration 9)
    Flee,        // run to the nearest indoor junction (iteration 10)
    Undress,     // take off the warmest safe-to-remove item (iteration 11)
    Drink,        // drink from the carried bottle in place (iter 13 / 29H)
    GetWater,     // fill the bottle at a water source (iteration 29)
    GatherTools,  // pick up a missing lighter/pot (iteration 13)
    GatherWood,   // pick up wood (log or stick) off the ground (iteration 13 / §54)
    SplitLog,     // §54: chop a ground log into sticks (needs an axe, in the field)
    ChopCrown,    // §54.2: chop a felled palm crown into loose leaves (needs an axe)
    GatherLeaves, // §54.2: pick scattered palm leaves off the ground
    TendFire,     // fuel/light the campfire with a carried stick (iteration 13 / §54)
    Hunt,         // chase a rabbit with a spear (iteration 14)
    Prey,         // §56: kill a housemate for meat — starvation last resort
    CraftSpear,   // whittle a spear from a log at the campfire (iteration 14)
    CookMeat,     // cook raw meat on the lit fire (iteration 14)
    CraftLeather, // sew pants from a hide, worn immediately (iteration 14)
    Mourn,        // visit a body (rite) or a grave (remembrance) (iter 15/16)
    Bury,         // lay a housemate's body to rest (iteration 16)
    GatherStone,  // pick up stones for tool recipes (iteration 18)
    CraftAxe,     // stone axe: 1 log + 1 stone (iteration 18)
    CraftPickaxe, // stone pickaxe: 1 log + 2 stones (iteration 18)
    HarvestTree,  // fell a big tree or palm with axe/saw (iteration 18)
    MineBoulder,  // break a boulder with the pickaxe (iteration 18)
    Build,        // add a piece to the communal hut (iteration 19)
    CoolOff,      // stand in shade or the river when overheating (iter 20)
    WarmUp,       // huddle by the burning campfire when freezing (spec 42)
    GatherHerb,   // pick healing leaves (spec 44)
    CraftBandage, // 2 herb leaves -> bandage at the campfire (spec 44)
    HarvestYucca, // §54: cut a yucca with a blade (knife/axe) — fiber scatters
    GatherFiber,  // §54: pick plant fiber off the ground
    CraftRope,    // §54: 3 fiber -> rope (lashing) at the campfire
    CraftCloth,   // §54: 4 fiber -> cloth at the campfire
    CraftKnife,   // §54: 1 stick + 1 stone -> knife at the campfire
    Butcher,      // §54: knife a carcass/corpse into meat + hide (needs a knife)
    CraftRack,    // drying rack: 2 logs at the campfire (iteration 21)
    DryClothes,   // hang the wettest garment / stand by the fire (iter 21)
    CraftBed,     // bedroll: 2 logs + 3 palm leaves (iteration 28)
    CraftTent,    // sun shelter: 4 palm leaves at the campfire (spec 40.14)
    BuildRaft,    // haul logs to the escape raft — the way off the island (40.15)
    CraftBow,     // bow: 2 logs + 1 hide at the campfire (iteration 22)
    CraftArrows,  // 1 log -> 3 arrows at the campfire (iteration 22)
    Aid,          // tend a suffering housemate — feed/treat/medicate/console (spec 53)
    PlaceSite,     // §52: stake out a furniture build-site (intent point)
    DeliverToSite, // §52: haul a needed material to a build-site and deposit it
    BuildFurniture,// §52: raise a fully-stocked build-site with a hammer
    HaulToFire,    // §52: carry a low-value item to the fireside stockpile to free a slot
    Idle,
    Defend,      // answer a combat help cry and attack the aggressor
    Bathe,       // undress at shore, then swim long enough to wash the body
    WashClothes, // wash one dirty ground garment at the shore
    StowBottle,  // §54.15: park the empty bottle under the water collector's funnel
    // §68: dress your OWN wounds with a carried bandage. Appended at the END —
    // the save blob stores goals by ordinal, so inserting mid-enum would
    // re-label every goal in every existing save.
    TreatWounds,
    // §72: hunt a member of a HOSTILE faction. Opportunist — it only outbids
    // his chores when the odds are his. Appended at the END, same reason.
    Raid,
    // §81: сцена насилия ради припаса ИЛИ ради самого контакта. Дописана в
    // КОНЕЦ — сейв хранит цели ординалом, вставка в середину перемаркировала бы
    // каждую цель в каждом существующем сейве.
    Abuse,
    // §28.15F: забрать вещь с тела погибшей. Раньше смерть сама вываливала весь
    // гардероб под ноги, и подбирать было нечего — теперь одежда и карманы
    // остаются на теле, и за ними надо ПРИЙТИ. Дописана в конец, та же причина.
    LootCorpse,

    // §108: идти бить чужака ВМЕСТЕ. Единственная цель, которую NPC берёт не
    // сама и не от испуга, а по сговору: её раздаёт GroupHuntMath.TryFormPact
    // сразу всем, кто стоял в кружке. Дописана в конец — сейв хранит цели
    // ординалом.
    GroupHunt,

    // §111: обыскать беспомощного врага — лежащего в коме, умирающего или в
    // обмороке. Не «добить», а «разоружить и обчистить»: между §81 (та в
    // сознании) и §28.15F (та мертва) лежала живая, но выключенная, и её никто
    // не трогал. Дописана в конец — сейв хранит цели ординалом.
    LootHelpless,

    // §117: прогнать враждебного NPC из своего лагеря. Append-only: сейв хранит ординал.
    Expel,

    // §116 append-only: rescue/medical intent and craft outputs.
    Rescue,
    PickUpPerson,
    PutInBed,
    Splint,
    FitProsthetic,
    CraftSplint,
    CraftWoodenArm,
    CraftWoodenLeg,

    // §121: ПРИКАЗ ИГРОКА — идти в точку или сделать что-то с объектом.
    // Колонка взаимодействия в каталоге намеренно пуста: конкретный
    // InteractionType несёт шаг плана, потому что одна и та же цель исполняет
    // и «подобрать», и «срубить», и «выпить». Реактивная — её ставит
    // ManualCommandExecutor, аукцион о ней не знает. Append-only: сейв хранит
    // ординал, и эту цель он хранит по-настоящему (план приказа переживает
    // сохранение и продолжается после загрузки).
    PlayerOrder,

    // §121: ПРИКАЗ БИТЬ — отдельной целью, а не флагом на PlayerOrder, потому
    // что боевые системы рассуждают СПИСКАМИ целей (§109 AnswerBlows, зачистка
    // призрачных пар, MobSystem). PlayerAttack встаёт в эти списки ровно как
    // Expel, а PlayerOrder не должен попасть ни в один из них.
    PlayerAttack,

    // §123 append-only: authoritative wear/stow/drop plans issued by the
    // player. Stored like PlayerOrder; no save format bump is required.
    PlayerInventory,

    // §133 append-only: отнести свою (или ничейную) одежду, валяющуюся вдали
    // от дома, в гардероб/на сушилку — чтобы вещи жили у дома, а не по карте.
    StowClothes,

    // §50.9 append-only: «спуститься, пока ноги держат» — раненая, чей мир без
    // прыжка сжался до крошечного уступа, уходит на большую землю, пока прыжок
    // ещё возможен. Порог прыжка 0.75 на ногу, и пара укусов превращает гору в
    // ловушку: seed 987654 — Ines умерла от жажды на компоненте из 3 узлов с
    // четырьмя бинтами в рюкзаке.
    ReachSafeGround,

    // §140.2 append-only: «ДОМОЙ». Раненая, которой мир больше ничего не
    // предлагает, идёт к своему очагу — туда, где лежат орехи, стоит сборник
    // дождя и ходят соседки, способные её напоить. Отдельная цель, а не режим
    // Explore: у Explore случайная дальняя точка, а здесь ровно одна — якорь
    // лагеря, и доступна она в противоположной ситуации (Explore — сытой и
    // целой, Homeward — израненной или той, кому нечем утолить нужду).
    Homeward,

    // §127 append-only: consensual or forced paired intimacy. The concrete
    // branch and authored pose live in transient Romance* fields above.
    Romance
}

public sealed class GoalScore
{
    public GoalType Goal { get; set; }

    public float BaseScore { get; set; }

    public float NeedModifier { get; set; }

    // MemoryModifier и CommandModifier отсюда УБРАНЫ: они объявлялись,
    // печатались в трассу отдельными колонками — и не присваивались никогда.
    // Трасса тем самым обещала разложение оценки, которого в модели нет:
    // читающий видел «Mem=0,000» и думал, что память учтена и просто не
    // сработала, а её там не было вовсе.

    public float SocialModifier { get; set; }

    public float EnvironmentModifier { get; set; }

    public float EmergencyModifier { get; set; }

    public float FinalScore { get; set; }
}

public sealed class GoalLock
{
    public GoalType Goal { get; set; }

    public int StartTick { get; set; }

    public int EndTick { get; set; }
}

public sealed class GoalCooldown
{
    public GoalType Goal { get; set; }

    public int EndTick { get; set; }
}

public sealed class DecisionResult
{
    public GoalType SelectedGoal { get; set; }

    public List<GoalScore> Scores { get; } = new();

    public string Reason { get; set; } = string.Empty;
}

}
