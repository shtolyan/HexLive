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
}

}
