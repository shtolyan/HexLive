namespace HexLive.Simulation.Debug
{

public sealed class ArchitectureElementSnapshot
{
    public int ElementId { get; set; }
    public string DefinitionId { get; set; } = string.Empty;
    public string SlotKey { get; set; } = string.Empty;
    public int SlotIndex { get; set; }
    public int Layer { get; set; }
    public float LocalX { get; set; }
    public float LocalZ { get; set; }
    public float LocalYaw { get; set; }
    public int RequiredSticks { get; set; }
    public int RequiredBoards { get; set; }
    public int RequiredRope { get; set; }
    public int RequiredLeaves { get; set; }
    public int DeliveredSticks { get; set; }
    public int DeliveredBoards { get; set; }
    public int DeliveredRope { get; set; }
    public int DeliveredLeaves { get; set; }
    public bool Buildable { get; set; }
    public int WorkRequired { get; set; }
    public int WorkDone { get; set; }
    public bool DemolitionPlanned { get; set; }
    public string ReplacementDefinitionId { get; set; } = string.Empty;
    public int RequiredTotal => RequiredSticks + RequiredBoards + RequiredRope + RequiredLeaves;
    public int DeliveredTotal => DeliveredSticks + DeliveredBoards + DeliveredRope + DeliveredLeaves;
    public float Progress => RequiredTotal <= 0
        ? (WorkDone >= WorkRequired ? 1f : 0f)
        : System.Math.Min(1f, DeliveredTotal / (float)RequiredTotal);
    public bool Complete => Buildable && DeliveredSticks >= RequiredSticks &&
        DeliveredBoards >= RequiredBoards && DeliveredRope >= RequiredRope &&
        DeliveredLeaves >= RequiredLeaves && WorkDone >= WorkRequired;
}

}
