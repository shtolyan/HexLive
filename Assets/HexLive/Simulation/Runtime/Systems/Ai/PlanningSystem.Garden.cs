using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{
public sealed partial class PlanningSystem
{
    private static bool TryBuildGardenPlan(WorldState world, NPCState npc)
    {
        if (!GardenMath.TryChooseTask(world, npc, out var task)) return false;
        if (task.Source != null)
            return TryInstallObjectPlan(world, npc, task.Source, task.Interaction, 1, out _, out _);
        if (!GardenMath.TryPickSpot(world, npc, out var point, out var tile) ||
            !SpatialMutations.TryReserveJunction(world, point.Id, npc.Id, world.Tick, Spec121.ManualReserveTicks)) return false;
        npc.Plan.TargetItemDefinitionId = task.Crop.SeedDefinitionId;
        npc.Plan.TargetJunctionId = point.Id;
        npc.Plan.TargetTile = tile;
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = point.Id });
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.PlantInventorySeed, TargetJunction = point.Id,
            TimeoutEndTick = task.SeedIndex, Interaction = InteractionType.Plant
        });
        npc.Execution.TargetInventoryItem = npc.Inventory.Items[task.SeedIndex];
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        return true;
    }
}
}
