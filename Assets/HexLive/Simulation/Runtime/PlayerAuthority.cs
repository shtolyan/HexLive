using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{
    /// <summary>§123: the single simulation-side ownership gate.</summary>
    public static class PlayerAuthority
    {
        public static bool CanControl(WorldState world, EntityId id, out NPCState npc)
        {
            if (world.Entities.Npcs.TryGetValue(id, out npc) &&
                npc.Faction == Faction.Colony)
            {
                return true;
            }

            npc = null;
            return false;
        }

        public static bool CanMutateInventory(WorldState world, EntityId id, out NPCState npc) =>
            CanControl(world, id, out npc);
    }
}
