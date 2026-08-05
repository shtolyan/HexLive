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

// Spec §53: compassion & mutual aid. A girl with a full belly and no fire to
// tend will walk over to a starving / wounded / sick / grieving housemate and
// help — feed, dress a wound, hand a pill, or console — which lifts BOTH
// relationships. The pull scales with the sufferer's plight and with this
// girl's personality CompassionTrait, but is gated hard behind her own
// survival: if SHE is starving or bleeding she looks after herself first.
// §53.7: helping now COSTS SUPPLIES — a meal, a gulp of water, a bandage, a
// pill leave the helper's own pack — and a girl who means to help but has
// nothing to give first goes and fetches it (the aid errand).
// All balance lives here so the headless harness can bisect and the
// HexTuningConfig sliders can drive it.
public static class Spec53
{
    public static bool Enabled = true;

    // Compassion need (NPCNeeds.Compassion): drains per slow tick by
    // CompassionRate × (nearby suffering) × CompassionTrait; recovers toward
    // full by RecoverRate when no one nearby is hurting.
    public static float CompassionRate = 0.02f;
    public static float RecoverRate = 0.01f;

    // Aid bid = base + suffering × CompassionTrait × AidWeight
    //                 + (1 − Compassion) × PressureWeight.
    // AidWeight 0.85 lets a high-trait girl (≈1.0) facing a dying housemate
    // (suffering≈1) bid ≈0.95 — over CraftBed's 0.7 and the 0.15 switch margin —
    // while a reserved girl (≈0.35) bids ≈0.4 and only helps when otherwise idle.
    // Her own StarvingBoost (1.0) still outranks aid: self-preservation wins.
    public static float AidWeight = 0.85f;
    public static float PressureWeight = 0.2f;

    // Self-survival gate — she will NOT set out to help while her own body is
    // in the red: hunger at/above this, health below this, actively fighting,
    // fleeing, or already flagged starving/dehydrated.
    public static float SelfHungerGate = 0.6f;
    public static float SelfHealthGate = 0.5f;

    // A neighbour must be suffering at least this much (0..1) to be worth a trip.
    public static float SufferingThreshold = 0.3f;

    // §53.7: aid SPENDS the helper's own supplies — a meal / a water charge /
    // a bandage / a pill. Off = the pre-§53.7 free relief, byte-for-byte (and
    // no aid errands: nobody ever fetches on someone else's behalf).
    public static bool AidCostsSupplies = true;

    // §53.7: how long an aid errand (fetching the missing food/water/herb for
    // a housemate) keeps pulling once started, even if the sufferer drops out
    // of sight while she walks. Expiry just lets the auction re-decide.
    public static int AidErrandTicks = 1200;

    // §53.7: the share of the FULL aid bid (suffering × trait × AidWeight +
    // compassion pressure + bleed-out emergency) that flows into the supply
    // chore. Just under 1: fetching is a notch less urgent than the helping
    // itself, but it must stay in the same weight class — shade it much lower
    // and the errand loses to the very chores the aid used to outbid, which
    // reads as "she saw you dying and went back to hauling logs".
    public static float AidErrandBidShare = 0.9f;

    // Relief applied to the TARGET on a completed aid (§53.7: the matching
    // supply is spent from the helper; a fed meal's own nutrition wins over
    // FeedRelief when the item defines one):
    public static float FeedRelief = 0.5f;          // target Hunger down
    public static float HydrateRelief = 0.5f;       // target Thirst down
    public static float TreatHeal = 0.15f;          // wounded body parts up

