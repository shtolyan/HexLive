using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Content
{

public sealed class WorldObjectState
{
    public ObjectId Id { get; set; }

    public string DefinitionId { get; set; } = string.Empty;

    public FragmentId Fragment { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public List<PointId> Points { get; } = new();

    public bool IsOccupied { get; set; }

    public EntityId? CurrentUser { get; set; }

    public float ResourceAmount { get; set; }
}

}
