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

// Spec §49: sleep / social / water overhaul knobs. Static so the headless soak
// harness can bisect features deterministically, and so HexTuningConfig can push
// live slider values in the editor. Defaults = all features ON at design values.
public static class Spec49
{
    // Feature toggles (harness bisect).
    public static bool Rearm = true;         // sleep re-arm (kill empty get-ups)
    public static bool CoolRearm = true;     // spec 35.4: cool-off dwell re-arm (kill None→CoolOff spam)
    public static bool SleepComfort = true;  // unified sleep-comfort formula
    public static bool AmbientSocial = true; // passive proximity socialising
    public static bool SickDoT = true;       // delayed raw-water sickness

    // Spec 35.4: cool-off dwell — how long one "stay in the shade" beat lasts
    // before ShouldKeepCooling re-checks, and the max number of re-arms before a
    // fallback tile that never cools aborts the dwell (safety against a frozen NPC).
    public static int CoolOffDwellTicks = 40;
    public static int CoolOffMaxRearms = 6;

    // Talk tuning (longer, less rewarding).
    public static int TalkDuration = 90;
    public static float TalkInitGain = 0.20f;
    public static float TalkListenGain = 0.12f;

    // Ambient (passive proximity) social gain per slow tick.
    public static float AmbientGain = 0.012f;

    // A chat won't START once hunger/thirst reach this (in-flight talks finish).
    public static float SocializeNeedGate = 0.55f;

    // Tier A: unified sleep-comfort formula — comfort gained over a full night
    // of sleep by surface, plus a fireside bonus, minus sun/rain penalties.
    // "Night" here is the 75-slow-tick drip window of SleepComfortNightSlowTicks
    // (a per-slow-tick divisor, NOT the stretched visual night). A night's sleep
    // must clearly OUT-pace the ~0.75-per-150-slow-tick waking comfort drain,
    // or a bed feels pointless and comfort stays pinned at 0 (the coma spiral).
    // So a proper bed is a BIG comfort source: the leaf mat nearly fills the bar
    // in a night (net-positive even off the fire), the premium bedroll fills it
    // outright. Bare grass stays a pittance so building the bed matters.
    public static float SleepComfortGrassNight = 0.05f;
    public static float SleepComfortLeafNight = 0.85f;  // was 0.30 — a leaf bed now significantly raises comfort
    public static float SleepComfortBedNight = 1.40f;   // was 1.00 — premium bedroll fills comfort fully + margin
    // §49.8: a night's sleep beside a lit fire tops up ~5% comfort on its own —
    // the campfire's warmth reads as cosy even on bare grass.
    public static float SleepComfortFireBonusNight = 0.05f;
    public static float SleepComfortSunPenaltyNight = 0.15f;
    public static float SleepComfortRainPenaltyNight = 0.15f;
    // §49.8: awake by a lit fire is a touch comfier than trudging about — the
    // usual waking comfort drain (ComfortRate) reverses into a small gain, so
    // sitting fireside slowly restores comfort instead of bleeding it.
    public static float AwakeFireComfortGain = 0.003f;
    // A jacket/coat (torso-covering outer garment) bunched under the body pads
    // the bare ground a little — a touch more comfort than sleeping on plain
    // dirt. Only helps when there's no bed; a real mat/bed already dwarfs it.
    public static float SleepComfortJacketPadNight = 0.06f;

    // Tier C: pick a comfier sleep spot — shade on a hot day, fireside in the
    // cold — as a SMALL nudge that never overrides the home-anchor + indoor
    // safety (which historically stopped the colony bedding down in dog land).
    public static bool SmartSleepSpot = true;
    public static float SleepSpotFireWeight = 1.5f;
    public static float SleepSpotShadeWeight = 1.5f;

    // §113 r2: full ground-body footprint. Candidate count and placement are
    // structural HexPointLayout properties, not tuning knobs: every ground-lying
    // state always searches all 37 interior nodes and six headings.
    // 0.88 × 1.5 = 1.32 wu long; 0.24 × 1.5 = 0.36 wu wide.
    public static float LieBodyLengthFactor = 0.88f;
    public static float LieBodyWidthFactor = 0.24f;

    // Минимальный ФИЗИЧЕСКИЙ радиус вещи, помеченной Obstacle, но не назвавшей
    // габарита (валун и пальма закрывают только свой узел, ObstacleRadius у них
    // 0). Долями HexRadius: 0.30 × 1.5 = 0.45 wu. Вещи, у которых габарит есть
    // (кровать 1.39 wu), меряются своим; у костра ObstacleRadius — это ширина
    // угольного КОЛЬЦА, поэтому огню задан отдельный SolidRadius (§113).
    public static float LieSolidRadiusFloorFactor = 0.30f;

