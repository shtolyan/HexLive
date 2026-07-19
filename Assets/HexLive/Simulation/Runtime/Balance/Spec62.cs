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

    // How far a girl notices a live hostile mob (dog aggro is 2 — four tiles
    // of decision room before its nose finds her).
    public static int SpotRadiusTiles = 6;

    // Re-warn per (girl, mob) at most this often: one ⚠️ per sighting, not
    // one per medium tick while the wolf hangs around.
    public static int CueCooldownTicks = 600;

    // "No significant wounds": EVERY body part at or above this — a single
    // mauled leg below 80% and she no longer picks the fight.
    public static float FitBoneHealth = 0.8f;

    // Attack-first only against a lone threat — the melee assessment already
    // bails at 2 adjacent attackers, so charging a pack would be a suicide run.
    public static int AttackMaxPack = 0;

    // Defend goal-lock length for the pre-emptive attack (help cry uses 240).
    public static int AttackLockTicks = 240;

    // Junctions within this many tiles of a live mob cost extra for an unfit
    // girl's routes (soft — a sealed map still routes through the ring).
    public static int DangerRingTiles = 2;

    // Extra per-step cost inside the ring (flat step = 10, swim = 40): pay up
    // to 9x to walk around the wolf rather than past its teeth.
    public static long DangerStepCost = 80L;
}

}
