using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §119 compassion pledge: remembers one ally+limb across ordinary replans and
/// keeps selecting the next achievable link of the workbench/prosthetic chain.
/// </summary>
public sealed class ProstheticAidSystem : ISimulationSystem
{
    public string Name => nameof(ProstheticAidSystem);
    public TickLayer Layer => TickLayer.Medium;

    public void Run(WorldState world)
    {
        if (!Spec119.Enabled || !Spec118.Enabled || !Spec118.ProstheticsEnabled) return;

        foreach (var helper in world.Entities.Npcs.Values)
        {
            if (ManualControlMath.IsManual(helper))
            {
                continue;
            }

            ValidatePledge(world, helper);
            if (!helper.Mind.ProstheticAidTargetId.HasValue)
            {
                TryClaimBestNeed(world, helper);
            }

            if (!TryGetPledge(world, helper, out var patient, out var part)) continue;
            if (!MayWorkOnPledge(world, helper)) continue;

            // A ready part in the pack is intentionally left to RescueSystem:
            // it prepares a compatible bed (including carrying a conscious
            // prone patient there) and then performs the authored installation.
            var arm = part is BodyPart.ArmL or BodyPart.ArmR;
            var wooden = arm ? ContentIds.WoodenArm : ContentIds.WoodenLeg;
            var mechanical = arm ? ContentIds.MechanicalArm : ContentIds.MechanicalLeg;
            if (KenshiProstheticMath.HasItem(helper, wooden) ||
                KenshiProstheticMath.HasItem(helper, mechanical))
            {
                continue;
            }

            if (TryAssignPickup(world, helper, wooden, mechanical) ||
                (DecisionSystem.CountInventory(helper, ContentIds.Hide) == 0 &&
                 TryAssignPickup(world, helper, ContentIds.Hide)))
            {
                continue;
            }

            var craftGoal = arm ? GoalType.CraftWoodenArm : GoalType.CraftWoodenLeg;
            var station = CraftProjectMath.FindStationForGoal(world, helper, craftGoal);
            if (station is null)
            {
                // BedSiteSystem owns placement and staged construction. The
                // pledge survives while ordinary builders make the station.
                continue;
            }

            if (CraftProjectMath.HasReachableProject(world, helper, craftGoal) ||
                HasRecipeInputs(helper, craftGoal))
            {
                TryAssignGoal(world, helper, craftGoal);
                continue;
            }

            var boardNeed = RecipeCatalog.InputCount(craftGoal, ContentIds.Board);
            if (DecisionSystem.CountInventory(helper, ContentIds.Board) < boardNeed)
            {
                if (!GearCatalog.HasCapability(helper.Inventory.Items, GearCapability.Saw))
                {
                    TryAssignGoal(world, helper, GoalType.GatherTools);
                }
                else
                {
                    TryAssignGoal(world, helper, GoalType.GatherWood);
                }
                continue;
            }

            var ropeNeed = RecipeCatalog.InputCount(craftGoal, ContentIds.Rope);
            if (DecisionSystem.CountInventory(helper, ContentIds.Rope) < ropeNeed)
            {
                var ropeCost = System.Math.Max(1, RecipeCatalog.InputCount(
                    GoalType.CraftRope, ContentIds.Fiber));
                TryAssignGoal(world, helper,
                    DecisionSystem.CountInventory(helper, ContentIds.Fiber) >= ropeCost
                        ? GoalType.CraftRope
                        : GoalType.GatherFiber);
                continue;
            }

            if (DecisionSystem.CountInventory(helper, ContentIds.Hide) == 0)
            {
                TryAssignGoal(world, helper, GoalType.Butcher);
            }
        }
    }

    private static bool MayWorkOnPledge(WorldState world, NPCState helper) =>
        helper.Health > 0f && !helper.IsUnconscious(world.Tick) &&
        !helper.Body.IsProne && !helper.IsBeingCarried && !helper.IsCarryingPerson &&
        !helper.IsFighting && !helper.Mind.IsStarving && !helper.Mind.IsDehydrated &&
        helper.Execution.Status != ExecutionStatus.InProgress &&
        helper.Plan.Status != PlanStatus.Active &&
        world.Tick >= helper.Mind.ProstheticAidRetryAfterTick;

