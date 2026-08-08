using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Runtime
{

// Spec §75: a stable personal taste for each NPC × item definition pair.
// It is derived rather than saved: the same character always gets the same
// value, old saves need no migration, and identical instances share a taste.
public static class ItemAffinity
{
    public static float For(int npcId, string definitionId)
    {
        unchecked
        {
            var itemHash = 17;
            if (definitionId != null)
            {
                foreach (var ch in definitionId)
                {
                    itemHash = itemHash * 31 + ch;
                }
            }

            return MathUtil.Hash01(npcId, itemHash, 7501);
        }
    }

    public static string FavoriteWeapon(int npcId, IReadOnlyList<ItemInstance> items)
    {
        string favorite = null;
        var bestAffinity = -1f;
        var bestPriority = -1;
        foreach (var item in items)
        {
            var gear = GearCatalog.For(item.DefinitionId);
            if (gear.MeleePriority <= 0)
            {
                continue;
            }

            var affinity = For(npcId, item.DefinitionId);
            if (affinity > bestAffinity ||
                (System.MathF.Abs(affinity - bestAffinity) < 0.0001f &&
                 gear.MeleePriority > bestPriority))
            {
                favorite = item.DefinitionId;
                bestAffinity = affinity;
                bestPriority = gear.MeleePriority;
            }
        }

        return favorite;
    }

    public static string FavoriteWeapon(int npcId, IReadOnlyList<string> definitionIds)
    {
        string favorite = null;
        var bestAffinity = -1f;
        var bestPriority = -1;
        foreach (var definitionId in definitionIds)
        {
            var gear = GearCatalog.For(definitionId);
            if (gear.MeleePriority <= 0)
            {
                continue;
            }

            var affinity = For(npcId, definitionId);
            if (affinity > bestAffinity ||
                (System.MathF.Abs(affinity - bestAffinity) < 0.0001f &&
                 gear.MeleePriority > bestPriority))
            {
                favorite = definitionId;
                bestAffinity = affinity;
                bestPriority = gear.MeleePriority;
            }
        }

        return favorite;
    }
}

}
