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
}

}
