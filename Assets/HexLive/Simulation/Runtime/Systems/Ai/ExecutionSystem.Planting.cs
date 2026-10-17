using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
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
        var seed = npc.Execution.TargetInventoryItem;
        if (seed is null || !InventoryMath.ContainsReference(npc.Inventory.Items, seed))
        {
            // A save/load clears transient object references. Seeds have no
            // per-instance state, so the guarded source index can safely
            // restore this exact pending inventory selection.
            seed = sourceIndex >= 0 && sourceIndex < npc.Inventory.Items.Count &&
                npc.Inventory.Items[sourceIndex].DefinitionId == seedDefinitionId
                    ? npc.Inventory.Items[sourceIndex]
                    : null;
            npc.Execution.TargetInventoryItem = seed;
        }

        if (seed is null || seed.DefinitionId != seedDefinitionId ||
            !PlantGrowthCatalog.TryGetBySeed(seedDefinitionId, out _))
        {
            FailInventoryPlanting(world, npc, "PlantSeed: selected seed disappeared");
            return;
        }

        if (!npc.Body.HasUsableHand || !npc.Body.CanUseToolsOrWeapons)
        {
            FailInventoryPlanting(world, npc, "PlantSeed: usable hand or stance lost");
            return;
        }

        if (!PlantGrowthSystem.CanPlantAt(world, target, tile, npc.Id))
        {
            FailInventoryPlanting(world, npc, "PlantSeed: planting spot became occupied");
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            if (!world.Content.ObjectDefinitions.TryGetValue(seedDefinitionId, out var definition))
            {
                FailInventoryPlanting(world, npc, "PlantSeed: seed definition missing");
                return;
            }

            var interaction = definition.Interactions.FirstOrDefault(candidate =>
                candidate.Type == InteractionType.Plant);
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

        if (!InventoryMath.RemoveReference(npc.Inventory.Items, seed))
        {
            FailInventoryPlanting(world, npc, "PlantSeed: selected seed disappeared");
            return;
        }

        var planted = PlantGrowthSystem.TryPlantAt(
            world, seedDefinitionId, target, tile, npc.Id);
        if (planted is null)
        {
            // The site was validated immediately above; retain ownership if a
            // content mutation nevertheless made the final spawn impossible.
            npc.Inventory.Items.Add(seed);
            FailInventoryPlanting(world, npc, "PlantSeed: final placement failed");
            return;
        }

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
        npc.Plan.TargetItemDefinitionId = null;
        npc.Mind.CurrentGoal = GoalType.None;
        ManualControlMath.RenewInactivityLease(world, npc);

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "SeedPlanted",
                $"Seed={seedDefinitionId} Plant={planted.DefinitionId} " +
                $"Tile={tile.Q},{tile.R} Junction={target.Value}");
        }
    }

    private static void FailInventoryPlanting(
        WorldState world, NPCState npc, string reason)
    {
        PlanInterruption.TryAbort(
            world, npc, InterruptionCause.ExecutionFailure, reason);
        npc.Mind.CurrentGoal = GoalType.None;
    }
}

}