    // §105 r3: ⭐ ПОТОЛОК ЛЕЧЕНИЯ — до какого уровня зоны вообще может довести
    // перевязка В ЭТИХ РУКАХ. Без него сиделка с нулевым навыком вылечивала
    // соседку до 100%, просто садясь рядом и повторяя: бинт поднимал зоны
    // безостановочно, и «спасли с того света» превращалось в «залечили начисто
    // за пару минут».
    //
    // Число выбрано ПОРОГОМ ПОДЪЁМА, а не на глаз. Встать можно с груди выше
    // Spec105.VitalExitHealth (0.15), а Medicine у всех стартует с нуля — так
    // что потолок новичка ОБЯЗАН быть заметно выше 0.15, иначе спасение
    // физически недостижимо в начале игры и вся §105 умирает не родившись.
    // 0.35 даёт запас: она встаёт и держит ещё три-четыре укуса волка —
    // «залатали, но не вылечили».
    //
    // Собственное заживление тела этим НЕ ограничено: за дни она дойдёт до
    // единицы сама. Потолок — про то, что умеют руки, а не про то, на что
    // способно тело.
    public static float TreatCapNovice = 0.35f;
    public static float TreatBlood = 0.2f;          // target Blood up
    public static float MedicateHeal = 0.1f;        // target Health up (+ sickness cleared)
    public static float ConsoleStressRelief = 0.3f; // target Stress down (+ grief eased)

    // §110: на сколько тиков одно завершённое утешение укорачивает плач
    // (CryingUntilTick) — с подругой рядом она выплакивается быстрее.
    public static int ConsoleCryingReliefTicks = 120;

    // §110: утешала на коленях — столько тиков стоит после, пока играет
    // вставание. Держать РАВНЫМ длине клипа PrayUp (6.9 с ≈ 28 тиков при 4/с):
    // меньше — она уедет пешком в позе молитвы, больше — просто постоит зря.
    public static int ConsoleStandUpTicks = 28;

    // Relationship gain on BOTH sides of a completed aid — deliberately larger
    // than a chat: kindness under hardship bonds hard.
    public static float AidRelationshipGain = 0.18f;

    // How long the aid interaction runs (ticks), mirroring a talk.
    public static int AidDuration = 70;

    // How much of her own Compassion a completed aid restores.
    public static float AidSelfRestore = 0.4f;

    // Personality spread: CompassionTrait is seeded in [TraitMin, TraitMax].
    public static float TraitMin = 0.35f;
    public static float TraitMax = 1.0f;

    // ---- §68: self first-aid -------------------------------------------------
    // Aid(Treat) let a HOUSEMATE dress your wounds; the wounded girl herself had
    // no goal for it. Her own first aid was a passive last resort in
    // NeedsDecaySystem, gated on blood < BandageBloodThreshold AND a single part
    // below 0.4 — so a mauling of many shallow bites (save 604905660: Marta at
    // HP 0.61, worst zone 0.55, blood 0.48, TWO bandages in the pack) never
    // opened it, and the auction handed her evening to laundry. This is the
    // conscious goal: burden crosses the line → she stops and patches herself up.
    public static bool SelfTreatEnabled = true;

    // Burden = max(1 − Health, 1 − worst intact zone, 1 − Blood). Reading the
    // WHOLE body (not just the worst zone) is the point: many shallow wounds
    // are what the old gate was blind to.
    public static float SelfTreatBurdenThreshold = 0.25f;

    // The LAST dressing is emergency stock: spending it on a moderate mauling
    // leaves nothing for the bleed-out that follows the next dog. With one
    // bandage left she waits for this (higher) burden.
    public static float SelfTreatLastBandageBurden = 0.45f;

    // Bid = SelfTreatBase + burden × SelfTreatWeight (+ the bleed emergency).
    // At burden 0.5 that is ≈0.95 — over laundry/leisure, under a real
    // hunger/thirst emergency, which is the intended pecking order.
    public static float SelfTreatBase = 0.35f;
    public static float SelfTreatWeight = 1.0f;

    // Losing blood is the emergency band: below SelfTreatBleedBlood the bid
    // takes SelfTreatBleedEmergency on top, so patching up outranks every chore
    // (the §63 "no spa while bleeding out" intent, but with somewhere to go).
    public static float SelfTreatBleedBlood = 0.6f;
    public static float SelfTreatBleedEmergency = 0.6f;

    // Winding a dressing takes about as long as tending someone else.
    public static int SelfTreatDuration = 60;

    // Relief from one self-applied dressing. Deliberately a notch under the
    // TreatHeal a housemate delivers — another pair of hands does it better.
    public static float SelfTreatHeal = 0.12f;
    public static float SelfTreatBlood = 0.18f;
}

}
