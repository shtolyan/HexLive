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

    // §75A: любимое оружие — сначала СТАТЫ, потом вкус. MeleePriority уже
    // ранжирует железо по реальной силе (мачете 35 > копьё 30 > топор 20 >
    // нож 10), так что между мачете и ножом всегда мачете; симпатия решает
    // только между двумя экземплярами одного класса.
    public static string FavoriteWeapon(int npcId, IReadOnlyList<ItemInstance> items)
    {
        string favorite = null;
        var bestAffinity = -1f;
        var bestPriority = -1;
        foreach (var item in items)
        {
            ConsiderFavorite(npcId, item.DefinitionId, ref favorite, ref bestAffinity, ref bestPriority);
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
            ConsiderFavorite(npcId, definitionId, ref favorite, ref bestAffinity, ref bestPriority);
        }

        return favorite;
    }

    private static void ConsiderFavorite(int npcId, string definitionId,
        ref string favorite, ref float bestAffinity, ref int bestPriority)
    {
        var gear = GearCatalog.For(definitionId);
        if (gear.MeleePriority <= 0)
        {
            return;
        }

        var affinity = For(npcId, definitionId);
        if (gear.MeleePriority > bestPriority ||
            (gear.MeleePriority == bestPriority && affinity > bestAffinity))
        {
            favorite = definitionId;
            bestAffinity = affinity;
            bestPriority = gear.MeleePriority;
        }
    }
}

}
