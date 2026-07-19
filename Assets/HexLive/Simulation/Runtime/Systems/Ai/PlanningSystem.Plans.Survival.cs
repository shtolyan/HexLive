using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

public sealed partial class PlanningSystem
{
    private static bool BuildCoconutDrinkPlan(WorldState world, NPCState npc)
    {
        if (TryFindInventoryItem(npc, "food.coconut_pierced", requireWater: true, out _))
        {
            return false;
        }

        if (HasCoconutBlade(npc) &&
            TryFindInventoryItem(npc, "food.coconut", out var carriedWhole))
        {
            return BuildCoconutInventoryPlan(world, npc, GoalType.Drink, carriedWhole,
                InteractionType.Process, InteractionType.PickUp);
        }

        if (TryFindCoconutObject(npc, world, "food.coconut_pierced", requireWater: true, out var pierced))
        {
            return BuildCoconutWorldPlan(world, npc, GoalType.Drink, pierced, InteractionType.PickUp);
        }

        if (HasCoconutBlade(npc) &&
            TryFindCoconutObject(npc, world, "food.coconut", requireWater: false, out var whole))
        {
            return BuildCoconutWorldPlan(world, npc, GoalType.Drink, whole,
                InteractionType.Process, InteractionType.PickUp);
        }

        return false;
    }

    private static bool BuildCoconutEatPlan(WorldState world, NPCState npc)
    {
        if (TryFindInventoryItem(npc, "food.coconut_open", out _))
        {
            return false;
        }

        if (HasCoconutBlade(npc) &&
            TryFindInventoryItem(npc, "food.coconut_pierced", out var carriedPierced))
        {
            return BuildCoconutInventoryPlan(world, npc, GoalType.Eat, carriedPierced,
                InteractionType.Process, InteractionType.PickUp);
        }

        if (HasCoconutBlade(npc) &&
            TryFindInventoryItem(npc, "food.coconut", out var carriedWhole))
        {
            return BuildCoconutInventoryPlan(world, npc, GoalType.Eat, carriedWhole,
                InteractionType.Process, InteractionType.Process, InteractionType.PickUp);
        }

        if (TryFindCoconutObject(npc, world, "food.coconut_open", requireWater: false, out var open))
        {
            return BuildCoconutWorldPlan(world, npc, GoalType.Eat, open, InteractionType.PickUp);
        }

        if (HasCoconutBlade(npc) &&
            TryFindCoconutObject(npc, world, "food.coconut_pierced", requireWater: false, out var pierced))
        {
            return BuildCoconutWorldPlan(world, npc, GoalType.Eat, pierced,
                InteractionType.Process, InteractionType.PickUp);
        }

        if (HasCoconutBlade(npc) &&
            TryFindCoconutObject(npc, world, "food.coconut", requireWater: false, out var whole))
        {
            return BuildCoconutWorldPlan(world, npc, GoalType.Eat, whole,
                InteractionType.Process, InteractionType.Process, InteractionType.PickUp);
        }

        return false;
    }

