using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Common;
using HexLive.Simulation.Navigation;

namespace HexLive.Simulation.Runtime
{
public sealed partial class PlanningSystem
{
    private static bool TryBuildGardenPlan(WorldState world, NPCState npc)
    {
        if (!GardenMath.TryChooseTask(world, npc, out var task)) return false;
        if (task.Source != null)
            return TryInstallObjectPlan(world, npc, task.Source, task.Interaction, 1, out _, out _);
        Junction point;
        TileCoord tile;
        if (task.Site != null)
        {
            if (task.Site.Junctions.Count == 0 ||
                !world.Junctions.Items.TryGetValue(task.Site.Junctions[0], out point) ||
                npc.CurrentJunction is not { } from ||
                !Connectivity.Reachable(world, from, point.Id, CanUseCriticalTraversal(npc))) return false;
            tile = task.Site.Tile;
        }
        else if (!GardenMath.TryPickSpot(world, npc, out point, out tile)) return false;
        if (!SpatialMutations.TryReserveJunction(world, point.Id, npc.Id, world.Tick, Spec121.ManualReserveTicks)) return false;
        npc.Plan.TargetObjectId = task.Site?.Id;
        npc.Plan.TargetItemDefinitionId = task.Crop.SeedDefinitionId;
        npc.Plan.TargetJunctionId = point.Id;
        npc.Plan.TargetTile = tile;
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = point.Id });
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.PlantInventorySeed, TargetJunction = point.Id,
            TargetObject = task.GroundSeed?.Id,
            TimeoutEndTick = task.SeedIndex, Interaction = InteractionType.Plant
        });
        npc.Execution.TargetInventoryItem = task.GroundSeed is null ? npc.Inventory.Items[task.SeedIndex] : null;
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        return true;
    }
}
}
