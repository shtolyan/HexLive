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

// §72: the hostile faction. The outsider is a FULL survivor — same needs, same
// GOAP auction, same crafting and wardrobe as the girls — who happens to be on
// the other side. He is an opportunist, not a berserker: he lives his own life
// and only goes for a kill when the odds are his (a lone, weak or sleeping
// target while he is healthy and armed). The girls never strike first: they SEE
// him (§62 layer), route around him (DangerRing) and only rally once he draws
// blood (§29C.4B friend-guard + §57 help cry).
//
// Enabled = false collapses FactionRelations to "everyone is an ally", so every
// gate in the sim answers exactly as it did pre-§72. Together with
// OutsiderCount = 0 the world is the pre-§72 world byte for byte — the two
// flags are separate so a soak can bisect "the faction plumbing" and "the extra
// body" independently (the extra body alone perturbs dog spawn placement).
public static class Spec72
{
    // ВКЛЮЧЕНО. Охота запускается (старт — прерывание, не ставка в аукционе),
    // снаряжение с бронёй зарегистрировано, отпор колонии работает. Баланс ещё
    // не доведён: в последнем замере чужак не убил никого и гибнет сам, так что
    // это скорее «он есть и мешает жить», чем «он страшен».
    public static bool Enabled = true;

    // Сколько чужаков селить. 0 — ни одного: это и есть прежний флаг «не
    // селить», только без второй ручки рядом, которая неминуемо разошлась бы с
    // первой. Отдельно от Enabled, чтобы соак мог развести влияние ПРАВИЛ и
    // влияние лишних тел (они сами по себе двигают спавн собак и маршруты).
    //
    // Все чужаки — одна фракция, то есть союзники друг другу, и живут одним
    // лагерем: садятся на якорь стоянки и кольцо вокруг него.
    public static int OutsiderCount = 1;

    // §72.14: wave 0 is the authored male outsider. Wave 1 lands after three
    // days, then one new hostile survivor every three days. Exact authored rule
    // (not a tuning knob): save migration and deterministic wave numbering both
    // depend on this interval.
    public const int RaidWaveIntervalDays = 3;

    // --- His camp -----------------------------------------------------------

    // How far his camp must sit from the girls' home plateau. The island is
    // q in [-8,10] / r in [-6,8], so 8 hexes is genuinely "the far side".
    public static int OutsiderCampMinDistanceTiles = 8;

    // How far from her OWN camp anchor a survivor is seeded with permanent
    // home knowledge at bootstrap. Without this the outsider starts out knowing
    // the girls' entire camp layout (and they know his).
    public static int CampKnowledgeRadiusTiles = 4;

    // Насколько далеко от ЛЮБОЙ стоянки заводятся собаки. Обычное правило
    // спавна держит 5 гексов от каждого NPC, но это от того места, где он
    // стоит СЕЙЧАС: стоит ему отойти за дровами, и стая заводится прямо у его
    // очага, а вернувшись он входит в неё. У колонии это скрадывалось тем, что
    // четверо девушек постоянно топчутся дома и закрывают собой округу; у
    // одиночки скрадывать некому.
    public static int DogSpawnMinDistanceFromCamp = 9;

    // The widest a camp may be — the anchor-scoped hearth search radius, so a
    // faction's "our fire" is its own and not whichever campfire hashes first.
    public static int MaxCampRadiusTiles = 6;

    // He is a colder personality than the girls (§53 spreads the colony over
    // Spec53.TraitMin..TraitMax); a compassionate raider would never raid.
    public static float OutsiderCompassionMin = 0f;
    public static float OutsiderCompassionMax = 0.25f;

    // §76: HIS BODY IS AUTHORED, NOT ROLLED. The girls draw from the §76.2
    // point-buy budget — same total, different shape, different every world.
    // He does not: he is a fixed antagonist, and a raider who might roll frail
    // is a raider the player meets as a pushover on half the seeds. Authored
    // through NpcBootstrap.Attributes, so it travels the ordinary
    // blank-means-roll override path (§74's rule) rather than a special case
    // inside the roll.
    //
    // The profile deliberately BREAKS the colony budget — it sums to 4.0 where
    // theirs sums to 3.0. That is the point of "not by the same rules": he is a
    // big man who has been living rough, and the girls beat him with numbers,
    // tools and a fire, not with better stats.
    //
    // ⚠️ This stacks with RaidStrikeDamageMult (1.35) — both make him hit
    // harder. Move ONE of them at a time when tuning, or a re-soak cannot tell
    // you which did it.
    public static float OutsiderStrength = 0.9f;   // 9/10 — он крупный и бьёт тяжело
    public static float OutsiderEndurance = 0.8f;  // 8/10 — привык идти весь день
    public static float OutsiderToughness = 0.8f;  // 8/10 — держит удар: он один, спасать некому
    public static float OutsiderHardiness = 0.6f;  // 6/10 — терпит голод и холод лучше домашних
    public static float OutsiderAgility = 0.5f;    // 5/10 — тяжёлый, не быстрый
    public static float OutsiderWits = 0.4f;       // 4/10 — не мастеровой: его сила в руках
    public static float OutsiderPerception = 0.7f; // 7/10 — §125: зоркий охотник, живёт наблюдением

