using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

// Spec §54: meat left on the ground spoils. Raw rots fast, cooked lasts longer
// (cooking is preservation). Uses the SpawnTick-since-landing pattern (like
// coconut rot); carried meat is out of scope for v1 (assumed eaten/cooked in
// time). A carcass being butchered (IsOccupied) is left alone.
public sealed class MeatSpoilageSystem : ISimulationSystem
{
    public string Name => nameof(MeatSpoilageSystem);

    public TickLayer Layer => TickLayer.Slow;

    public ChunkPolicy ChunkPolicy => ChunkPolicy.PerChunk;

    private readonly System.Collections.Generic.List<ObjectId> _spoiled = new();

    public void Run(WorldState world)
    {
        _spoiled.Clear();
        foreach (var obj in world.Entities.Objects.Values)
        {
            // §156: порог якорный (Tick - SpawnTick), поэтому проспавшее мясо
            // испортится обычным кодом на первом бодром такте — фильтр это вся
            // правка.
            if (obj.SpawnTick <= 0 || obj.IsOccupied ||
                !ChunkMath.IsAwake(world, obj.Tile))
            {
                continue;
            }

            int spoilTicks;
            if (obj.DefinitionId == ContentIds.MeatRaw)
            {
                spoilTicks = SimBalance.MeatRawSpoilTicks;
            }
            else if (obj.DefinitionId == ContentIds.MeatCooked)
            {
                spoilTicks = SimBalance.MeatCookedSpoilTicks;
            }
            else if (obj.DefinitionId == ContentIds.PalmLeaf &&
                     SimBalance.PalmLeafWitherTicks > 0)
            {
                // Bug #338: лист — та же схема SpawnTick-на-земле, что и мясо.
                spoilTicks = SimBalance.PalmLeafWitherTicks;
            }
            else
            {
                continue;
            }

            if (world.Tick - obj.SpawnTick >= spoilTicks)
            {
                _spoiled.Add(obj.Id);
            }
        }

        foreach (var id in _spoiled)
        {
            WorldObjectMutations.DespawnObject(world, id);
            Trace.EmitSystem(world, "MeatSpoiled", $"Obj={id.Value} rotted on the ground");
        }
    }
}

}
