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
    // (grass 0.05, +fire 0.05, a bed ~1.0 minus sun/rain → ~0.70.)
    public static float SleepComfortGrassNight = 0.05f;
    public static float SleepComfortLeafNight = 0.30f;
    public static float SleepComfortBedNight = 1.00f;
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
