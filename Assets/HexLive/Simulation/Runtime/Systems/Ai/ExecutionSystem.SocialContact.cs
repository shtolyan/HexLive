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
    private static bool CanStageContact(WorldState world, NPCState npc, NPCState target) =>
        npc.Health > 0f && target.Health > 0f && !npc.IsDying && !target.IsDying &&
        !npc.IsFighting && !target.IsFighting && !npc.IsLyingDown(world.Tick) &&
        !target.IsLyingDown(world.Tick) && !target.Movement.IsMoving &&
        !npc.IsBeingCarried && !target.IsBeingCarried && !npc.IsCarryingPerson && !target.IsCarryingPerson &&
        !target.Mind.IsStarving && !target.Mind.IsDehydrated &&
        target.Mind.ExternalControl?.IsActive != true && !ManualControlMath.IsManual(target) &&
        target.Execution.Status != ExecutionStatus.InProgress && target.Mind.PendingTalkFrom == npc.Id &&
        HexSpatialMath.Distance(npc.Position, target.Position) <= InteractionReach.Talk;

    private static void StartSocialContact(WorldState world, NPCState npc, NPCState target, TalkTopic request)
    {
        var kiss = request == TalkTopic.Kiss;
        if (!CanStageContact(world, npc, target))
        {
            SocialCueSignals.Stamp(world, npc, kiss ? "KissUnavailable" : "HugUnavailable", target.Id);
            FinishSocialContact(world, npc, target);
            return;
        }

        var relation = target.Social.GetOrCreate(npc.Id);
        var accepted = !FactionRelations.AreHostile(world, target, npc) && (kiss
            ? relation.Affinity >= 0.42f && relation.Trust >= 0.25f && relation.Familiarity >= 0.28f
            : relation.Affinity >= 0.08f && relation.Familiarity >= 0.10f);
        var result = kiss
            ? (accepted ? TalkTopic.KissAccepted : TalkTopic.KissDeclined)
            : (accepted ? TalkTopic.HugAccepted : TalkTopic.HugDeclined);
        npc.Plan.RequestedTalkTopic = result;
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.CurrentInteraction = InteractionType.Talk;
        npc.Execution.StartTick = world.Tick;
        npc.Execution.EndTick = world.Tick + Math.Max(1,
            (int)Math.Ceiling(SocialContact.Duration(result) / Math.Max(0.001f, world.TickDeltaTime)));
        npc.Execution.CurrentTalkTopic = result;
        npc.Execution.CurrentTalkTopicPeerId = target.Id;
        target.Mind.PendingTalkSinceTick = world.Tick;
        if (npc.Plan.TargetJunctionId is { } at) SpatialMutations.OccupyJunction(world, at, npc.Id);
        SocialCueSignals.Stamp(world, target, result.ToString(), npc.Id);
    }

    private static void RunSocialContact(WorldState world, NPCState npc, NPCState target, TalkTopic result)
    {
        if (!CanStageContact(world, npc, target))
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
        if (result == TalkTopic.KissAccepted)
        {
            var before = relation.Affinity;
            relation.Affinity = MathUtil.Clamp(relation.Affinity + SocialContact.KissAffinityGain, -1f, 1f);
            actor.Execution.LastTalkAffinityDelta = relation.Affinity - before;
            actor.Execution.LastTalkResultTick = world.Tick;
            SocialCueSignals.Stamp(world, actor, "KissBond", peer.Id);
        }
        else
        {
            relation.Trust = MathUtil.Clamp01(relation.Trust + SocialContact.HugFriendshipGain);
            relation.Familiarity = MathUtil.Clamp01(relation.Familiarity + SocialContact.HugFriendshipGain);
            SocialCueSignals.Stamp(world, actor, "HugFriendship", peer.Id);
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