    private static bool TryAssignGoal(WorldState world, NPCState helper, GoalType goal)
    {
        // This system is an external goal producer, just like a help cry. It
        // must not resurrect a route Planning/Execution has just rejected.
        // Seed 31337 showed GatherTools being reassigned every four ticks to
        // the same boxed-in prosthetic despite a fresh failure cooldown.
        if (PlanningSystem.IsGoalOnCooldown(helper, goal, world.Tick))
        {
            return false;
        }

        helper.Mind.CurrentGoal = goal;
        helper.Plan.Status = PlanStatus.Invalid;
        helper.Plan.Goal = GoalType.None;
        helper.Plan.Steps.Clear();
        return true;
    }

    private static bool HasRecipeInputs(NPCState helper, GoalType goal)
    {
        if (!RecipeCatalog.ByGoal.TryGetValue(goal, out var recipe)) return false;
        foreach (var input in recipe.Inputs)
        {
            if (DecisionSystem.CountInventory(helper, input.Id) < input.Count) return false;
        }
        return true;
    }

    private static void ValidatePledge(WorldState world, NPCState helper)
    {
        if (!TryGetPledge(world, helper, out var patient, out var part) ||
            patient.Health <= 0f ||
            !CampDiplomacyMath.CanProvideCare(world, helper, patient) ||
            !patient.Body.IsSevered(part) ||
            patient.Body.Condition(part).Prosthetic is not null)
        {
            helper.Mind.ProstheticAidTargetId = null;
            helper.Mind.ProstheticAidPart = null;
        }
    }

    private static bool TryGetPledge(
        WorldState world, NPCState helper, out NPCState patient, out BodyPart part)
    {
        patient = null;
        part = BodyPart.ArmL;
        if (helper.Mind.ProstheticAidTargetId is not { } target ||
            helper.Mind.ProstheticAidPart is not { } pledgedPart ||
            !world.Entities.Npcs.TryGetValue(target, out patient)) return false;
        part = pledgedPart;
        return true;
    }