    private static bool BuildCoconutInventoryPlan(
        WorldState world,
        NPCState npc,
        GoalType goal,
        ItemInstance item,
        params InteractionType[] interactions)
    {
        if (npc.CurrentJunction is not { } current)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, goal);
            Trace.Emit(world, npc.Id, "PlanFailed", $"Goal={goal} Coconut inventory plan has no current junction");
            return true;
        }

        npc.Plan.TargetItemDefinitionId = item.DefinitionId;
        npc.Plan.TargetJunctionId = current;
        npc.Plan.TargetTile = npc.Tile;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.DropInventoryItem,
            TargetJunction = current
        });
        foreach (var interaction in interactions)
        {
            npc.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.Interact,
                TargetJunction = current,
                Interaction = interaction
            });
        }

        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        Trace.Emit(world, npc.Id, "PlanBuilt",
            $"Goal={goal} Item={item.DefinitionId} Steps=[DropInventoryItem,{FormatInteractions(interactions)}]");
        return true;
    }

    private static bool BuildCoconutWorldPlan(
        WorldState world,
        NPCState npc,
        GoalType goal,
        PerceivedObject target,
        params InteractionType[] interactions)
    {
        if (!world.Entities.Objects.TryGetValue(target.Id, out var worldObject) ||
            worldObject.Junctions.Count == 0)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, goal);
            Trace.Emit(world, npc.Id, "PlanFailed", $"Goal={goal} Coconut target vanished or has no junction");
            return true;
        }

        var anchorJunction = worldObject.Junctions[0];
        if (!TryReserveBesideJunction(world, npc, anchorJunction, 48, out var targetJunction))
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, goal);
            Trace.Emit(world, npc.Id, "ReservationFailed",
                $"Coconut Anchor={anchorJunction.Value} has no free junction beside it");
            return true;
        }

        if (npc.CurrentJunction is not { } current || !current.Equals(targetJunction))
        {
            if (!SpatialMutations.TryReserveJunction(world, targetJunction, npc.Id, world.Tick, 48))
            {
                npc.Plan.Status = PlanStatus.Failed;
                SetGoalCooldown(world, npc, goal);
                Trace.Emit(world, npc.Id, "ReservationFailed",
                    $"Coconut beside Junction={targetJunction.Value} already reserved or occupied");
                return true;
            }
        }

        npc.Plan.TargetObjectId = target.Id;
        npc.Plan.TargetTile = target.Tile;
        npc.Plan.TargetJunctionId = targetJunction;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = targetJunction,
            TargetObject = target.Id
        });
        foreach (var interaction in interactions)
        {
            npc.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.Interact,
                TargetObject = target.Id,
                TargetJunction = targetJunction,
                Interaction = interaction
            });
        }

        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        Trace.Emit(world, npc.Id, "PlanBuilt",
            $"Goal={goal} Target={target.DefinitionId} Tile={target.Tile.Q},{target.Tile.R} " +
            $"Anchor={anchorJunction.Value} Junction={targetJunction.Value} " +
            $"Steps=[MoveToJunction,{FormatInteractions(interactions)}]");
        return true;
    }

    private static bool TryFindInventoryItem(NPCState npc, string definitionId, out ItemInstance item)
    {
        return TryFindInventoryItem(npc, definitionId, requireWater: false, out item);
    }

    private static bool TryFindInventoryItem(
        NPCState npc,
        string definitionId,
        bool requireWater,
        out ItemInstance item)
    {
        foreach (var carried in npc.Inventory.Items)
        {
            if (carried.DefinitionId == definitionId &&
                (!requireWater || carried.ResourceAmount > 0f))
            {
                item = carried;
                return true;
            }
        }

        item = null;
        return false;
    }

    private static bool TryFindCoconutObject(
        NPCState npc,
        WorldState world,
        string definitionId,
        bool requireWater,
        out PerceivedObject result)
    {
        PerceivedObject? best = null;
        foreach (var obj in npc.Perception.Objects)
        {
            if (!obj.IsReachable ||
                obj.DefinitionId != definitionId ||
                !DecisionSystem.ObjectUsableBy(obj, npc.Id) ||
                !world.Entities.Objects.TryGetValue(obj.Id, out var worldObject) ||
                (requireWater && worldObject.ResourceAmount <= 0f))
            {
                continue;
            }

            if (best is null || obj.Distance < best.Distance)
            {
                best = obj;
            }
        }

        result = best;
        return best is not null;
    }

    // ONE truth — DecisionSystem owns the rule (§50-prone: piercing a coconut
    // is light hand-work, allowed lying). This private duplicate silently kept
    // the old stand-up-only body and starved one-legged Marta at day 28.
    private static bool HasCoconutBlade(NPCState npc) =>
        DecisionSystem.HasCoconutBlade(npc);

    // Spec 27.18A foraging: no known food item — walk to the nearest known
    // producer; arriving brings dropped fruit into perception radius.
    private void BuildForagePlan(WorldState world, NPCState npc)
    {
        PerceivedObject? flora = null;
        foreach (var obj in npc.Perception.Objects)
        {
            if (!obj.IsReachable ||
                !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                definition.Produce is null)
            {
                continue;
            }

            // Spec 29C.4A food avoidance: skip producers near fresh danger
            // unless starving.
            if (!npc.Mind.IsStarving && IsNearDanger(npc, obj.Tile, 2))
            {
                continue;
            }

            if (flora is null || obj.Distance < flora.Distance)
            {
                flora = obj;
            }
        }

        if (flora is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.GetFood);
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=GetFood NoKnownProducer");
            return;
        }

        if (HexSpatialMath.HexDistance(npc.Tile, flora.Tile) <= 1)
        {
            // Already by the tree and still no fruit in sight: wait it out.
            npc.Plan.Status = PlanStatus.Completed;
            npc.Mind.CurrentGoal = GoalType.None;
            SetGoalCooldown(world, npc, GoalType.GetFood);
            Trace.Emit(world, npc.Id, "ForageWaiting",
                $"At producer {flora.DefinitionId} Tile={flora.Tile.Q},{flora.Tile.R}, no fruit visible");
            return;
        }

        JunctionId? floraJunction = null;
        if (world.Entities.Objects.TryGetValue(flora.Id, out var floraObject))
        {
            floraJunction = floraObject.Junctions.Count > 0 ? floraObject.Junctions[0] : null;
        }
        else if (npc.Memory.KnownObjects.TryGetValue(flora.Id, out var floraMemory))
        {
            floraJunction = floraMemory.Junction;
        }

        if (floraJunction is not { } anchor)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.GetFood);
            Trace.Emit(world, npc.Id, "PlanFailed",
                $"Goal=GetFood Producer={flora.Id.Value} has no junction");
            return;
        }

        JunctionId? approach = null;
        foreach (var neighbor in SpatialQueries.GetPassableNeighbors(world, anchor))
        {
            if (SpatialQueries.IsJunctionFree(world, neighbor) &&
                SpatialMutations.TryReserveJunction(world, neighbor, npc.Id, world.Tick, 48))
            {
                approach = neighbor;
                break;
            }
        }

        if (approach is not { } approachJunction)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.GetFood);
            Trace.Emit(world, npc.Id, "PlanFailed",
                $"Goal=GetFood Producer={flora.Id.Value} NoFreeApproachJunction");
            return;
        }

        npc.Plan.TargetTile = flora.Tile;
        npc.Plan.TargetJunctionId = approachJunction;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approachJunction
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        Trace.Emit(world, npc.Id, "ForagePlanned",
            $"To {flora.DefinitionId} Tile={flora.Tile.Q},{flora.Tile.R} " +
            $"Junction={approachJunction.Value} FromMemory={flora.FromMemory} Steps=[MoveToJunction]");
    }

    // Spec 29E.4: goals shop by tag — food pickups and wood pickups never cross.
    // §54.12: is a WHOLE log in demand anywhere — a furniture site whose
    // current stage bills logs, or the raft (hauled log by log)?
    private static bool WholeLogsWanted(WorldState world, NPCState npc)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (BuildSiteMath.IsSite(obj) &&
                BuildSiteMath.Needs(obj, BuildSiteMath.MaterialLogs))
            {
                return true;
            }
        }

        return world.RaftProgress < WorldState.RaftTarget &&
            DecisionSystem.HasReachableWithTag(npc, world, "Raft");
    }
}

}