    // He comes ashore with a knife. Not a handout — a lone man has none of the
    // four-way division of labour the colony has, and the first soak had him
    // dead of thirst before the grace period was even over on half the seeds.
    // It also unblocks the hunt itself: IsFitToRaid demands a real weapon, and
    // fists never count.
    public static bool OutsiderStartsArmed = true;

    // --- The hunt -----------------------------------------------------------

    // §125.4: как далеко он ищет жертву — его радиус восприятия; он же
    // нормирует вес близости в Opportunity.

    // How many of the victim's allies he still tolerates near her. 1 = he will
    // take on a straggler with one friend nearby, never the whole camp.
    public static int RaidMaxVictimAllies = 1;

    // Radius counting the victim's friends, and how many of them fully cancel
    // the isolation term (2 → one friend halves it, two zero it).
    public static int RaidIsolationRadiusTiles = 3;
    public static int RaidCrowdCount = 2;

    // The opportunity mix. Isolation dominates on purpose — "alone" is the
    // premise, "wounded" is a bonus. Sums to 1.
    public static float RaidWeightIsolation = 0.40f;
    public static float RaidWeightWeakness = 0.30f;
    public static float RaidWeightHelpless = 0.20f;
    public static float RaidWeightProximity = 0.10f;

    // Minimum target quality to bid at all, and the quality below which he
    // calls off a hunt already in progress (her friends showed up). 0.45 lets a
    // LONE, whole girl close by qualify (isolation 0.40 + proximity) — which is
    // the "he picks off stragglers" premise. At 0.55 only the wounded and the
    // sleeping ever cleared it, and in practice nobody did.
    public static float RaidOpportunityFloor = 0.45f;
    public static float RaidAbandonOpportunity = 0.30f;

    // He goes looking when nobody is in range: a walk toward the colony's camp.
    // Without it his camp is too far for anyone to ever wander into his scan
    // radius and the whole hunt is dead code.
    public static bool ProwlEnabled = true;

    // Close enough to their camp that walking further adds nothing — from here
    // the ordinary victim scan takes over.
    public static int ProwlArrivedTiles = 5;

    // He gives up at the hut door, exactly like a dog (§29C.4A sanctuary).
    public static bool RaidRespectsSanctuary = true;

    // Hard ceiling on one pursuit, and the goal-lock that stops him
    // re-auctioning every medium tick mid-stalk.
    public static float RaidPursuitMaxTicks = 600f;
    public static int RaidLockTicks = 240;

    // Сколько защитниц вокруг заставляют его отступить. Двое — это почти
    // всегда (friend-guard срабатывал 147 раз за прогон), поэтому он убегал, не
    // успев ничего сделать. Трое — уже настоящая толпа.
    public static int RaidBreakOffDefenders = 3;

    // --- Combat spacing (§72.5, the human mirror of §29C.3's stand-off) -----

    // Melee reach is junction-based and junction spacing (~0.37 wu) is far
    // tighter than two human models, so paired fighters used to stand inside
    // each other. While a fighter carries a live CombatOpponentNpcId and is
    // STANDING, HumanCombatSystem backs her rendered Position off until the
    // pair is this far apart (wu). Junction/Tile, reach and the blows are
    // untouched — spacing, not range. 0 = off.
    public static float MeleeHoldDistance = 0.9f;

    // How fast a fighter backs into the stand-off ring, wu/s — a deliberate
    // step back (walking pace is ~1.2 wu/s), not a teleport.
    public static float MeleeHoldGlideSpeed = 0.8f;

