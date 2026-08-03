using System.Collections.Generic;

namespace HexLive.Simulation.AI
{

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

    // Spec §60: coma — the deep unconsciousness. Unlike the timed faint above,
    // a coma has no deadline: the body lies as if dead, recovering exactly as
    // in sleep, until the STAT that felled it climbs back over its wake
    // threshold. Entered when Energy hits 0 or Blood reaches the blood-loss
    // coma line.
    public ComaCause ComaCause { get; set; }

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

    // Spec §53: someone is walking over to HELP this NPC (feed/treat/medicate/
    // console). Mirrors PendingTalkFrom — while set, the sufferer holds still so
    // the helper can reach her, until arrival, timeout, or an emergency. This is
    // separate from PendingTalkFrom so a chat and an aid claim don't clobber each
    // other. Self-healed each decision pass.
    public HexLive.Simulation.Common.EntityId? PendingAidFrom { get; set; }

    public int PendingAidSinceTick { get; set; }

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
    // §108: кого группа пошла бить. Стоит у КАЖДОЙ участницы сговора — общей
    // «группы» как объекта нет нарочно: связь держится тем, что цель у всех
    // одна, и это же делает распад группы бесплатным (умерла/отстала — просто
    // выбывает). TRANSIENT, как и остальные боевые поля: цель GroupHunt при
    // сохранении обнуляется.
    public HexLive.Simulation.Common.EntityId? GroupHuntTargetNpcId { get; set; }

    // Когда сговорились — от этого тика считается бюджет охоты.
    public int GroupHuntStartedTick { get; set; }

    // Сколько ударов ОНА успела всадить за эту охоту. Сумма по группе и есть
    // мера «побили»: с пощадой §108 он не падает, и без счётчика у расправы не
    // было бы успешного конца вовсе — только истёкший бюджет.
    public int GroupHuntBlowsLanded { get; set; }

    // Передышка после охоты, любой. Проверяется ПРИ СГОВОРЕ, а не в аукционе:
    // цель реактивная, аукцион её и не спрашивает, так что кулдаун цели её бы
    // не удержал и они шли бы на него снова в тот же вечер.
    public int GroupHuntCooldownUntilTick { get; set; }

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

    // §40.6: garments doffed at the shore for a bathe. After washing her body
    // she walks back to RedressShore and puts these EXACT ground pieces back
    // on — the same clothes she took off. Holds the dropped pile's object ids
    // (stale/taken pieces are skipped); RedressShore is the junction to return
    // to. Both cleared once she is dressed again (or the pile is gone).
    public List<HexLive.Simulation.Common.ObjectId> RedressGarments { get; } = new();

    public HexLive.Simulation.Common.JunctionId? RedressShore { get; set; }

    public List<GoalCooldown> Cooldowns { get; } = new();

    public List<GoalScore> LastScores { get; } = new();

    public DecisionResult LastDecision { get; set; } = new();
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
    TorsoDestroyed, // грудь пробита в ноль — тоже перевязка
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
    GroupHunt
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
