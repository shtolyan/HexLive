using System;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Social;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

public sealed partial class ExecutionSystem
{
    private static string? ContactBlocker(WorldState world, NPCState npc, NPCState target)
    {
        if (npc.Health <= 0f || target.Health <= 0f || npc.IsDying || target.IsDying ||
            npc.IsLyingDown(world.Tick) || target.IsLyingDown(world.Tick) ||
            npc.IsBeingCarried || target.IsBeingCarried || npc.IsCarryingPerson || target.IsCarryingPerson)
            return "ContactNeedsStanding";
        if (npc.IsFighting || target.IsFighting) return "ContactFighting";
        if (target.Mind.IsStarving || target.Mind.IsDehydrated) return "ContactUrgentNeed";
        if (target.Mind.ExternalControl?.IsActive == true || ManualControlMath.IsManual(target))
            return "ContactControlled";
        if (target.Movement.IsMoving || target.Execution.Status == ExecutionStatus.InProgress)
            return "ContactBusy";
        if (target.Mind.PendingTalkFrom != npc.Id) return "ContactInvitationLost";
        if (HexSpatialMath.Distance(npc.Position, target.Position) > InteractionReach.Talk)
            return "ContactTooFar";
        return null;
    }

    private static void StartSocialContact(WorldState world, NPCState npc, NPCState target, TalkTopic request)
    {
        if (ContactBlocker(world, npc, target) is { } blocker)
        {
            SocialCueSignals.Stamp(world, npc, blocker, target.Id);
            FinishSocialContact(world, npc, target);
            return;
        }

        var relation = target.Social.GetOrCreate(npc.Id);
        var friendly = !FactionRelations.AreHostile(world, target, npc);
        var result = request switch
        {
            TalkTopic.Kiss => friendly && relation.Affinity >= .42f && relation.Trust >= .25f &&
                relation.Familiarity >= .28f ? TalkTopic.KissAccepted : TalkTopic.KissDeclined,
            TalkTopic.PassionateKiss => friendly && relation.Affinity >= .62f && relation.Trust >= .4f &&
                relation.Familiarity >= .42f ? TalkTopic.PassionateKissAccepted : TalkTopic.PassionateKissDeclined,
            TalkTopic.FlirtGesture => friendly && relation.Affinity >= .15f
                ? TalkTopic.FlirtAccepted : TalkTopic.FlirtDeclined,
            TalkTopic.Dance => friendly && relation.Affinity >= .08f
                ? TalkTopic.DanceAccepted : TalkTopic.DanceDeclined,
            TalkTopic.Greet => friendly ? TalkTopic.GreetAccepted : TalkTopic.GreetDeclined,
            TalkTopic.Apologize when relation.Affinity < .15f || relation.Trust < .2f
                => TalkTopic.ApologyAccepted,
            TalkTopic.Hug => friendly && relation.Affinity >= .08f && relation.Familiarity >= .10f
                ? TalkTopic.HugAccepted : TalkTopic.HugDeclined,
            _ => (TalkTopic?)null
        };
        if (result is not { } outcome)
        {
            SocialCueSignals.Stamp(world, npc, request == TalkTopic.Apologize
                ? "ContactApologyNotNeeded" : "ContactUnavailable", target.Id);
            FinishSocialContact(world, npc, target);
            return;
        }
        npc.Plan.RequestedTalkTopic = result;
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.CurrentInteraction = InteractionType.Talk;
        npc.Execution.StartTick = world.Tick;
        npc.Execution.EndTick = world.Tick + Math.Max(1,
            (int)Math.Ceiling(SocialContact.Duration(outcome) / Math.Max(0.001f, world.TickDeltaTime)));
        npc.Execution.CurrentTalkTopic = result;
        npc.Execution.CurrentTalkTopicPeerId = target.Id;
        target.Mind.PendingTalkSinceTick = world.Tick;
        if (npc.Plan.TargetJunctionId is { } at) SpatialMutations.OccupyJunction(world, at, npc.Id);
        SocialCueSignals.Stamp(world, target, outcome.ToString(), npc.Id);
    }

    private static void RunSocialContact(WorldState world, NPCState npc, NPCState target, TalkTopic result)
    {
        if (ContactBlocker(world, npc, target) is not null)
        {
            FinishSocialContact(world, npc, target);
            return;
        }
        // The outcome lives in the saved plan. Restore the presentation after loading an active scene.
        npc.Execution.CurrentTalkTopic = result;
        npc.Execution.CurrentTalkTopicPeerId = target.Id;
        if (world.Tick < npc.Execution.EndTick) return;

        if (SocialContact.IsAccepted(result))
        {
            ApplyContactGain(world, npc, target, result);
            ApplyContactGain(world, target, npc, result);
        }
        npc.Social.MarkInteraction(target.Id, world.Tick);
        target.Social.MarkInteraction(npc.Id, world.Tick);
        FinishSocialContact(world, npc, target);
    }

    private static void ApplyContactGain(WorldState world, NPCState actor, NPCState peer, TalkTopic result)
    {
        var relation = actor.Social.GetOrCreate(peer.Id);
        if (result is TalkTopic.KissAccepted or TalkTopic.PassionateKissAccepted or TalkTopic.FlirtAccepted)
        {
            var before = relation.Affinity;
            var gain = result == TalkTopic.FlirtAccepted ? .045f : SocialContact.KissAffinityGain;
            relation.Affinity = MathUtil.Clamp(relation.Affinity + gain, -1f, 1f);
            actor.Execution.LastTalkAffinityDelta = relation.Affinity - before;
            actor.Execution.LastTalkResultTick = world.Tick;
            SocialCueSignals.Stamp(world, actor, "KissBond", peer.Id);
        }
        else if (result == TalkTopic.HugAccepted)
        {
            relation.Trust = MathUtil.Clamp01(relation.Trust + SocialContact.HugFriendshipGain);
            relation.Familiarity = MathUtil.Clamp01(relation.Familiarity + SocialContact.HugFriendshipGain);
            SocialCueSignals.Stamp(world, actor, "HugFriendship", peer.Id);
        }
        else
        {
            var gain = result == TalkTopic.ApologyAccepted ? .045f : .025f;
            relation.Trust = MathUtil.Clamp01(relation.Trust + gain);
            if (result == TalkTopic.ApologyAccepted)
                relation.Affinity = MathUtil.Clamp(relation.Affinity + gain, -1f, 1f);
            else
                relation.Familiarity = MathUtil.Clamp01(relation.Familiarity + gain);
        }
    }

    private static void FinishSocialContact(WorldState world, NPCState npc, NPCState target)
    {
        if (target.Mind.PendingTalkFrom == npc.Id) target.Mind.PendingTalkFrom = null;
        if (npc.Plan.TargetJunctionId is { } at)
        {
            SpatialMutations.FreeJunction(world, at, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, at, npc.Id);
        }
        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetAgentId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Plan.RequestedTalkTopic = null;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.CurrentTalkTopic = null;
        npc.Execution.CurrentTalkTopicPeerId = null;
        npc.Execution.StartTick = npc.Execution.EndTick = 0;
        npc.Execution.LastCompletedTick = world.Tick;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;
        PlanningSystem.SetGoalCooldown(world, npc, GoalType.Socialize);
    }
}

}
