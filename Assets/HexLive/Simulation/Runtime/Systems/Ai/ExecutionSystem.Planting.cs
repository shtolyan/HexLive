using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

public sealed partial class ExecutionSystem
{
    private static void RunPlantInventorySeed(WorldState world, NPCState npc)
    {
        if (npc.Plan.Steps.Count == 0 ||
            npc.Plan.Steps[^1].Type != PlanStepType.PlantInventorySeed ||
            npc.Plan.TargetJunctionId is not { } target ||
            npc.Plan.TargetTile is not { } tile)
        {
            FailInventoryPlanting(world, npc, "PlantSeed: invalid plan");
            return;
        }

        if (npc.Movement.IsMoving)
        {
            return;
        }

        if (npc.Movement.Status == MovementStatus.Blocked)
        {
            FailInventoryPlanting(world, npc, "PlantSeed: target unreachable");
            return;
        }

        if (npc.CurrentJunction is not { } current || !current.Equals(target))
        {
            return;
        }

        var step = npc.Plan.Steps[^1];
        var sourceIndex = step.TimeoutEndTick ?? -1;
        var seedDefinitionId = npc.Plan.TargetItemDefinitionId ?? string.Empty;
        WorldObjectState groundSeed = null;
        ItemInstance seed = null;
        if (step.TargetObject is { } seedObjectId)
        {
            if (!world.Entities.Objects.TryGetValue(seedObjectId, out groundSeed) ||
                !GardenMath.IsUsableGroundSeed(world, npc, groundSeed, target, seedDefinitionId))
            {
                FailInventoryPlanting(world, npc, "PlantSeed: on-site seed disappeared");
                return;
            }
        }
        else
        {
            seed = npc.Execution.TargetInventoryItem;
            if (seed is null || !InventoryMath.ContainsReference(npc.Inventory.Items, seed))
            {
                // A save/load clears transient references. The guarded index
                // recovers the pending carried seed.
                seed = sourceIndex >= 0 && sourceIndex < npc.Inventory.Items.Count &&
                    npc.Inventory.Items[sourceIndex].DefinitionId == seedDefinitionId
                        ? npc.Inventory.Items[sourceIndex]
                        : null;
                npc.Execution.TargetInventoryItem = seed;
            }
        }

        if ((groundSeed is null && (seed is null || seed.DefinitionId != seedDefinitionId)) ||
            !InventoryPlacementMath.CanPlace(seedDefinitionId))
        {
            FailInventoryPlanting(world, npc, "PlantSeed: selected seed disappeared");
            return;
        }

        if (!npc.Body.HasUsableHand || !npc.Body.CanUseToolsOrWeapons)
        {
            FailInventoryPlanting(world, npc, "PlantSeed: usable hand or stance lost");
            return;
        }

        ObjectId? plantingSite = null;
        if (npc.Plan.TargetObjectId is { } siteId)
        {
            if (!world.Entities.Objects.TryGetValue(siteId, out var site) ||
                !BuildSiteMath.IsPlantingSite(site) || site.BuildProduct != seedDefinitionId ||
                !site.Junctions.Contains(target))
            {
                FailInventoryPlanting(world, npc, "PlantSeed: blueprint disappeared");
                return;
            }
            plantingSite = siteId;
        }
        if (!PlantGrowthSystem.CanPlantAt(world, target, tile, npc.Id, plantingSite,
                allowLooseSeeds: PlantGrowthCatalog.TryGetBySeed(seedDefinitionId, out _)))
        {
            FailInventoryPlanting(world, npc, "PlantSeed: planting spot became occupied");
            return;
        }

        // A completed/failed previous action is not a running planting beat.
        // Garden plans and orders placed while idle retain that terminal status;
        // waiting only for None stranded the first planting attempt forever.
        if (npc.Execution.Status is ExecutionStatus.None or
            ExecutionStatus.Completed or ExecutionStatus.Failed)
        {
            if (!world.Content.ObjectDefinitions.TryGetValue(seedDefinitionId, out var definition))
            {
                FailInventoryPlanting(world, npc, "PlantSeed: seed definition missing");
                return;
            }

            var interaction = InventoryPlacementMath.InteractionFor(world, seedDefinitionId);
            if (interaction is null)
            {
                FailInventoryPlanting(world, npc, "PlantSeed: planting action missing");
                return;
            }

            var workTicks = AttributeMath.WorkTicks(
                npc, interaction.DurationTicks, InteractionType.Plant,
                GoalType.PlayerOrder);
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Plant;
            npc.Execution.TargetObject = null;
            npc.Execution.TargetInventoryItem = seed;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + workTicks;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "InteractionStarted",
                    $"Plant inventory seed {seedDefinitionId} " +
                    $"Duration={workTicks}ticks Junction={target.Value}");
            }
            return;
        }

        if (npc.Execution.Status != ExecutionStatus.InProgress ||
            world.Tick < npc.Execution.EndTick)
        {
            return;
        }

        if (seed is not null && !InventoryMath.RemoveReference(npc.Inventory.Items, seed))
        {
            FailInventoryPlanting(world, npc, "PlantSeed: selected seed disappeared");
            return;
        }

        var planted = seedDefinitionId == ContentIds.Torch
            ? InventoryPlacementMath.PlaceTorch(world, npc, seed, target, tile)
            : PlantGrowthSystem.TryPlantAt(world, seedDefinitionId, target, tile, npc.Id, plantingSite);
        if (planted is null)
        {
            // The site was validated immediately above; retain ownership if a
            // content mutation nevertheless made the final spawn impossible.
            if (seed is not null) npc.Inventory.Items.Add(seed);
            FailInventoryPlanting(world, npc, "PlantSeed: final placement failed");
            return;
        }

        var batchCenter = plantingSite.HasValue && npc.Plan.Goal == GoalType.PlayerOrder
            ? npc.Plan.PlantingBatchCenter : null;
        if (groundSeed is not null) WorldObjectMutations.DespawnObject(world, groundSeed.Id);
        if (plantingSite is { } completedSite) WorldObjectMutations.DespawnObject(world, completedSite);
        npc.Plan.TargetObjectId = null;

        var duration = npc.Execution.EndTick - npc.Execution.StartTick;
        SkillTrace.Award(world, npc, InteractionType.Plant, duration);
        npc.Execution.Status = ExecutionStatus.Completed;
        npc.Execution.LastCompletedTick = world.Tick;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetObject = null;
        npc.Execution.TargetInventoryItem = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        SpatialMutations.FreeJunction(world, target, npc.Id);
        SpatialMutations.ReleaseJunctionReservation(world, target, npc.Id);
        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Plan.PlantingBatchCenter = null;
        npc.Plan.TargetItemDefinitionId = null;
        npc.Mind.CurrentGoal = GoalType.None;
        ManualControlMath.RenewInactivityLease(world, npc);

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "SeedPlanted",
                $"Seed={seedDefinitionId} Plant={planted.DefinitionId} " +
                $"Tile={tile.Q},{tile.R} Junction={target.Value}");
        }
        if (batchCenter is { } center)
            ManualCommandExecutor.ContinuePlantingBatch(world, npc, center);
    }

    private static void FailInventoryPlanting(
        WorldState world, NPCState npc, string reason)
    {
        npc.Plan.PlantingBatchCenter = null;
        PlanInterruption.TryAbort(
            world, npc, InterruptionCause.ExecutionFailure, reason);
        npc.Mind.CurrentGoal = GoalType.None;
    }
}

}
