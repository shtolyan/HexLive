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

    // Spec 40.3: first-aid stock, held apart from the general inventory (a
    // med pouch — it must not crowd food/materials out of the pack). Two to
    // start; auto-spent to dress a serious wound.
    public int Bandages { get; set; } = 2;

    // Spec 44: of the bandages in the pouch, how many are HERBAL — crafted from
    // 2 gathered plantain leaves at the fire — as opposed to the two pre-made
    // medkit bandages every girl starts with (spec 40.3). Only a herbal dressing
    // shows the leaf-wrap decal; medkit bandages patch the wound with no leaf
    // visual (you never gathered them). Medkit bandages are spent first, so the
    // plantain wrap appears only once she has actually gone out and gathered.
    public int HerbalBandages { get; set; } = 0;

    // Spec 40.3: pills — the last-resort backup to the bandage. When a wound
    // is open yet no bandage has fired and HP has fallen near death, a pill is
    // spent to pull the body and HP back a step. One to start; kept in the med
    // pouch beside the bandages.
    public int Pills { get; set; } = 1;

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
