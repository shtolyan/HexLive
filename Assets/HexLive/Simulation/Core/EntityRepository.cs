using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Core
{

public sealed class EntityRepository
{
    public Dictionary<EntityId, NPCState> Npcs { get; } = new();

    public Dictionary<ObjectId, WorldObjectState> Objects { get; } = new();
}

}
