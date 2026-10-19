using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Social;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{
public sealed partial class ExecutionSystem
{
    internal static bool IsRomanceInvitation(NPCState npc) =>
        !npc.Mind.RomanceForced && npc.Plan.Goal == GoalType.Romance &&
        npc.Plan.Steps.Count > 0 && npc.Plan.Steps[^1].Interaction == InteractionType.Talk;

    private static void RunRomanceInvitation(WorldState world, NPCState leader)
    {
        if (leader.Plan.TargetAgentId is not { } peer ||
            !world.Entities.Npcs.TryGetValue(peer, out var partner))
        {
            FinishRomanceInvitation(world, leader, null, "TargetUnavailable");
            return;
        }

        if (!RomanceMath.CanInvite(world, leader, partner) ||
            partner.Mind.PendingRomanceFrom != leader.Id)
        {
            FinishRomanceInvitation(world, leader, partner, "InvitationCancelled");
            return;
        }
        if (leader.Movement.Status == MovementStatus.Blocked)
        {
            FinishRomanceInvitation(world, leader, partner, "Unreachable");
            return;
        }
        if (leader.Movement.IsMoving ||
            (leader.Movement.Status != MovementStatus.Arrived && leader.Movement.JunctionPath.Count > 0)) return;
        if (leader.Execution.Status == ExecutionStatus.None &&
            leader.Plan.TargetJunctionId is { } wanted && leader.CurrentJunction != wanted) return;

        if (HexSpatialMath.Distance(leader.Position, partner.Position) > InteractionReach.Talk ||
            partner.Movement.IsMoving)
        {
            FinishRomanceInvitation(world, leader, partner, "PartnerUnavailable");
            return;
        }

        if (leader.Execution.Status == ExecutionStatus.None)
        {
            if ((partner.Plan.Status == PlanStatus.Active || partner.Execution.Status == ExecutionStatus.InProgress) &&
                !PlanInterruption.TryAbort(world, partner, InterruptionCause.SceneInitiator, "Romantic invitation conversation"))
            {
                FinishRomanceInvitation(world, leader, partner, "PartnerBusy");
                return;
            }
            partner.Mind.CurrentGoal = GoalType.None;
            partner.Mind.PendingRomanceSinceTick = world.Tick;
            leader.Execution.Status = ExecutionStatus.InProgress;
            leader.Execution.CurrentInteraction = InteractionType.Talk;
            leader.Execution.StartTick = world.Tick;
            leader.Execution.EndTick = world.Tick + Spec127.InvitationTalkTicks;
            leader.Execution.CurrentTalkTopic = partner.Execution.CurrentTalkTopic = TalkTopic.Flirt;
            leader.Execution.CurrentTalkTopicPeerId = partner.Id;
            partner.Execution.CurrentTalkTopicPeerId = leader.Id;
            var delta = HexSpatialMath.Normalize(new Float2(
                partner.Position.X - leader.Position.X, partner.Position.Y - leader.Position.Y));
            leader.RotationDegrees = HexSpatialMath.AngleDegrees(delta);
            partner.RotationDegrees = HexSpatialMath.AngleDegrees(new Float2(-delta.X, -delta.Y));
            if (leader.Plan.TargetJunctionId is { } at) SpatialMutations.OccupyJunction(world, at, leader.Id);
            SocialCueSignals.Stamp(world, leader, "RomanceRequest", partner.Id);
            SocialCueSignals.Stamp(world, partner, "RomanceIncoming", leader.Id);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, leader.Id, "RomanceInvitationStarted", $"Partner={partner.Id.Value} Duration={Spec127.InvitationTalkTicks}");
            }
            return;
        }
        if (partner.Execution.Status == ExecutionStatus.InProgress &&
            partner.Execution.CurrentInteraction != InteractionType.Talk)
        {
            FinishRomanceInvitation(world, leader, partner, "PartnerBusy");
            return;
        }
        if (world.Tick < leader.Execution.EndTick) return;

        leader.Social.MarkInteraction(partner.Id, world.Tick);
        partner.Social.MarkInteraction(leader.Id, world.Tick);
        if (!RomanceMath.CanConsent(world, leader, partner))
        {
            FinishRomanceInvitation(world, leader, partner, "NoMutualConsent");
            return;
        }

        ClearInvitationTopics(leader, partner);
        leader.Plan.RequestedTalkTopic = null;
        leader.Plan.Steps[^1].Interaction = InteractionType.Romance;
        // Successful phase transition, not an interruption: retain this
        // active plan, the listener claim and the occupied approach junction.
        leader.Execution.Status = ExecutionStatus.None;
        leader.Execution.CurrentInteraction = null;
        leader.Execution.StartTick = leader.Execution.EndTick = 0;
        leader.Mind.RomancePartnerNpcId = partner.Id;
        leader.Mind.RomanceLeaderNpcId = leader.Id;
        SocialCueSignals.Stamp(world, leader, "RomanceAccepted", partner.Id);
        SocialCueSignals.Stamp(world, partner, "RomanceAccepted", leader.Id);
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, leader.Id, "RomanceInvitationAccepted", $"Partner={partner.Id.Value}");
        }
    }

    private static void ClearInvitationTopics(NPCState leader, NPCState partner)
    {
        if (partner == null || leader.Execution.CurrentTalkTopicPeerId == partner.Id)
        {
            leader.Execution.CurrentTalkTopic = null;
            leader.Execution.CurrentTalkTopicPeerId = null;
        }
        if (partner != null && partner.Execution.CurrentTalkTopicPeerId == leader.Id)
        {
            partner.Execution.CurrentTalkTopic = null;
            partner.Execution.CurrentTalkTopicPeerId = null;
        }
    }

    internal static void ClearInterruptedRomanceInvitation(WorldState world, NPCState npc, InterruptionCause cause)
    {
        if (IsRomanceInvitation(npc))
        {
            world.Entities.Npcs.TryGetValue(npc.Plan.TargetAgentId ?? default, out var partner);
            ClearInvitationTopics(npc, partner);
            if (partner != null && partner.Mind.PendingRomanceFrom == npc.Id) partner.Mind.PendingRomanceFrom = null;
        }
        // Pausing an AI listener's previous job is part of accepting the
        // invitation. A new player order or emergency instead cancels it.
        if (cause != InterruptionCause.SceneInitiator && npc.Mind.PendingRomanceFrom is { } inviter &&
            world.Entities.Npcs.TryGetValue(inviter, out var leader) && IsRomanceInvitation(leader))
        {
            npc.Mind.PendingRomanceFrom = null;
            ClearInvitationTopics(leader, npc);
        }
    }

    private static void FinishRomanceInvitation(WorldState world, NPCState leader, NPCState partner, string reason)
    {
        ClearInvitationTopics(leader, partner);
        if (partner != null && partner.Mind.PendingRomanceFrom == leader.Id) partner.Mind.PendingRomanceFrom = null;
        if (leader.Plan.TargetJunctionId is { } at)
        {
            SpatialMutations.FreeJunction(world, at, leader.Id);
            SpatialMutations.ReleaseJunctionReservation(world, at, leader.Id);
        }
        // A refusal completes the conversation normally. This talk-only plan
        // owns the listener claim and approach released above, not an object,
        // craft project or carried item. Abort would report a failed order.
        leader.Plan.Status = PlanStatus.Completed;
        leader.Plan.Steps.Clear();
        leader.Plan.TargetAgentId = null;
        leader.Plan.TargetJunctionId = null;
        leader.Plan.TargetTile = null;
        leader.Plan.RequestedTalkTopic = null;
        leader.Execution.Status = ExecutionStatus.None;
        leader.Execution.CurrentInteraction = null;
        leader.Execution.StartTick = leader.Execution.EndTick = 0;
        leader.Execution.LastCompletedTick = world.Tick;
        leader.Mind.CurrentGoal = GoalType.None;
        leader.Movement.JunctionPath.Clear();
        leader.Movement.PathIndex = 0;
        leader.Movement.IsMoving = false;
        leader.Movement.SetStatus(MovementStatus.Idle);
        PlanningSystem.SetGoalCooldown(world, leader, GoalType.Romance);
        SocialCueSignals.Stamp(world, leader, "RomanceRejected", partner?.Id);
        if (partner != null) SocialCueSignals.Stamp(world, partner, "RomanceRefused", leader.Id);
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, leader.Id, "RomanceInvitationDeclined", $"Reason={reason} Partner={partner?.Id.Value}");
        }
    }
}
}
