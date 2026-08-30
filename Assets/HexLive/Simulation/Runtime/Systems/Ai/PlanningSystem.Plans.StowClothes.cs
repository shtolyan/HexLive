using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

public sealed partial class PlanningSystem
{
    /// <summary>
    /// §133: подобрать забытую вещь и отнести её домой. План из двух ног, как у
    /// стирки (§40.6 r4): дойти до вещи, поднять её в руку — и уже оттуда
    /// шагать к гардеробу. Вторая нога ставится исполнением, когда вещь в руке:
    /// строить её заранее нельзя, место у дома к тому времени может занять
    /// другая, а гардероб — наполниться.
    /// </summary>
    private void BuildStowClothesPlan(WorldState world, NPCState npc)
    {
        // Bug #314: одежда из карманов сдаётся домой без ноги «дойти и
        // поднять» — вещь сразу в руку, план из одной ноги к гардеробу.
        if (npc.CurrentJunction is not null &&
            npc.Execution.HeldGarment is null &&
            StrayGarmentMath.FindPocketGarment(world, npc) is { } pocket &&
            StowMath.FindUndressSpot(world, npc) is { } home)
        {
            npc.Inventory.Items.Remove(pocket);
            npc.Execution.HeldGarment = pocket;
            npc.Plan.TargetObjectId = home.StowObject;
            npc.Plan.TargetJunctionId = home.Stand;
            npc.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.MoveToJunction, TargetJunction = home.Stand
            });
            npc.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.StowCarriedGarment, TargetJunction = home.Stand
            });
            npc.Plan.CurrentStepIndex = 0;
            npc.Plan.Status = PlanStatus.Active;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "StowClothesPlanned",
                    $"Pocket={pocket.DefinitionId} Stand={home.Stand.Value}");
            }
            return;
        }

        if (npc.CurrentJunction is not { } from ||
            StrayGarmentMath.FindStray(world, npc) is not { } stray ||
            stray.Junctions.Count == 0)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.StowClothes);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanFailed", "Goal=StowClothes NothingStray");
            }
            return;
        }

        var anchor = stray.Junctions[0];
        JunctionId stand;
        if (SpatialQueries.IsJunctionFree(world, anchor) &&
            world.Junctions.Items.TryGetValue(anchor, out var anchorJunction) &&
            !anchorJunction.Blocked && Connectivity.Reachable(world, from, anchor))
        {
            stand = anchor;
        }
        else if (TryReserveBesideJunction(world, npc, anchor, 48, out var beside,
                     SpatialQueries.BesideReach(0f)))
        {
            stand = beside;
        }
        else
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.StowClothes);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanFailed",
                    $"Goal=StowClothes Obj={stray.Id.Value} NoStand");
            }
            return;
        }

        npc.Plan.TargetObjectId = stray.Id;
        npc.Plan.TargetJunctionId = stand;
        npc.Plan.TargetTile = stray.Tile;
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = stand });
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.StowCarriedGarment,
            TargetJunction = stand,
            TargetObject = stray.Id
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "StowClothesPlanned",
                $"Obj={stray.Id.Value} Def={stray.DefinitionId} Stand={stand.Value}");
        }
    }
}

}
