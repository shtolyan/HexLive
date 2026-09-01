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

// Spec §50: a prepared amputation hazard — a reef, a bear-trap, a set spot on
// the map an author places. A survivor standing on a tile holding a "Hazard"
// object loses a leg (deterministically at chance 1, or by a tuned roll). Like
// a predator, it's a fixed dangerous place rather than an emergent bite.
public sealed class HazardSystem : ISimulationSystem
{
    public string Name => nameof(HazardSystem);

    public TickLayer Layer => TickLayer.Slow;

    public ChunkPolicy ChunkPolicy => ChunkPolicy.NpcDriven;

    public void Run(WorldState world)
    {
        if (!Spec50.Enabled)
        {
            return;
        }

        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health <= 0f ||
                !world.Caches.ObjectsByTile.TryGetValue(npc.Tile, out var objects))
            {
                continue;
            }

            var onHazard = false;
            foreach (var objId in objects)
            {
                if (world.Entities.Objects.TryGetValue(objId, out var obj) &&
                    world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def) &&
                    def.HasTag("Hazard"))
                {
                    onHazard = true;
                    break;
                }
            }

            if (!onHazard)
            {
                continue;
            }

            // Take a leg that's still attached (right first, then left).
            BodyPart? leg =
                !npc.Body.IsSevered(BodyPart.LegR) ? BodyPart.LegR
                : !npc.Body.IsSevered(BodyPart.LegL) ? BodyPart.LegL
                : (BodyPart?)null;

            if (leg is { } target &&
                MathUtil.Hash01(world.Seed, world.Tick, npc.Id.Value, 851) < Spec50.HazardSeverChance)
            {
                AmputateSystemHelpers.Sever(world, npc, target);
            }
        }
    }
}

}
