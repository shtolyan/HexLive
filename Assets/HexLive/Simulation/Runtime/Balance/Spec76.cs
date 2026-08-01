namespace HexLive.Simulation.Runtime
{

// Spec §76: character attributes & skills. Until now every colonist was
// mechanically identical — §74 gave them different bodies, faces and voices,
// §75 a variable roster, but the NUMBERS were the same girl three times over.
//
// Two layers:
//  - ATTRIBUTES (6): innate, rolled from the seed at spawn, fixed for life.
//  - SKILLS (8): learned by doing, growing with diminishing returns.
//
// All balance lives here so the headless harness can bisect it and the
// AttributesBalance config asset can drive it (§59).
//
// ⭐ AttributeSpread = 0 IS THE KILL SWITCH, and it is stronger than a bool:
// at spread 0 every girl is exactly AttributeMean, every multiplier in
// AttributeMath is exactly 1.0f, and every float multiply is EXACT — so the
// new code stays on the hot path and a soak proves the plumbing rather than
// bypassing it. Verified: 240k ticks × 6 seeds, Enabled=false vs Spread=0,
// tick-identical. Drop it back to 0 to bisect anything §76 is suspected of.
//
// It now SHIPS at 0.5 (the full 0..10 display range) because identical
// colonists were the thing §76 exists to fix. The §76.7 balance ladder has
// still not been run — the numbers are an intention, not a measurement.
public static class Spec76
{
    // Master switch: the roll, the XP accrual and the UI tab. Coarse — prefer
    // AttributeSpread = 0 when you want a provable no-op with the code live.
    public static bool Enabled = true;

    // Split from Enabled on purpose (the §72 Enabled/OutsiderCount precedent):
    // a soak must be able to bisect "attributes moved it" from "skills did".
    public static bool SkillsEnabled = true;

    // ---- The roll -----------------------------------------------------------

    // The human average. Every multiplier is expressed as a deviation FROM
    // this, so a body sitting exactly here is the pre-§76 game.
    public static float AttributeMean = 0.5f;

    // Half-width of the band. Attributes land in [Mean − Spread, Mean + Spread]
    // EXACTLY (AttributeMath.Roll normalises, so Clamp01 never bites and the
    // budget is exact rather than approximate).
    // Must stay ≤ min(Mean, 1 − Mean) or the band would clip.
    //
    // 0.5 = the full 0..10 display range: every colonist reads as a distinct
    // person at a glance. Note the numbers look more dramatic than they play —
    // a "0/10" is a 15% penalty, not a broken limb (see the *Gain knobs).
    // Set to 0 for the provable no-op described above.
    public static float AttributeSpread = 0.5f;

    // ---- Per-effect gains ---------------------------------------------------
    // Every multiplier reads `1 + (attr − Mean) × Gain`, so at Spread 0.5 a
    // Gain of 0.3 is the ±15% band the design targets. Toughness inverts the
    // sign inside the helper (more grit = LESS damage taken).

    public static float MeleeDamageGain = 0.3f;
    public static float IncomingDamageGain = 0.3f;
    public static float MoveSpeedGain = 0.3f;
    public static float TurnSpeedGain = 0.3f;
    public static float AttackCooldownGain = 0.3f;
    public static float StaminaCeilingGain = 0.3f;
    public static float StaminaDrainGain = 0.3f;
    public static float BreathGain = 0.3f;
    public static float MetabolismGain = 0.3f;
    public static float ThermalToleranceGain = 0.3f;
    public static float HealRateGain = 0.3f;
    public static float BleedGain = 0.3f;
    public static float WorkSpeedGain = 0.3f;

    // Carry capacity is an INT (slots), so it is a band and not a curve: above
    // this much Strength she gets one extra slot, and that is the whole story.
    public static float CarrySlotThreshold = 0.85f;

    // ---- Skills -------------------------------------------------------------

    // XP per tick of completed work. At 0.0004 a 60-tick palm-fell is 0.024 of
    // a skill — roughly forty fellings to the halfway mark before diminishing
    // returns bite, which is a colony-lifetime arc rather than a week.
    public static float SkillXpPerWorkTick = 0.0004f;

    // A landed melee blow is worth about ten ticks of ordinary labour: fights
    // are rare and short, so they must pay better per event or Combat never
    // moves at all.
    public static float SkillXpPerHit = 0.004f;

    // Wits scales learning: `1 + (Wits − Mean) × this`.
    public static float SkillLearnWitsGain = 0.8f;

    // Diminishing returns: gain ∝ (1 − skill)^exp. Squared means the last
    // quarter of a skill costs about as much as the first three.
    public static float SkillDiminishExp = 2f;

    // What a maxed skill is worth. Duration ×(1 − skill × this); damage and
    // healing ×(1 + skill × this).
    public static float SkillWorkSpeedGain = 0.35f;
    public static float SkillDamageGain = 0.4f;
    public static float SkillHealGain = 0.5f;
    public static float SkillSocialGain = 0.4f;

    // Trace SkillUp only when a skill crosses a band edge, so a 240k-tick soak
    // gets ~10 events per girl instead of ~10000. 0.1 = one per displayed level.
    public static float SkillTraceBand = 0.1f;

    // ---- Perks --------------------------------------------------------------
    // An attribute in the top band earns a named badge, in the bottom band a
    // named flaw. These are NOT EffectKinds: all 33 effects are transient
    // classifications and the panel draws a chip per entry, so twelve permanent
    // chips would swamp the row. The §76 tab IS their visibility surface, which
    // is the §48.6 "no invisible influences" rule satisfied by a character
    // sheet rather than by the chip row.
    public static float PerkHighBand = 0.70f;
    public static float PerkLowBand = 0.30f;
}

}