    private static void TryClaimBestNeed(WorldState world, NPCState helper)
    {
        if (helper.Health <= 0f || helper.CompassionTrait < 0.25f) return;
        NPCState bestPatient = null;
        var bestPart = BodyPart.ArmL;
        var bestScore = float.MinValue;
        foreach (var candidate in world.Entities.Npcs.Values)
        {
            if (candidate.Id == helper.Id || candidate.Health <= 0f ||
                !CampDiplomacyMath.CanProvideCare(world, helper, candidate)) continue;
            foreach (var part in new[] { BodyPart.ArmL, BodyPart.ArmR, BodyPart.LegL, BodyPart.LegR })
            {
                if (!NeedsReplacement(candidate, part) || IsClaimed(world, candidate.Id, part)) continue;
                var score = AidScore(helper, candidate);
                if (!IsBestHelper(world, helper, candidate, part, score)) continue;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPatient = candidate;
                    bestPart = part;
                }
            }
        }
        if (bestPatient is null) return;
        helper.Mind.ProstheticAidTargetId = bestPatient.Id;
        helper.Mind.ProstheticAidPart = bestPart;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, helper.Id, "ProstheticAidPledged",
                $"NPC{bestPatient.Id.Value} Part={bestPart} Compassion={helper.CompassionTrait:F2}");
        }
    }

    private static bool NeedsReplacement(NPCState patient, BodyPart part) =>
        patient.Body.IsSevered(part) &&
        patient.Body.Condition(part).Prosthetic is null &&
        !KenshiProstheticMath.HasUnstabilizedWound(patient, part);

    private static bool IsClaimed(WorldState world, EntityId target, BodyPart part)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Mind.ProstheticAidTargetId == target && npc.Mind.ProstheticAidPart == part)
                return true;
        }
        return false;
    }

    private static bool IsBestHelper(
        WorldState world, NPCState helper, NPCState patient, BodyPart part, float score)
    {
        foreach (var rival in world.Entities.Npcs.Values)
        {
            if (rival.Id == helper.Id || rival.Health <= 0f ||
                !CampDiplomacyMath.CanProvideCare(world, rival, patient) ||
                rival.CompassionTrait < 0.25f)
                continue;
            var rivalScore = AidScore(rival, patient);
            if (rivalScore > score + 0.001f ||
                (System.MathF.Abs(rivalScore - score) <= 0.001f && rival.Id.Value < helper.Id.Value))
                return false;
        }
        return true;
    }

    private static float AidScore(NPCState helper, NPCState patient)
    {
        var affinity = helper.Social.Relationships.TryGetValue(patient.Id, out var relation)
            ? relation.Affinity : 0f;
        var distance = HexSpatialMath.HexDistance(helper.Tile, patient.Tile);
        return helper.CompassionTrait * 2f + affinity * 0.75f - distance * 0.02f;
    }

    private static readonly List<JunctionId> ApproachScratch = new();

    private static bool TryAssignPickup(
        WorldState world, NPCState helper, params string[] acceptableIds)
    {
        if (PlanningSystem.IsGoalOnCooldown(
                helper, GoalType.GatherTools, world.Tick) ||
            helper.CurrentJunction is not { } from)
        {
            return false;
        }

        WorldObjectState target = null;
        JunctionId? targetApproach = null;
        var bestDistance = float.MaxValue;
        var occupied = PathfindingSystem.OtherActorJunctions(world, helper);
        foreach (var perceived in helper.Perception.Objects)
        {
            if (!perceived.IsReachable || System.Array.IndexOf(acceptableIds, perceived.DefinitionId) < 0 ||
                !world.Entities.Objects.TryGetValue(perceived.Id, out var candidate) ||
                candidate.IsCraftProject || candidate.IsOccupied || candidate.Junctions.Count == 0)
                continue;
            var anchor = candidate.Junctions[0];
            ApproachScratch.Clear();
            SpatialQueries.CollectStandableAround(
                world, anchor, ApproachScratch, 32, SpatialQueries.BesideReach(0f), candidate,
                InteractionReach.RimMode);
            foreach (var junction in ApproachScratch)
            {
                var isCurrent = junction.Equals(from);
                if ((!isCurrent && !SpatialQueries.IsJunctionFree(world, junction)) ||
                    occupied.Contains(junction) ||
                    (world.Reservations.Junctions.TryGetValue(junction, out var reservation) &&
                     reservation.Owner != helper.Id && reservation.EndTick >= world.Tick))
                {
                    continue;
                }

                var route = HexPathfinder.FindPath(
                    world, from, junction, occupied,
                    weightClimb: true,
                    canJump: PlanningSystem.CanUseRoutineTraversal(helper),
                    danger: null, dangerCost: 0L,
                    hardAvoid: DoorTopology.ForbiddenFor(world, helper.Faction),
                    maxExpansions: 2000);
                if (route.Count == 0)
                {
                    continue;
                }

                // Exact route length is the real cost; perceived Euclidean
                // distance can choose a prop across an uncrossable ledge.
                var distance = route.Count;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    target = candidate;
                    targetApproach = junction;
                }
            }
        }
        if (target is null || targetApproach is not { } workPoint ||
            !SpatialMutations.TryReserveJunction(world, workPoint, helper.Id, world.Tick, 48))
            return false;

        helper.Mind.CurrentGoal = GoalType.GatherTools;
        helper.Plan.Goal = GoalType.GatherTools;
        helper.Plan.TargetObjectId = target.Id;
        helper.Plan.TargetTile = target.Tile;
        helper.Plan.TargetJunctionId = workPoint;
        helper.Plan.TargetItemDefinitionId = target.DefinitionId;
        helper.Plan.Steps.Clear();
        helper.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = workPoint,
            TargetObject = target.Id
        });
        helper.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.Interact,
            TargetJunction = workPoint,
            TargetObject = target.Id,
            Interaction = InteractionType.PickUp
        });
        helper.Plan.CurrentStepIndex = 0;
        helper.Plan.Status = PlanStatus.Active;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, helper.Id, "ProstheticAidPickupAssigned",
                $"Def={target.DefinitionId} Obj={target.Id.Value}");
        }
        return true;
    }
}

}
