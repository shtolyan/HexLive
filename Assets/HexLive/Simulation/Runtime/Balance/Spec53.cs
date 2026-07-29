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
// Helping costs no items (the relief is applied straight to the target) so it
// can never bankrupt the knife-edge colony. All balance lives here so the
// headless harness can bisect and the HexTuningConfig sliders can drive it.
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

    // Relief applied to the TARGET on a completed aid (no item is spent):
    public static float FeedRelief = 0.5f;          // target Hunger down
    public static float HydrateRelief = 0.5f;       // target Thirst down
    public static float TreatHeal = 0.15f;          // wounded body parts up
    public static float TreatBlood = 0.2f;          // target Blood up
    public static float MedicateHeal = 0.1f;        // target Health up (+ sickness cleared)
    public static float ConsoleStressRelief = 0.3f; // target Stress down (+ grief eased)

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
}

}
