namespace HexLive.Simulation.Agents
{
public sealed class NPCNeeds
{
    public float Hunger { get; set; }

    // Spec 29E.1: higher = thirstier.
    public float Thirst { get; set; }

    // Spec §60: at 0 (awake) the body switches off into an exhaustion coma —
    // it lies as if dead, recovering at sleep pace, and wakes at 15%.
    public float Energy { get; set; }

    public float Comfort { get; set; }

    public float Social { get; set; }

    // Spec §53: compassion (1 = at peace, 0 = wrung out). Mirrors the Social
    // convention (higher = better). It is SPENT when nearby housemates suffer
    // and go un-helped — the more (and worse) the suffering around her, the
    // faster it drains — and it is restored by helping someone or when no one
    // near her is hurting. Low compassion adds pressure to the Aid goal so a
    // caring girl eventually breaks off her own chores to tend the wounded.
    // Starts full. The per-NPC WEIGHT of this drive is NPCState.CompassionTrait.
    public float Compassion { get; set; } = 1f;

    public float ThermalDiscomfort { get; set; }

    // Spec 29C.10: signed thermal comfort for the UI — 0 = ideal ("chocolate"),
    // negative = too cold, positive = too hot. Both extremes drain HP. This is
    // the instantaneous reading from the effective temperature; the unsigned
    // ThermalDiscomfort above stays the accumulating NEED the decision layer
    // scores (Dress/CoolOff), driven by direction from the effective temp.
    public float ThermalComfort { get; set; }

    // Spec 40.1: stamina (0..1). The energy to DO things — spent on work,
    // recovered by rest and food. Its ceiling is set by how fed/rested/
    // comfortable the body is (you can't be spry on an empty stomach). Low
    // stamina pulls the NPC toward sitting/lying down. Starts full.
    public float Stamina { get; set; } = 1f;

    // §71: BREATH (0..1) — the sprint reserve, and ONLY that. Spent while
    // running, refilled while walking (faster still while standing). At zero
    // she is forced back to a walk until it re-arms.
    // DELIBERATELY separate from Stamina: Stamina feeds the Sit bid, so hanging
    // the sprint cost on it would send a girl who just ran to help a housemate
    // straight to sitting down. NOTHING in the decision layer may read Breath —
    // it governs gait, never goals. Starts full.
    public float Breath { get; set; } = 1f;

    // Spec 40.6: hygiene (1 = clean, 0 = filthy). Decays slowly with living;
    // rises while at the water's edge (bathing/washing). A visible survivor
    // param — grubbier the longer since a wash. Starts clean.
    public float Hygiene { get; set; } = 1f;

    // Spec 40.2: blood (1 = full). A badly wounded part (< 0.4) bleeds — blood
    // ebbs away; it refills slowly while fed and rested. At zero the NPC dies
    // of blood loss. Bandages stop the bleed and speed the refill.
    // Spec §60: below 5% the body drops into a blood-loss coma first — the
    // last chance before the death line, if the bleed can be outlasted.
    public float Blood { get; set; } = 1f;

    // Legacy blob field: v41 and older stored first aid outside Inventory.
    // Current worlds keep physical, stackable item.bandage instances;
    // WorldSaveSerializer materializes this count once when loading old data.
    public int Bandages { get; set; }

    // Legacy companion to Bandages: number of old pouch entries that were
    // herbal. Current instances persist provenance in ResourceAmount.
    public int HerbalBandages { get; set; }

    // Legacy blob field for pre-physical pills. Current item.pill instances
    // live in Inventory and this value is materialized once on old-save load.
    public int Pills { get; set; }

    // Spec 40.7: tan (0 = pale, 1 = dark). Builds slowly from sun on bare
    // skin; the redness→tan look is painted from this on the skin texture
    // (presentation). Never fades to zero — a survivor stays weathered.
    public float TanLevel { get; set; }

    // Spec 40.7: sunburn — the ACUTE redness on bare skin (0 = none, 1 = raw).
    // Rises fast under strong sun on uncovered parts, then heals when out of
    // the sun; as it heals a fraction settles into permanent TanLevel (burn
    // browns into tan). Purely cosmetic — the skin painter reads it as the red
    // channel over the tan; it gates nothing and changes no survival outcome.
    public float Sunburn { get; set; }

    // Spec 40.13: stress (0 = calm, 1 = breaking point). Rises near danger,
    // in a fight, when badly hurt or starving; ebbs when safe and rested.
    // Overwhelming stress on an already-spent body can knock it out.
    public float Stress { get; set; }
}

}
