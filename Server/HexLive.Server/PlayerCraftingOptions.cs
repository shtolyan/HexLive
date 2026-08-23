using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;

namespace HexLive.Server
{

/// <summary>§138/§149: captures the authoritative crafting read model only
/// for characters permanently assigned to this viewer.</summary>
public static class PlayerCraftingOptions
{
    public static CraftingOptionsSnapshot Capture(
        WorldState world, IEnumerable<int> assignedNpcIds)
    {
        var result = new CraftingOptionsSnapshot();
        var ids = new List<int>();
        var unique = new HashSet<int>();
        foreach (var npcId in assignedNpcIds)
        {
            if (npcId > 0 && unique.Add(npcId))
            {
                ids.Add(npcId);
            }
        }
        ids.Sort();

        for (var i = 0; i < ids.Count; i++)
        {
            var npc = new NpcCraftingOptionsSnapshot { NpcId = ids[i] };
            if (CraftingOptions.TryFill(world, new EntityId(ids[i]), npc.Options))
            {
                result.Npcs.Add(npc);
            }
        }

        return result;
    }
}

}
