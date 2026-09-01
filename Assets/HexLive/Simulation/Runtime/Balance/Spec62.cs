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

// §62: far threat detection — see the wolf before it sees you. Dogs aggro at
// 2 tiles; a girl SPOTS one at SpotRadiusTiles and reacts before contact: a ⚠️
// cue pops over her head, then she either attacks first (fit and armed, threat
// alone) or files the spot as danger and routes around it. Reactive melee and
// flee stay untouched — this layer only acts BEFORE the chase starts.
public static class Spec62
{
    public static bool ThreatAlertEnabled = true;

    // §125.4: «как далеко она замечает зверя» больше не константа — это её
    // собственный радиус восприятия (PerceptionMath.RadiusTiles).

    // Re-warn per (girl, mob) at most this often: one ⚠️ per sighting, not
    // one per medium tick while the wolf hangs around.
    public static int CueCooldownTicks = 600;

    // "No significant wounds": EVERY body part at or above this — a single
    // mauled leg below 80% and she no longer picks the fight.
    public static float FitBoneHealth = 0.8f;

    // Attack-first only against a lone threat — the melee assessment already
    // bails at 2 adjacent attackers, so charging a pack would be a suicide run.
    // `pack` counts the threat itself, so 1 = "charge a loner", 0 = first
    // strike DISABLED entirely (the 2026-07-20 balance audit turned it off).
    public static int AttackMaxPack = 0;

    // §62.2: does anybody ever attack first? While this is off, the §62.3
    // fit-fighter exemption from the danger ring must be off too — "she walks
    // wherever she likes because she would attack anyway" is a lie when
    // nobody attacks.
    public static bool FirstStrikeEnabled => AttackMaxPack >= 1;

    // Defend goal-lock length for the pre-emptive attack (help cry uses 240).
    public static int AttackLockTicks = 240;

    // Junctions within this many tiles of a live mob cost extra for an unfit
    // girl's routes (soft — a sealed map still routes through the ring).
    public static int DangerRingTiles = 2;

    // Extra per-step cost inside the ring (flat step = 10, swim = 40): pay up
    // to 9x to walk around the wolf rather than past its teeth.
    public static long DangerStepCost = 80L;

    // §62.7: the errand's DESTINATION sits inside a wolf's danger ring — the
    // trip is dropped and its goal cooled for this long, so she genuinely
    // changes her mind instead of detouring INTO the teeth. Matches
    // CueCooldownTicks: by the next possible sighting the defer has expired.
    public static int TargetDangerCooldownTicks = 600;
}

}
