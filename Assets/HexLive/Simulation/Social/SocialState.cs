namespace HexLive.Simulation.Social
{
public sealed class SocialState
{
    public float Trust { get; set; }

    public float Embarrassment { get; set; }

    public float Presence { get; set; }

    public float Hearing { get; set; }
}

public sealed class RelationshipSummary
{
    public float Trust { get; set; }

    public float Familiarity { get; set; }
}

public sealed class SocialSystem
{
}

}
