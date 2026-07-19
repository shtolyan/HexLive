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

internal static class DamageReactionSystemHelpers
{
    public static void GrantAdrenaline(WorldState world, NPCState npc, float damage, string reason)
    {
        if (world == null || npc == null || npc.Health <= 0f || damage <= 0f || SimBalance.AdrenalineTicks <= 0)
        {
            return;
        }

        ApplyAdrenalineEnergyFloor(npc);

        var until = world.Tick + SimBalance.AdrenalineTicks;
        if (until <= npc.Mind.AdrenalineUntilTick)
        {
            return;
        }

        npc.Mind.AdrenalineUntilTick = until;
        Trace.Emit(world, npc.Id, "Adrenaline",
            $"{reason} Damage={damage:F3} Until={npc.Mind.AdrenalineUntilTick}");
    }

    public static bool IsAdrenalineActive(WorldState world, NPCState npc) =>
        world != null && npc != null && world.Tick < npc.Mind.AdrenalineUntilTick;

    public static void ApplyAdrenalineEnergyFloor(NPCState npc)
    {
        if (npc == null || SimBalance.AdrenalineEnergyFloor <= 0f)
        {
            return;
        }

        npc.Needs.Energy = System.Math.Max(npc.Needs.Energy, SimBalance.AdrenalineEnergyFloor);
    }
}

}
