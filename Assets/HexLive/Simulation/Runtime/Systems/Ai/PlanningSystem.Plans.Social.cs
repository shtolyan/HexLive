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
    // Spec 29C.5: wander to a seeded-random unblocked junction 3-8 tiles away.
    // Discoveries along the way land in spatial memory.
    private readonly System.Collections.Generic.List<Junction> _exploreCandidates = new();

    private void BuildExplorePlan(WorldState world, NPCState npc)
    {
        _exploreCandidates.Clear();
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked)
            {
                continue;
            }

            var tile = junction.Tiles.Count > 0 ? junction.Tiles[0] : npc.Tile;
            var distance = HexSpatialMath.HexDistance(npc.Tile, tile);
            if (distance is < 3 or > 8)
            {
                continue;
            }

            // Spec 29C.4A: avoid places where we were recently attacked.
            var dangerous = false;
            foreach (var danger in npc.Memory.Dangers)
            {
                if (HexSpatialMath.HexDistance(tile, danger.Tile) <= 3)
                {
                    dangerous = true;
                    break;
                }
            }

            if (!dangerous)
            {
                _exploreCandidates.Add(junction);
            }
        }

        if (_exploreCandidates.Count == 0)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Explore);
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=Explore NoCandidateJunctions");
            return;
        }

        var pick = (int)(MathUtil.Hash01(world.Seed, world.Tick, npc.Id.Value, 991) * _exploreCandidates.Count);
        pick = System.Math.Min(pick, _exploreCandidates.Count - 1);
        var destination = _exploreCandidates[pick];

        // Reachability check: destination must connect to where we stand.
        if (npc.CurrentJunction is not { } startJunction ||
            !Connectivity.Reachable(world, startJunction, destination.Id))
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Explore);
            Trace.Emit(world, npc.Id, "PlanFailed",
                $"Goal=Explore Destination={destination.Id.Value} unreachable");
            return;
        }

        npc.Plan.TargetJunctionId = destination.Id;
        npc.Plan.TargetTile = destination.Tiles.Count > 0 ? destination.Tiles[0] : null;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = destination.Id
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        Trace.Emit(world, npc.Id, "ExplorePlanned",
            $"To Junction={destination.Id.Value} " +
            $"Tile={Trace.FormatTile(npc.Plan.TargetTile)} Steps=[MoveToJunction]");
    }

    // Spec 28.15A: walk to a free neighbor junction of the target agent, then Talk.
    private void BuildTalkPlan(WorldState world, NPCState npc)
    {
        // Handshake (spec 28.8): if someone is already coming to talk to us,
        // wait for them instead of initiating our own approach.
        if (npc.Mind.PendingTalkFrom is { } incoming)
        {
            npc.Plan.Status = PlanStatus.Completed;
            npc.Mind.CurrentGoal = GoalType.None;
            Trace.Emit(world, npc.Id, "PlanNoInteraction",
                $"Goal=Socialize WaitingForTalkFrom=NPC{incoming.Value}");
            return;
        }

        // Spec 28.6 (iteration 8): prefer the most-liked available partner;
        // distance only breaks ties. Friendship self-selects.
        PerceivedAgent? target = null;
        foreach (var agent in npc.Perception.Agents)
        {
            if (!agent.IsReachable || agent.IsBusy || agent.IsMoving ||
                agent.IsUnconscious) // §60: never plan a chat with a body
            {
                continue;
            }

            // Skip targets already claimed by another initiator.
            if (world.Entities.Npcs.TryGetValue(agent.Id, out var agentState) &&
                agentState.Mind.PendingTalkFrom is { } claimedBy &&
                !claimedBy.Equals(npc.Id))
            {
                continue;
            }

            if (target is null ||
                agent.Relationship.Affinity > target.Relationship.Affinity + 0.01f ||
                (System.Math.Abs(agent.Relationship.Affinity - target.Relationship.Affinity) <= 0.01f &&
                 agent.Distance < target.Distance))
            {
                target = agent;
            }
        }

        if (target?.Junction is not { } targetJunction)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Socialize);
            Trace.Emit(world, npc.Id, "PlanFailed",
                "Goal=Socialize NoApproachableAgent");
            return;
        }

        // Spec 28.8: talk at arm's length — a free junction ~0.9 hex radius
        // from the partner, on the initiator's side, not the adjacent
        // sub-grid point (that reads as standing inside each other).
        JunctionId? approach = null;
        if (world.Entities.Npcs.TryGetValue(target.Id, out var partnerState))
        {
            var toMe = HexSpatialMath.Normalize(new Float2(
                npc.Position.X - partnerState.Position.X,
                npc.Position.Y - partnerState.Position.Y));
            var spot = new Float2(
                partnerState.Position.X + toMe.X * HexSpatialMath.HexRadius * 0.9f,
                partnerState.Position.Y + toMe.Y * HexSpatialMath.HexRadius * 0.9f);
            if (SpatialQueries.FindNearestJunction(world, spot) is { } armsLength &&
                !armsLength.Equals(targetJunction) &&
                SpatialQueries.IsJunctionFree(world, armsLength) &&
                SpatialMutations.TryReserveJunction(world, armsLength, npc.Id, world.Tick, 48))
            {
                approach = armsLength;
            }
        }

        if (approach is null)
        {
            foreach (var neighbor in SpatialQueries.GetPassableNeighbors(world, targetJunction))
            {
                if (SpatialQueries.IsJunctionFree(world, neighbor) &&
                    SpatialMutations.TryReserveJunction(world, neighbor, npc.Id, world.Tick, 48))
                {
                    approach = neighbor;
                    break;
                }
            }
        }

        if (approach is not { } approachJunction)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Socialize);
            Trace.Emit(world, npc.Id, "PlanFailed",
                $"Goal=Socialize Target={target.Id.Value} NoFreeApproachJunction");
            return;
        }

        npc.Plan.TargetAgentId = target.Id;
        npc.Plan.TargetJunctionId = approachJunction;
        npc.Plan.TargetTile = target.Tile;
        if (world.Entities.Npcs.TryGetValue(target.Id, out var claimedTarget))
        {
            claimedTarget.Mind.PendingTalkFrom = npc.Id;
            claimedTarget.Mind.PendingTalkSinceTick = world.Tick;
            SocialCueSignals.Stamp(world, npc, "TalkRequest", target.Id);
            SocialCueSignals.Stamp(world, claimedTarget, "TalkIncoming", npc.Id);
            Trace.Emit(world, npc.Id, "TalkRequested",
                $"Asked NPC{target.Id.Value} to talk " +
                $"Affinity={npc.Social.GetOrCreate(target.Id).Affinity:F2}");
        }

        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approachJunction
        });
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.Interact,
            TargetJunction = approachJunction,
            Interaction = InteractionType.Talk
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        Trace.Emit(world, npc.Id, "PlanBuilt",
            $"Goal=Socialize Target=NPC{target.Id.Value} " +
            $"ApproachJunction={approachJunction.Value} Steps=[MoveToJunction,Talk]");
    }

    // Spec §53: which aid interaction serves this kind of suffering.
    private static InteractionType AidInteraction(AidKind kind) => kind switch
    {
        AidKind.Feed => InteractionType.FeedOther,
        AidKind.Hydrate => InteractionType.HydrateOther,
        AidKind.Treat => InteractionType.TreatOther,
        AidKind.Medicate => InteractionType.MedicateOther,
        _ => InteractionType.ConsoleOther
    };

    // Spec §53: walk to a suffering housemate and help. Mirrors BuildTalkPlan
    // but selects the WORST-OFF reachable neighbour (highest Suffering) rather
    // than the most-liked, and claims her with PendingAidFrom so she holds still.
    private void BuildAidPlan(WorldState world, NPCState npc)
    {
        // If someone is already coming to help US, don't set off ourselves.
        if (npc.Mind.PendingAidFrom is { } incoming)
        {
            npc.Plan.Status = PlanStatus.Completed;
            npc.Mind.CurrentGoal = GoalType.None;
            Trace.Emit(world, npc.Id, "PlanNoInteraction",
                $"Goal=Aid WaitingForAidFrom=NPC{incoming.Value}");
            return;
        }

        PerceivedAgent? target = null;
        foreach (var agent in npc.Perception.Agents)
        {
            if (agent.AidKind == AidKind.None || agent.Suffering < Spec53.SufferingThreshold ||
                !agent.IsReachable || agent.IsBusy || agent.IsMoving)
            {
                continue;
            }

            // Skip a sufferer another helper is already on the way to.
            if (world.Entities.Npcs.TryGetValue(agent.Id, out var agentState) &&
                agentState.Mind.PendingAidFrom is { } claimedBy &&
                !claimedBy.Equals(npc.Id))
            {
                continue;
            }

            if (target is null || agent.Suffering > target.Suffering + 0.001f ||
                (System.Math.Abs(agent.Suffering - target.Suffering) <= 0.001f &&
                 agent.Distance < target.Distance))
            {
                target = agent;
            }
        }

        if (target?.Junction is not { } targetJunction)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Aid);
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=Aid NoReachableSufferer");
            return;
        }

        // Help at arm's length — a free junction ~0.9 hex radius from her, on
        // our side (same geometry as a talk approach).
        JunctionId? approach = null;
        if (world.Entities.Npcs.TryGetValue(target.Id, out var partnerState))
        {
            var toMe = HexSpatialMath.Normalize(new Float2(
                npc.Position.X - partnerState.Position.X,
                npc.Position.Y - partnerState.Position.Y));
            var spot = new Float2(
                partnerState.Position.X + toMe.X * HexSpatialMath.HexRadius * 0.9f,
                partnerState.Position.Y + toMe.Y * HexSpatialMath.HexRadius * 0.9f);
            if (SpatialQueries.FindNearestJunction(world, spot) is { } armsLength &&
                !armsLength.Equals(targetJunction) &&
                SpatialQueries.IsJunctionFree(world, armsLength) &&
                SpatialMutations.TryReserveJunction(world, armsLength, npc.Id, world.Tick, 48))
            {
                approach = armsLength;
            }
        }

        if (approach is null)
        {
            foreach (var neighbor in SpatialQueries.GetPassableNeighbors(world, targetJunction))
            {
                if (SpatialQueries.IsJunctionFree(world, neighbor) &&
                    SpatialMutations.TryReserveJunction(world, neighbor, npc.Id, world.Tick, 48))
                {
                    approach = neighbor;
                    break;
                }
            }
        }

        if (approach is not { } approachJunction)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Aid);
            Trace.Emit(world, npc.Id, "PlanFailed",
                $"Goal=Aid Target={target.Id.Value} NoFreeApproachJunction");
            return;
        }

        var interaction = AidInteraction(target.AidKind);
        npc.Plan.TargetAgentId = target.Id;
        npc.Plan.TargetJunctionId = approachJunction;
        npc.Plan.TargetTile = target.Tile;
        if (world.Entities.Npcs.TryGetValue(target.Id, out var claimedTarget))
        {
            claimedTarget.Mind.PendingAidFrom = npc.Id;
            claimedTarget.Mind.PendingAidSinceTick = world.Tick;
            SocialCueSignals.Stamp(world, npc, "AidRequest", target.Id);
            SocialCueSignals.Stamp(world, claimedTarget, "AidIncoming", npc.Id);
            Trace.Emit(world, npc.Id, "AidRequested",
                $"Going to help NPC{target.Id.Value} Kind={target.AidKind} " +
                $"Suffering={target.Suffering:F2}");
        }

        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approachJunction
        });
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.Interact,
            TargetJunction = approachJunction,
            Interaction = interaction
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        Trace.Emit(world, npc.Id, "PlanBuilt",
            $"Goal=Aid Kind={target.AidKind} Target=NPC{target.Id.Value} " +
            $"Suffering={target.Suffering:F2} ApproachJunction={approachJunction.Value} " +
            $"Steps=[MoveToJunction,{interaction}]");
    }

    private void BuildDefendPlan(WorldState world, NPCState npc)
    {
        JunctionId? attackerJunction = null;
        TileCoord attackerTile = npc.Tile;
        var label = string.Empty;

        if (npc.Mind.CombatAssistDogId is { } dogId)
        {
            foreach (var dog in world.Mobs)
            {
                if (dog.Id == dogId && dog.Health > 0f)
                {
                    attackerJunction = dog.Junction;
                    attackerTile = dog.Tile;
                    label = $"Dog={dog.Id}";
                    break;
                }
            }
        }
        else if (npc.Mind.CombatAssistAttackerNpcId is { } attackerId &&
                 world.Entities.Npcs.TryGetValue(attackerId, out var attacker) &&
                 attacker.Health > 0f)
        {
            attackerJunction = attacker.CurrentJunction;
            attackerTile = attacker.Tile;
            label = $"Attacker=NPC{attacker.Id.Value}";
        }

        if (attackerJunction is not { } target)
        {
            npc.Plan.Status = PlanStatus.Failed;
            npc.Mind.CurrentGoal = GoalType.None;
            npc.Mind.CombatAssistDogId = null;
            npc.Mind.CombatAssistAttackerNpcId = null;
            Trace.Emit(world, npc.Id, "HelpCryAssistLost", "Attacker vanished before defender arrived");
            return;
        }

        JunctionId? approach = null;
        if (npc.CurrentJunction is { } current &&
            (current.Equals(target) ||
             (world.Junctions.Items.TryGetValue(target, out var targetJ) &&
              targetJ.Neighbors.Contains(current))))
        {
            approach = current;
        }
        else
        {
            foreach (var neighbor in SpatialQueries.GetPassableNeighbors(world, target))
            {
                if (SpatialQueries.IsJunctionFree(world, neighbor) &&
                    SpatialMutations.TryReserveJunction(world, neighbor, npc.Id, world.Tick, 48))
                {
                    approach = neighbor;
                    break;
                }
            }
        }

        if (approach is not { } approachJunction)
        {
            npc.Plan.Status = PlanStatus.Failed;
            PlanningSystem.SetGoalCooldown(world, npc, GoalType.Defend);
            npc.Mind.CurrentGoal = GoalType.None;
            Trace.Emit(world, npc.Id, "PlanFailed",
                $"Goal=Defend {label} NoFreeApproachJunction");
            return;
        }

        npc.Plan.TargetJunctionId = approachJunction;
        npc.Plan.TargetTile = attackerTile;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approachJunction
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        SocialCueSignals.Stamp(world, npc, "HelpCryAssistStarted", npc.Id);
        Trace.Emit(world, npc.Id, "HelpCryAssistStarted",
            $"{label} ApproachJunction={approachJunction.Value} Tile={attackerTile.Q},{attackerTile.R}");
    }
}

}
