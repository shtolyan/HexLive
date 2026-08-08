using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>§116 assigns one free allied rescuer to one helpless ally.</summary>
public sealed class RescueSystem : ISimulationSystem
{
    public string Name => nameof(RescueSystem);

    public TickLayer Layer => TickLayer.Medium;

    public void Run(WorldState world)
    {
        if (!Spec118.Enabled || !Spec118.RescueEnabled)
        {
            return;
        }

        foreach (var helper in world.Entities.Npcs.Values)
        {
            if (helper.Health <= 0f || helper.IsUnconscious(world.Tick) ||
                helper.Body.IsProne || helper.IsBeingCarried ||
                helper.IsCarryingPerson || helper.IsFighting ||
                helper.Plan.Status == PlanStatus.Active ||
                helper.Execution.Status == ExecutionStatus.InProgress ||
                helper.Mind.IsStarving || helper.Mind.IsDehydrated)
            {
                continue;
            }

            if (helper.Mind.Cooldowns.Exists(c =>
                    c.Goal == GoalType.Rescue && c.EndTick > world.Tick))
            {
                TryAssignLimbCare(world, helper);
                continue;
            }

            NPCState patient = null;
            var best = int.MaxValue;
            foreach (var candidate in world.Entities.Npcs.Values)
            {
                if (candidate.Id == helper.Id || !FactionRelations.AreAllies(helper, candidate) ||
                    !KenshiRescueMath.NeedsRescue(world, candidate) ||
                    (candidate.Mind.PendingAidFrom is { } claimedBy && claimedBy != helper.Id))
                {
                    continue;
                }

                var distance = HexLive.Simulation.Spatial.HexSpatialMath.HexDistance(
                    helper.Tile, candidate.Tile);
                if (distance < best ||
                    (distance == best && (patient is null || candidate.Id.Value < patient.Id.Value)))
                {
                    best = distance;
                    patient = candidate;
                }
            }

            if (patient is null ||
                !KenshiRescueMath.TryFindApproach(world, helper, patient, out var approach))
            {
                TryAssignLimbCare(world, helper);
                continue;
            }

            helper.Mind.CurrentGoal = GoalType.Rescue;
            helper.Plan.Goal = GoalType.Rescue;
            helper.Plan.TargetAgentId = patient.Id;
            helper.Plan.TargetJunctionId = approach;
            helper.Plan.TargetTile = patient.Tile;
            helper.Plan.Steps.Clear();
            helper.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.MoveToJunction,
                TargetJunction = approach
            });
            helper.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.PickUpPerson,
                TargetJunction = approach,
                Interaction = InteractionType.PickUpPerson
            });
            helper.Plan.CurrentStepIndex = 0;
            helper.Plan.Status = PlanStatus.Active;
            patient.Mind.PendingAidFrom = helper.Id;
            patient.Mind.PendingAidSinceTick = world.Tick;
            Trace.Emit(world, helper.Id, "RescueAssigned",
                $"NPC{patient.Id.Value} Distance={best} Approach={approach.Value}");
        }
    }

    private static bool TryAssignLimbCare(WorldState world, NPCState helper)
    {
        NPCState patient = null;
        var goal = GoalType.None;
        foreach (var candidate in world.Entities.Npcs.Values)
        {
            if (candidate.Id == helper.Id || !FactionRelations.AreAllies(helper, candidate) ||
                candidate.Health <= 0f || candidate.IsBeingCarried ||
                candidate.Mind.PendingAidFrom is not null)
            {
                continue;
            }

            if (Spec118.SplintsEnabled &&
                KenshiProstheticMath.HasItem(helper, ContentIds.Splint) &&
                KenshiProstheticMath.TryFindSplintPart(candidate, out _))
            {
                patient = candidate;
                goal = GoalType.Splint;
                break;
            }

            if (Spec118.ProstheticsEnabled &&
                KenshiProstheticMath.TryFindProstheticPart(
                    world, helper, candidate, out _, out _, out _))
            {
                patient = candidate;
                goal = GoalType.FitProsthetic;
                break;
            }
        }

        if (patient is null ||
            !KenshiRescueMath.TryFindApproach(world, helper, patient, out var approach))
        {
            return false;
        }

        // Keep the concrete reactive assignments explicit: besides making the
        // ownership clear, the goal-coverage gate can prove that both paths
        // are reachable without trying to infer the value of a local variable.
        if (goal == GoalType.Splint)
        {
            helper.Mind.CurrentGoal = GoalType.Splint;
        }
        else
        {
            helper.Mind.CurrentGoal = GoalType.FitProsthetic;
        }
        helper.Plan.Goal = goal;
        helper.Plan.TargetAgentId = patient.Id;
        helper.Plan.TargetJunctionId = approach;
        helper.Plan.TargetTile = patient.Tile;
        helper.Plan.Steps.Clear();
        helper.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approach
        });
        helper.Plan.Steps.Add(new PlanStep
        {
            Type = goal == GoalType.Splint
                ? PlanStepType.ApplySplint
                : PlanStepType.FitProsthetic,
            TargetJunction = approach,
            Interaction = goal == GoalType.Splint
                ? InteractionType.Splint
                : InteractionType.FitProsthetic
        });
        helper.Plan.Status = PlanStatus.Active;
        patient.Mind.PendingAidFrom = helper.Id;
        patient.Mind.PendingAidSinceTick = world.Tick;
        Trace.Emit(world, helper.Id, "LimbCareAssigned",
            $"Goal={goal} NPC{patient.Id.Value} Approach={approach.Value}");
        return true;
    }
}

}
