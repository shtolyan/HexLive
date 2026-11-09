using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{
/// <summary>#617: the camp keeps two authored ground stacks, not endless firewood.</summary>
internal static class CampStickStock
{
    internal static int Limit => 2 * GroundPileCatalog.Capacity(ContentIds.Stick);

    internal static int Count(WorldState world, NPCState npc)
    {
        var count = 0;
        if (ColonyQueries.Home(world, npc.Faction) is { } home)
        {
            var radius = Spec72.MaxCampRadiusTiles;
            for (var q = -radius; q <= radius; q++)
            for (var r = System.Math.Max(-radius, -q - radius);
                 r <= System.Math.Min(radius, -q + radius); r++)
            {
                var tile = new TileCoord(home.Q + q, home.R + r);
                if (!ColonyQueries.InCamp(world, tile, npc.Faction) ||
                    !world.Caches.ObjectsByTile.TryGetValue(tile, out var ids)) continue;
                foreach (var id in ids)
                    if (world.Entities.Objects.TryGetValue(id, out var item) &&
                        item.DefinitionId == ContentIds.Stick) count++;
            }
        }
        foreach (var resident in world.Entities.Npcs.Values)
            if (resident.Faction == npc.Faction)
                foreach (var item in resident.Inventory.Items)
                    if (item.DefinitionId == ContentIds.Stick) count++;
        return count;
    }

    internal static bool CanProduce(WorldState world, NPCState npc, int yield)
    {
        var promised = 0;
        foreach (var worker in world.Entities.Npcs.Values)
            if (worker.Id != npc.Id && worker.Faction == npc.Faction &&
                ((worker.Plan.Status == PlanStatus.Active && worker.Plan.Goal == GoalType.SplitLog) ||
                 worker.Mind.CurrentGoal == GoalType.SplitLog) &&
                !(GearCatalog.HasCapability(worker.Inventory.Items, GearCapability.Saw) &&
                  DecisionSystem.WoodenProstheticBoardShortfall(world, worker) > 0))
                promised += yield;
        return Count(world, npc) + promised + yield <= Limit;
    }
}
}
