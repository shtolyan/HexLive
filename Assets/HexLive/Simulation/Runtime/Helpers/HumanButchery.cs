using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{
// §56.7: manual choice bypasses AI hunger/empathy gates, never physical gates.
internal static class HumanButchery
{
    public static string Validate(WorldState world, NPCState actor, EntityId targetId)
    {
        if (actor.Health <= 0 || actor.IsUnconscious(world.Tick) || !actor.Body.CanUseToolsOrWeapons)
            return "Incapacitated";
        if (!GearCatalog.HasCapability(actor.Inventory.Items, GearCapability.Butcher)) return "MissingTool";
        if (actor.Id == targetId) return "TargetSelf";
        if (!KenshiRescueMath.TryGetPerson(world, targetId, out var target, out var dead)) return "TargetGone";
        if (!dead && target.Health > 0 && !target.IsUnconscious(world.Tick)) return "TargetUnavailable";
        if (target.CarriedByNpcId != null || target.CurrentJunction == null) return "TargetUnavailable";
        return null;
    }

    public static bool Start(WorldState world, NPCState actor, EntityId targetId)
    {
        if (Validate(world, actor, targetId) != null ||
            !KenshiRescueMath.TryGetPerson(world, targetId, out var target, out _) ||
            !KenshiRescueMath.TryFindApproach(world, actor, target, out var approach)) return false;
        actor.Plan.Goal = GoalType.PlayerOrder;
        actor.Plan.TargetAgentId = targetId;
        actor.Plan.TargetJunctionId = approach;
        actor.Plan.TargetTile = target.Tile;
        actor.Plan.Steps.Clear();
        actor.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = approach });
        actor.Plan.Steps.Add(new PlanStep { Type = PlanStepType.Interact, TargetJunction = approach,
            Interaction = InteractionType.Butcher, InteractionId = HumanMeatCatalog.ButcherPerson });
        actor.Plan.CurrentStepIndex = 0;
        actor.Plan.Status = PlanStatus.Active;
        actor.Mind.CurrentGoal = GoalType.PlayerOrder;
        return true;
    }

    public static void Run(WorldState world, NPCState actor)
    {
        var targetId = actor.Plan.TargetAgentId;
        var reason = targetId is { } id ? Validate(world, actor, id) : "TargetGone";
        if (reason != null) { Fail(world, actor, reason); return; }
        KenshiRescueMath.TryGetPerson(world, targetId.Value, out var target, out var dead);
        if (actor.Movement.IsMoving || (actor.Movement.Status != MovementStatus.Arrived &&
            actor.Movement.JunctionPath.Count > 0)) return;
        if (actor.Movement.Status == MovementStatus.Blocked ||
            actor.CurrentJunction != actor.Plan.TargetJunctionId ||
            actor.CurrentJunction is not { } here || target.CurrentJunction is not { } there ||
            !PlanningSystem.IsAdjacentJunction(world, here, there))
        { Fail(world, actor, "TargetUnavailable"); return; }
        // A second worker must not receive a second yield from the same target.
        foreach (var other in world.Entities.Npcs.Values)
            if (other.Id != actor.Id && other.Execution.CurrentInteraction == InteractionType.Butcher &&
                other.Execution.Status == ExecutionStatus.InProgress && other.Plan.TargetAgentId == targetId)
            { Fail(world, actor, "Occupied"); return; }
        ExecutionSystem.HoldSceneJunction(world, actor);
        if (actor.Execution.Status != ExecutionStatus.InProgress)
        {
            actor.Execution.Status = ExecutionStatus.InProgress;
            actor.Execution.CurrentInteraction = InteractionType.Butcher;
            actor.Execution.StartTick = world.Tick;
            actor.Execution.EndTick = world.Tick + SimBalance.ButcherDurationTicks;
        }
        if (world.Tick < actor.Execution.EndTick) return;
        if (!dead)
        {
            if (target.Health > 0)
            {
                PredationSystem.ApplyKillConsequences(world, actor, target);
                target.Body.Parts[BodyPart.Torso] = 0f;
                target.Health = 0f;
            }
            // The normal mortality sweep owns live roster mutation. Never remove
            // an NPC while ExecutionSystem is enumerating that dictionary.
            return;
        }
        var anchor = CorpseMath.AnchorOf(world, target.Id);
        if (anchor == null) { Fail(world, actor, "TargetGone"); return; }
        Dismantle(world, actor, anchor, target);
        if (actor.Plan.TargetJunctionId is { } approach)
            SpatialMutations.ReleaseJunctionReservation(world, approach, actor.Id);
        ExecutionSystem.ReleaseClaims(world, actor);
        actor.Plan.TargetJunctionId = null;
        actor.Plan.TargetTile = null;
        actor.Execution.CurrentInteraction = null;
        actor.Execution.Status = ExecutionStatus.None;
        actor.Execution.LastCompletedTick = world.Tick;
        actor.Plan.Status = PlanStatus.Completed;
        actor.Plan.Steps.Clear();
        actor.Plan.TargetAgentId = null;
        actor.Mind.CurrentGoal = GoalType.None;
        ManualControlMath.RenewInactivityLease(world, actor);
        AgentCommandLedger.Finish(world, actor, "completed", "Butchered");
    }

    private static void Fail(WorldState world, NPCState actor, string reason)
    {
        AgentCommandLedger.Finish(world, actor, "failed", reason);
        PlanInterruption.TryAbort(world, actor, InterruptionCause.ExecutionFailure, reason);
        actor.Mind.CurrentGoal = GoalType.None;
    }

    public static void Dismantle(WorldState world, NPCState actor, WorldObjectState anchor, NPCState body)
    {
        // Remove the source before dropping outputs; already missing limbs never regrow.
        WorldObjectMutations.DespawnObject(world, anchor.Id);
        world.Entities.Corpses.Remove(body.Id);
        foreach (var item in body.WornItems) Drop(world, actor, item);
        foreach (var item in body.Inventory.Items) Drop(world, actor, item);
        body.Inventory.Items.Clear();
        body.WornItems.Clear();
        foreach (var part in new[] { BodyPart.ArmL, BodyPart.ArmR, BodyPart.LegL, BodyPart.LegR })
        {
            if (!body.Body.IsSevered(part))
                Drop(world, actor, new ItemInstance(part is BodyPart.ArmL or BodyPart.ArmR
                    ? HumanMeatCatalog.ArmRaw : HumanMeatCatalog.LegRaw));
            if (body.Body.Condition(part).Prosthetic is { } prosthetic)
                Drop(world, actor, new ItemInstance(prosthetic.DefinitionId) {
                    Durability = prosthetic.MaxCondition <= 0 ? 0 : prosthetic.Condition / prosthetic.MaxCondition,
                    OwnerId = body.Id.Value });
        }
        Drop(world, actor, new ItemInstance(HumanMeatCatalog.TorsoRaw));
        actor.Needs.Comfort = System.Math.Max(0f, actor.Needs.Comfort - SimBalance.CannibalismComfortPenalty);
        Trace.Emit(world, actor.Id, "Butchered", $"NPC{body.Id.Value} -> body parts [cannibalism]");
    }

    private static void Drop(WorldState world, NPCState actor, ItemInstance item)
    {
        if (ExecutionSystem.DropItemAtFeet(world, actor, item) == null)
            InventoryMath.RetainOwnedItem(actor, item);
    }
}
}
