using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Core
{

public sealed class RuntimeCaches
{
    public Dictionary<TileCoord, List<EntityId>> EntitiesByTile { get; } = new();

    public Dictionary<FragmentId, List<EntityId>> EntitiesByFragment { get; } = new();

    public Dictionary<TileCoord, List<ObjectId>> ObjectsByTile { get; } = new();
}

}
