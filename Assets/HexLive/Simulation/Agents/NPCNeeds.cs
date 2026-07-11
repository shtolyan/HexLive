namespace HexLive.Simulation.Agents
{
public sealed class NPCNeeds
{
    public float Hunger { get; set; }

    // Spec 29E.1: higher = thirstier.
    public float Thirst { get; set; }

    public float Energy { get; set; }

    public float Comfort { get; set; }

    public float Social { get; set; }

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
    public float Blood { get; set; } = 1f;

    // Spec 40.3: first-aid stock, held apart from the general inventory (a
    // med pouch — it must not crowd food/materials out of the pack). Two to
    // start; auto-spent to dress a serious wound.
    public int Bandages { get; set; } = 2;

    // Spec 40.3: pills — the last-resort backup to the bandage. When a wound
    // is open yet no bandage has fired and HP has fallen near death, a pill is
    // spent to pull the body and HP back a step. One to start; kept in the med
    // pouch beside the bandages.
    public int Pills { get; set; } = 1;

    // Spec 40.7: tan (0 = pale, 1 = dark). Builds slowly from sun on bare
    // skin; the redness→tan look is painted from this on the skin texture
    // (presentation). Never fades to zero — a survivor stays weathered.
    public float TanLevel { get; set; }

    // Spec 40.13: stress (0 = calm, 1 = breaking point). Rises near danger,
    // in a fight, when badly hurt or starving; ebbs when safe and rested.
    // Overwhelming stress on an already-spent body can knock it out.
    public float Stress { get; set; }
}

}
