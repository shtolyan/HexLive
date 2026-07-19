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

// Dev-scene seam (WolfFightTest etc.): redress an NPC to an EXACT outfit and
// rerun the equipment math. EquipmentMath itself stays internal — test scenes
// live in the presentation assembly and can't reach it directly.
public static class WardrobeDebugHelpers
{
    public static void Redress(WorldState world, NPCState npc, params string[] garmentIds)
    {
        npc.WornItems.Clear();
        foreach (var id in garmentIds)
        {
            npc.WornItems.Add(id);
        }

        EquipmentMath.Recalculate(world, npc);
    }
}

}