    // §109.11: как далеко стойка может ОТПЯТИТЬСЯ от своего узла (wu). Junction
    // не едет вместе с Position, и без предела отжим против непрерывно
    // наступающего противника каравана через полкарты: «скользит в боевой
    // позе, он бежит следом». Упёрлась в предел — стоит.
    public static float MeleeHoldMaxDriftWorldUnits = 0.75f;

    // Множитель ТОЛЬКО его ударов — им и утяжеляют, и смягчают чужака, не
    // трогая оружейные листы, которыми машут и девушки. 1.35: с ровно единицей
    // он не убил никого за 7 дней на трёх сидах, размениваясь 20 ударами против
    // 30 ответных.
    public static float RaidStrikeDamageMult = 1.35f;

    // No raid before this many game days — the colony gets a fire, a hut and
    // usually a knife first (dogs start raiding at 2).
    public static int RaidGraceDays = 5;

    // "Weak enough": a victim above this Health is not worth the risk. A
    // sleeping or unconscious target ignores the ceiling — that is the whole
    // point of an opportunist.
    public static float RaidVictimHealthCeiling = 0.75f;

    // Below this he stops hunting and goes to lick his wounds instead. Split
    // from the per-PART floor on purpose: the same 0.7 applied to the worst
    // body part blocked the hunt on 113 of 600 post-grace samples, because a
    // man who met a dog once carries a sub-0.7 limb for days afterwards.
    public static float RaidSelfHealthFloor = 0.7f;
    public static float RaidSelfWorstPartFloor = 0.7f;

    // Goal weight: the auction adds 0.1, so the bid is
    // 0.1 + RaidBaseScore + RaidOpportunityGain * opportunity — 0.45 for a bare
    // prowl, ~0.9 for a lone sleeping straggler.
    //
    // These are much higher than they first look, and they have to be. A lone
    // man is permanently uncomfortable and short on stamina, which drives his
    // Sit ("leisure") bid as high as 0.85 — at the original 0.15/0.45 the hunt
    // was AVAILABLE for ~14% of the post-grace run and won the auction exactly
    // zero times in ten game days. Safety comes from the self-gates instead:
    // he cannot even bid while hungry, thirsty, tired or hurt.
    public static float RaidBaseScore = 0.7f;
    public static float RaidOpportunityGain = 0.5f;

    // He does not hunt on an empty stomach or half asleep: a real need must
    // always be able to outbid the hunt, and these are what guarantee it.
    //
    // Loosened from 0.6/0.3 after the soak: a LONE survivor is almost never
    // simultaneously fed, watered, rested and whole, so the conjunction of the
    // strict values left him available ~14% of the time and, with the auction
    // only re-running between interactions, he never once got to hunt in ten
    // game days. These still sit below the levels at which Eat/Drink/Sleep
    // outbid everything, so survival keeps its priority.
    public static float RaidSelfNeedCeiling = 0.6f;
    public static float RaidSelfEnergyFloor = 0.3f;

    // He does not chase forever: two plan rebuilds without leaving the junction
    // and the route clearly is not getting him anywhere (MobSystem precedent).
    public static float RaidStallGiveUpTicks = 60f;

    // He breaks off and runs once he has taken this much himself.
    public static float RaidFleeHealth = 0.55f;

    // She bolts at this much (matches §56 PredationFleeHealth).
    public static float RaidVictimFleeHealth = 0.6f;

    // Breather between raids, so he does not hammer the colony without pause.
    public static int RaidCooldownTicks = 1200;

    // --- The defence --------------------------------------------------------

    // Against an OUTSIDER the whole faction in radius rallies, with no affinity
    // gate. §29C.4B's friend-guard needs affinity >= 0.25, which the girls have
    // not built up in the opening days — without this "they fight back as one"
    // simply would not happen when it matters most.
    public static bool RallyIgnoresAffinityVsOutsider = true;

    // §125.4: как далеко она замечает чужого — её радиус восприятия.

    // Re-warn per (girl, outsider) at most this often — one ⚠️ per sighting.
    // Shorter than §62's 600 for a wolf: a man closes distance far faster than
    // a roaming dog, so 600 would leave her un-warned through half an approach.
    public static int StrangerCueCooldownTicks = 300;

    // Junctions within this many tiles of a hostile person cost extra. Wider
    // than the §62 wolf ring (2) — he is spotted further out and moves faster.
    public static int DangerRingTiles = 3;

    // Comfort GAIN for a witness when a hostile dies, instead of the grief the
    // death sweep gives a housemate. Winning must read as winning.
    public static float EnemyDeathRelief = 0.15f;
}

}
