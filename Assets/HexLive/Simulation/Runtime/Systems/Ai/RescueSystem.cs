using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;

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
            if (ManualControlMath.IsManual(helper) ||
                helper.Health <= 0f || helper.IsUnconscious(world.Tick) ||
                helper.Body.IsProne || helper.IsBeingCarried ||
                helper.IsCarryingPerson || helper.IsFighting ||
                helper.Mind.PendingAbuseFrom is not null ||
                helper.Mind.PendingExpulsionFrom is not null ||
                helper.Plan.Status == PlanStatus.Active ||
                helper.Execution.Status == ExecutionStatus.InProgress ||
                helper.Mind.IsStarving || helper.Mind.IsDehydrated)
            {
                continue;
            }

            if (TryResumeInterruptedRescue(world, helper))
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
                    IsClaimedByOtherActiveHelper(world, helper, candidate))
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

            if (patient is null || !TryAssignRescue(world, helper, patient, best, resumed: false))
            {
                TryAssignLimbCare(world, helper);
                continue;
            }
        }
    }

    private static bool TryResumeInterruptedRescue(WorldState world, NPCState helper)
    {
        if (helper.Mind.InterruptedRescuePatientId is not { } patientId)
        {
            return false;
        }

        if (!world.Entities.Npcs.TryGetValue(patientId, out var patient) ||
            !FactionRelations.AreAllies(helper, patient) ||
            !KenshiRescueMath.NeedsRescue(world, patient) ||
            IsClaimedByOtherActiveHelper(world, helper, patient))
        {
            ClearInterruptedRescue(world, helper);
            return false;
        }

        var distance = HexLive.Simulation.Spatial.HexSpatialMath.HexDistance(
            helper.Tile, patient.Tile);
        if (TryAssignRescue(world, helper, patient, distance, resumed: true))
        {
            return true;
        }

        // No reachable approach: do not hold a dying patient hostage behind a
        // stale promise. Release her for another rescuer and continue the
        // ordinary auction in this same pass.
        ClearInterruptedRescue(world, helper);
        return false;
    }

    private static bool TryAssignRescue(
        WorldState world, NPCState helper, NPCState patient, int distance, bool resumed)
    {
        if (!KenshiRescueMath.TryFindApproach(world, helper, patient, out var approach))
        {
            return false;
        }

        helper.Mind.CurrentGoal = GoalType.Rescue;
        helper.Mind.InterruptedRescuePatientId = null;
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
        TryPrearmLocalApproach(world, helper, approach);
        patient.Mind.PendingAidFrom = helper.Id;
        patient.Mind.PendingAidSinceTick = world.Tick;
        Trace.Emit(world, helper.Id, resumed ? "RescueResumed" : "RescueAssigned",
            $"NPC{patient.Id.Value} Distance={distance} Approach={approach.Value}");
        return true;
    }

    // The patient approach is commonly the immediately adjacent lattice node.
    // Pre-arm that exact graph edge so the next Fast tick does not invoke an
    // island-wide weighted search merely to take one local step.
    private static void TryPrearmLocalApproach(
        WorldState world, NPCState helper, JunctionId approach)
    {
        if (helper.CurrentJunction is not { } from ||
            !world.Junctions.Items.TryGetValue(from, out var start) ||
            (from != approach && !start.Neighbors.Contains(approach)))
        {
            return;
        }

        helper.Movement.JunctionPath.Clear();
        helper.Movement.JunctionPath.Add(from);
        if (from != approach)
        {
            helper.Movement.JunctionPath.Add(approach);
        }

        helper.Movement.PathIndex = 1;
        helper.Movement.BlockedWaitTicks = 0;
        helper.Movement.HopArmed = false;
        helper.Movement.HopPathIndex = -1;
        helper.Movement.IsMoving = from != approach;
        helper.Movement.SetStatus(
            helper.Movement.IsMoving ? MovementStatus.Moving : MovementStatus.Arrived);
        helper.Movement.StopReason = string.Empty;
    }

    private static bool IsClaimedByOtherActiveHelper(
        WorldState world, NPCState helper, NPCState patient)
    {
        if (patient.Mind.PendingAidFrom is not { } claimedBy || claimedBy == helper.Id)
        {
            return false;
        }

        if (world.Entities.Npcs.TryGetValue(claimedBy, out var claimant) &&
            claimant.Health > 0f && !claimant.IsUnconscious(world.Tick) &&
            (claimant.CarriedNpcId == patient.Id ||
             claimant.Mind.InterruptedRescuePatientId == patient.Id ||
             (claimant.Plan.TargetAgentId == patient.Id &&
              claimant.Mind.CurrentGoal is GoalType.Aid or GoalType.Rescue or
                  GoalType.Splint or GoalType.FitProsthetic)))
        {
            return true;
        }

        patient.Mind.PendingAidFrom = null;
        return false;
    }

    private static void ClearInterruptedRescue(WorldState world, NPCState helper)
    {
        if (helper.Mind.InterruptedRescuePatientId is { } patientId &&
            world.Entities.Npcs.TryGetValue(patientId, out var patient) &&
            patient.Mind.PendingAidFrom == helper.Id)
        {
            patient.Mind.PendingAidFrom = null;
        }

        helper.Mind.InterruptedRescuePatientId = null;
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
