using HexLive.Simulation.Common;

namespace HexLive.Simulation.Runtime
{
/// <summary>§173 / #638: an exact planting point, independent of furniture hex claims.</summary>
public sealed class PlacePlantingSiteCommand : ISimulationCommand
{
    public PlacePlantingSiteCommand(string catalogId, TileCoord tile, JunctionId anchor)
    { CatalogId = catalogId; Tile = tile; Anchor = anchor; }
    public EntityId? TargetEntity => null;
    public string CatalogId { get; }
    public TileCoord Tile { get; }
    public JunctionId Anchor { get; }
}
}
