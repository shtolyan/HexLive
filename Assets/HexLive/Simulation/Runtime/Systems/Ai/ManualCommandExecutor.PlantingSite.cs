using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{
internal static partial class ManualCommandExecutor
{
    private static void ApplyPlacePlantingSite(WorldState world, PlacePlantingSiteCommand command, AdmissionTracker admission)
    {
        if (!BuildCatalogDefinition.TryGet(command.CatalogId, out _) ||
            !PlantGrowthCatalog.TryGetBySeed(command.CatalogId, out _))
        { admission.Reject("UnknownProduct"); return; }
        if (!PlantGrowthSystem.CanPlantAt(world, command.Anchor, command.Tile, allowLooseSeeds: true))
        { admission.Reject("PlantingSpotOccupied"); return; }
        var site = WorldObjectMutations.SpawnObject(world, ContentIds.BuildSite,
            world.Junctions.Items[command.Anchor].Fragment, command.Tile, command.Anchor);
        site.BuildProduct = command.CatalogId;
        site.OwnerFaction = ConstructionAccess.Builder(world, admission.BuildFactions, command.Tile);
    }

    private static bool TryInstallPlantingPlan(WorldState world, NPCState npc,
        PlantInventorySeedCommand command, ObjectId? plantingSite, WorldObjectState groundSeed,
        TileCoord? batchCenter)
    {
        if (!SpatialMutations.TryReserveJunction(world, command.TargetJunction, npc.Id, world.Tick,
                Spec121.ManualReserveTicks)) return false;

        npc.Plan.Goal = GoalType.PlayerOrder;
        npc.Plan.TargetJunctionId = command.TargetJunction;
        npc.Plan.TargetTile = command.TargetTile;
        npc.Plan.TargetItemDefinitionId = command.Item.ExpectedDefinitionId;
        npc.Plan.TargetObjectId = plantingSite;
        npc.Plan.PlantingBatchCenter = batchCenter;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = command.TargetJunction
        });
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.PlantInventorySeed,
            TargetJunction = command.TargetJunction,
            TargetObject = groundSeed?.Id,
            TimeoutEndTick = command.Item.Index,
            Interaction = InteractionType.Plant
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        npc.Mind.CurrentGoal = GoalType.PlayerOrder;
        npc.Execution.TargetInventoryItem = groundSeed is null ? npc.Inventory.Items[command.Item.Index] : null;
        return true;
    }

    internal static void ContinuePlantingBatch(WorldState world, NPCState npc, TileCoord center)
    {
        if (!ManualControlMath.IsManual(npc) || Incapacitated(world, npc) ||
            npc.Needs.Stamina <= SimBalance.StaminaExhaustedThreshold ||
            !npc.Body.HasUsableHand || !npc.Body.CanUseToolsOrWeapons) return;
        var task = GardenMath.FindNextManualPlanting(world, npc, center);
        if (task is null) return;
        var site = task.Site;
        TryInstallPlantingPlan(world, npc, new PlantInventorySeedCommand(npc.Id,
            new InventoryItemRef(InventoryItemSource.Carried, task.SeedIndex, site.BuildProduct),
            site.Junctions[0], site.Tile), site.Id, task.GroundSeed, center);
    }
}
}