    // §65: dead-tired → seek a proper fireside sleep BEFORE the body collapses.
    // A body running on empty used to grind on until Energy hit zero and it
    // simply switched off (§60 dead-tired coma) wherever it stood — often at
    // the work site, out in dog country, cold. Once Energy falls under
    // DeadTiredEnergy she DROPS the chore and beds down at the campfire spot
    // (BuildGroundSleepPlan already anchors there) — the Sleep bid gets a
    // decisive DeadTiredSleepBoost, and a spent body TOLERATES moderate
    // hunger/thirst (up to the starving line) instead of being blocked by it.
    // NOTE the real collapse driver was stale DANGER memory, not hunger — see
    // SleepDangerRecencyTicks below; this energy-threshold seek is the smaller
    // half of §65. The wake side sleeps THROUGH to rested
    // (Energy >= SleepEnergyThreshold) but still wakes to eat at the starving
    // line, so she can't sleep her needs to a death. Spec §65.
    public static bool DeadTiredSeek = true;
    public static float DeadTiredEnergy = 0.15f;      // below this: drop work, bed down
    public static float DeadTiredSleepBoost = 0.30f;  // auction pull (cf. StarvingBoost 1.0)

    // Bug #25 / §49.9: the deliberate night bedtime. After 23:00 a body at/below
    // 25% energy stops ordinary work, prepares a sufficiently fuelled hearth,
    // and sleeps beside it. The intention stays armed through those separate
    // goals and through critical wake-ups until the energy bar is full.
    public static bool NightSleepSchedule = true;
    public static float NightSleepEnergy = 0.25f;
    public static float NightSleepBoost = 1.0f;
    public static float NightSleepWakeEnergy = 0.999f;

    // §65 (EXPERIMENTAL, default OFF): only a RECENTLY-seen threat forbids sleep.
    // The soak found the #1 work-site-collapse cause is a STALE danger memory:
    // a danger memory lingers a full day (DecisionSystem prune, 2400t) to steer
    // pathing / flee / arm-up, and ~60% of faints were NPCs kept awake by a wolf
    // that had wandered off ~a third of a day earlier (avg stale memory 875t old,
    // no mob within 6 tiles). Letting them sleep once the coast has been clear
    // for this many ticks slashes collapses (−45%) and more than doubles proper
    // fireside bed-downs. BUT it is COMBAT-DESTABILISING: sleeping during a lull
    // gets NPCs mauled by a returning wolf, and the death count is NON-MONOTONIC
    // in the window (20-seed soak: 900t→0 deaths, 1100t→5, legacy→3) — a classic
    // knife-edge-vs-dogs reshuffle (see spec §40 / dog-fragility notes). So it
    // ships OFF (0 = legacy "any remembered danger blocks sleep"); enabling it
    // needs a dedicated dog-balance re-soak to pick a window that is safe across
    // seeds, not just lucky on one. An actively-perceived wolf re-stamps its
    // danger tile every sighting (RememberDangerAt), so while it lingers the
    // memory stays "recent" and still blocks — the risk is the wolf that returns
    // AFTER the window lapses. Spec §65.
    public static int SleepDangerRecencyTicks = 0;

    // Tier C: when only mildly thirsty, prefer to set up / drink BOILED water
    // rather than gamble on raw (which the fire being dead 90% of the time makes
    // the default). Only urgent thirst reaches for raw.
    public static bool ProactiveBoil = true;
    public static float BoilThirstCeiling = 0.6f; // above this, raw is fine (urgent)
    public static float BoilChainWeight = 0.1f;   // fire/tool-chain push while boiling. 0.1 = safe (10W/0L/3d); raise toward 0.3 for more boiled water at a survival cost (0.2→13% boiled/3L, 0.3→20%/8W)

    // §49.7: soggy REAL garments (pants/vest — not bra/panties/bikini) drag on
    // the move; a soaked body is a little less comfortable (wet underwear too,
    // it just doesn't slow you). Drag is x(PerGarment) per wet non-underwear
    // item, floored.
    public static float WetDragPerGarment = 0.9f;
    public static float WetDragFloor = 0.8f;
    public static float WetComfortPenalty = 0.004f; // per slow tick while soaked

    // Tier B: shade is a genuinely cooler spot (35° sun → ~28° shade) — was -2.
    public static float ShadeCooling = -7f;
    // Tier B: a sleeping body's cold/heat accrues AND bites this much slower —
    // the Sims-style "needs slow while asleep", so a night's sleep doesn't
    // freeze her (pairs with re-arm, which lets her sleep THROUGH mild cold).
    public static float ThermalSleepFactor = 0.5f;
}

}
