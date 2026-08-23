using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

public sealed partial class ExecutionSystem
{
    private static bool TryContinueTalkAsRomance(
        WorldState world, NPCState leader, NPCState partner)
    {
        if (!RomanceMath.CanConsent(world, leader, partner) ||
            MathUtil.Hash01(world.Seed, world.Tick,
                leader.Id.Value, partner.Id.Value) >= Spec127.AutonomousAfterTalkChance)
        {
            return false;
        }

        PrepareImmediateRomance(world, leader, partner, forced: false);
        return true;
    }

    private static bool TryContinueAbuseAsForcedRomance(
        WorldState world, NPCState leader, NPCState victim)
    {
        if (!RomanceMath.CanForce(world, leader, victim) ||
            MathUtil.Hash01(world.Seed, world.Tick,
                victim.Id.Value, leader.Id.Value) >= Spec127.ForcedAfterAbuseChance)
        {
            return false;
        }

        PrepareImmediateRomance(world, leader, victim, forced: true);
        return true;
    }

    private static void PrepareImmediateRomance(WorldState world, NPCState leader,
        NPCState partner, bool forced)
    {
        leader.Plan.Goal = GoalType.Romance;
        leader.Plan.Status = PlanStatus.Active;
        leader.Plan.TargetAgentId = partner.Id;
        leader.Plan.Steps.Clear();
        leader.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.Interact,
            TargetJunction = leader.CurrentJunction,
            Interaction = InteractionType.Romance
        });
        leader.Mind.CurrentGoal = GoalType.Romance;
        leader.Mind.RomancePartnerNpcId = partner.Id;
        leader.Mind.RomanceLeaderNpcId = leader.Id;
        leader.Mind.RomanceForced = forced;
        leader.Execution.Status = ExecutionStatus.None;
        leader.Execution.CurrentInteraction = null;
        leader.Execution.StartTick = 0;
        leader.Execution.EndTick = 0;
        partner.Mind.PendingTalkFrom = null;
        partner.Mind.PendingRomanceFrom = leader.Id;
        partner.Mind.PendingRomanceSinceTick = world.Tick;
        SocialCueSignals.Stamp(world, leader,
            forced ? "RomanceForcedRequest" : "RomanceRequest", partner.Id);
        SocialCueSignals.Stamp(world, partner,
            forced ? "RomanceForcedIncoming" : "RomanceIncoming", leader.Id);
    }

    private static void RunRomance(WorldState world, NPCState leader)
    {
        if (leader.Plan.TargetAgentId is not { } partnerId ||
            !world.Entities.Npcs.TryGetValue(partnerId, out var partner))
        {
            AbortRomancePlan(world, leader, "PartnerVanished", false);
            return;
        }

        if (leader.Movement.IsMoving ||
            (leader.Movement.Status != MovementStatus.Arrived &&
             leader.Movement.JunctionPath.Count > 0))
        {
            return;
        }

        if (leader.Execution.Status == ExecutionStatus.None &&
            leader.Movement.Status == MovementStatus.Blocked)
        {
            AbortRomancePlan(world, leader, "ApproachBlocked", false);
            return;
        }

        if (leader.Execution.Status == ExecutionStatus.None &&
            leader.Plan.TargetJunctionId is { } wanted &&
            (leader.CurrentJunction is not { } current || !current.Equals(wanted)))
        {
            return;
        }

        var forced = leader.Mind.RomanceForced;
        if (leader.Execution.Status == ExecutionStatus.None)
        {
            if (!InteractionReach.CheckStart(world, leader, partner.Position,
                    InteractionReach.Talk, $"Romance NPC{partner.Id.Value}"))
            {
                AbortRomancePlan(world, leader, "PartnerOutOfRange", false);
                return;
            }

            var eligible = forced
                ? RomanceMath.CanForce(world, leader, partner)
                : RomanceMath.CanConsent(world, leader, partner);
            if (!eligible)
            {
                SocialCueSignals.Stamp(world, leader, "RomanceRejected", partner.Id);
                SocialCueSignals.Stamp(world, partner, "RomanceRefused", leader.Id);
                AbortRomancePlan(world, leader,
                    forced ? "ForcedSceneNoLongerValid" : "ConsentWithdrawn", false);
                return;
            }

            // The invitation owns both bodies once accepted. A manual target
            // may refuse that scene takeover through NpcControlPolicy.
            if ((partner.Plan.Status == PlanStatus.Active ||
                 partner.Execution.Status == ExecutionStatus.InProgress) &&
                !PlanInterruption.TryAbort(world, partner,
                    forced ? InterruptionCause.AbuseMark : InterruptionCause.SceneInitiator,
                    $"Romance with NPC{leader.Id.Value}"))
            {
                AbortRomancePlan(world, leader, "PartnerBusy", false);
                return;
            }
            partner.Mind.CurrentGoal = GoalType.None;

            var female = leader.Sex == GarmentSex.Female ? leader : partner;
            var male = leader.Sex == GarmentSex.Male ? leader : partner;
            if (!DropRomanceGarments(world, female) ||
                !DropRomanceGarments(world, male))
            {
                AbortRomancePlan(world, leader, "CannotLayGarmentsDown", false);
                return;
            }

            var placement = RomanceMath.PickPlacement(world, female, male);
            MirrorRomanceState(world, leader, partner, placement, forced);
            if (leader.Plan.TargetJunctionId is { } occupied)
            {
                SpatialMutations.OccupyJunction(world, occupied, leader.Id);
            }

            if (forced)
            {
                // Same unconditional witnessed-assault response as searching a
                // helpless body: nearby squad members may break the pair.
                CombatHelpSystem.RallyLootWitnesses(world, partner, leader.Id);
                CombatHelpSystem.RallyFriends(world, partner, null, leader.Id,
                    $"ForcedRomance=NPC{leader.Id.Value}");
            }

            SocialCueSignals.Stamp(world, leader,
                forced ? "RomanceForcedStarted" : "RomanceStarted", partner.Id);
            SocialCueSignals.Stamp(world, partner,
                forced ? "RomanceResisting" : "RomanceStarted", leader.Id);
            Trace.Emit(world, leader.Id, "RomanceStarted",
                $"Partner=NPC{partner.Id.Value} Forced={forced} " +
                $"Pose={placement.Key} Duration={Spec127.DurationTicks}");
            return;
        }

        if (leader.IsFighting || partner.IsFighting ||
            leader.Mind.CombatOpponentNpcId is not null ||
            partner.Mind.CombatOpponentNpcId is not null)
        {
            AbortRomancePlan(world, leader, "CombatStarted", forced);
            return;
        }

        if (leader.Health <= 0f || partner.Health <= 0f ||
            leader.IsUnconscious(world.Tick) || partner.IsUnconscious(world.Tick))
        {
            AbortRomancePlan(world, leader, "ParticipantIncapacitated", forced);
            return;
        }

        if (world.Tick < leader.Execution.EndTick)
        {
            return;
        }

        CompleteRomance(world, leader, partner, forced);
    }

    private static void MirrorRomanceState(WorldState world, NPCState leader,
        NPCState partner, RomanceMath.Placement placement, bool forced)
    {
        foreach (var participant in new[] { leader, partner })
        {
            var other = participant.Id.Equals(leader.Id) ? partner : leader;
            participant.Execution.Status = ExecutionStatus.InProgress;
            participant.Execution.CurrentInteraction = InteractionType.Romance;
            participant.Execution.TargetObject = null;
            participant.Execution.StartTick = world.Tick;
            participant.Execution.EndTick = world.Tick + Spec127.DurationTicks;
            participant.Mind.RomancePartnerNpcId = other.Id;
            participant.Mind.RomanceLeaderNpcId = leader.Id;
            participant.Mind.RomanceClipKey = placement.Key;
            participant.Mind.RomanceForced = forced;
            participant.Mind.RomanceAnchorX = placement.Anchor.X;
            participant.Mind.RomanceAnchorY = placement.Anchor.Y;
            participant.Mind.RomanceFacingDegrees = placement.FacingDegrees;
        }

        partner.Mind.PendingRomanceFrom = null;
    }

    private static bool DropRomanceGarments(WorldState world, NPCState npc)
    {
        var changed = false;
        for (var i = npc.WornItems.Count - 1; i >= 0; i--)
        {
            var garment = npc.WornItems[i];
            if (!RomanceMath.MustRemoveForRomance(world, npc, garment)) continue;
            npc.WornItems.RemoveAt(i);
            if (DropGarmentWithContents(world, npc, garment) is null)
            {
                npc.WornItems.Insert(i, garment);
                EquipmentMath.Recalculate(world, npc);
                return false;
            }
            changed = true;
        }

        if (changed) EquipmentMath.Recalculate(world, npc);
        return true;
    }

    private static void CompleteRomance(WorldState world, NPCState leader,
        NPCState partner, bool forced)
    {
        var female = leader.Sex == GarmentSex.Female ? leader : partner;
        female.Body.Condition(BodyPart.Pelvis).IntimacySoil = 1f;

        if (forced)
        {
            leader.Needs.Social = MathUtil.Clamp01(
                leader.Needs.Social + Spec127.ForcedInitiatorSocialGain);
            partner.Needs.Social = MathUtil.Clamp01(
                partner.Needs.Social - Spec127.ForcedVictimSocialLoss);
            var victimRel = partner.Social.GetOrCreate(leader.Id);
            victimRel.Affinity = MathUtil.Clamp(
                victimRel.Affinity - Spec127.ForcedAffinityLoss, -1f, 1f);
            victimRel.Trust = MathUtil.Clamp(
                victimRel.Trust - Spec127.ForcedTrustLoss, -1f, 1f);
            RomanceMath.ApplyForcedPelvisDamage(world, partner, leader);
            partner.Mind.CryingUntilTick = System.Math.Max(
                partner.Mind.CryingUntilTick,
                world.Tick + Spec127.VictimCryingTicks);
            partner.Execution.LastTalkResultTick = world.Tick;
            partner.Execution.LastTalkAffinityDelta = -Spec127.ForcedAffinityLoss;
            SocialCueSignals.Stamp(world, partner, "RomanceTraumatized", leader.Id);
        }
        else
        {
            leader.Needs.Social = MathUtil.Clamp01(
                leader.Needs.Social + Spec127.ConsentSocialGain);
            partner.Needs.Social = MathUtil.Clamp01(
                partner.Needs.Social + Spec127.ConsentSocialGain);
            ImproveMutualRelationship(leader, partner);
            ImproveMutualRelationship(partner, leader);
            SocialCueSignals.Stamp(world, leader, "RomanceCompleted", partner.Id);
            SocialCueSignals.Stamp(world, partner, "RomanceCompleted", leader.Id);
        }

        Trace.Emit(world, leader.Id, "RomanceCompleted",
            $"Partner=NPC{partner.Id.Value} Forced={forced} " +
            $"Pose={leader.Mind.RomanceClipKey}");
        FinishRomancePair(world, leader, partner);
    }

    private static void ImproveMutualRelationship(NPCState source, NPCState target)
    {
        var rel = source.Social.GetOrCreate(target.Id);
        rel.Affinity = MathUtil.Clamp(
            rel.Affinity + Spec127.ConsentAffinityGain, -1f, 1f);
        rel.Trust = MathUtil.Clamp(
            rel.Trust + Spec127.ConsentTrustGain, -1f, 1f);
        rel.Familiarity = MathUtil.Clamp01(
            rel.Familiarity + Spec127.ConsentFamiliarityGain);
    }

    private static void FinishRomancePair(WorldState world, NPCState leader,
        NPCState partner)
    {
        if (leader.Plan.TargetJunctionId is { } jId)
        {
            SpatialMutations.FreeJunction(world, jId, leader.Id);
            SpatialMutations.ReleaseJunctionReservation(world, jId, leader.Id);
        }

        leader.Plan.Status = PlanStatus.Completed;
        leader.Plan.Steps.Clear();
        leader.Plan.TargetAgentId = null;
        leader.Plan.TargetJunctionId = null;
        leader.Plan.TargetTile = null;
        leader.Mind.CurrentGoal = GoalType.None;
        leader.Movement.JunctionPath.Clear();
        leader.Movement.PathIndex = 0;
        ClearRomanceState(world, leader);
        ClearRomanceState(world, partner);
    }

    private static void AbortRomancePlan(WorldState world, NPCState leader,
        string reason, bool leaveVictimCrying)
    {
        AbortRomancePair(world, leader, reason, leaveVictimCrying);
        PlanInterruption.TryAbortForCombat(world, leader,
            InterruptionCause.ExecutionFailure, reason);
        leader.Mind.CurrentGoal = GoalType.None;
        PlanningSystem.SetGoalCooldown(world, leader, GoalType.Romance);
    }

    internal static void AbortRomancePair(WorldState world, NPCState participant,
        string reason, bool leaveVictimCrying)
    {
        NPCState partner = null;
        if (participant.Mind.RomancePartnerNpcId is { } partnerId)
        {
            world.Entities.Npcs.TryGetValue(partnerId, out partner);
        }

        if (leaveVictimCrying && participant.Mind.RomanceForced)
        {
            var victim = participant.Sex == GarmentSex.Female ? participant : partner;
            if (victim != null)
            {
                victim.Mind.CryingUntilTick = System.Math.Max(
                    victim.Mind.CryingUntilTick,
                    world.Tick + Spec127.VictimCryingTicks);
                SocialCueSignals.Stamp(world, victim, "RomanceInterruptedCrying",
                    participant.Sex == GarmentSex.Male ? participant.Id : partner?.Id);
            }
        }

        ClearRomanceState(world, participant);
        if (partner != null) ClearRomanceState(world, partner);
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, participant.Id, "RomanceAborted", reason);
        }
    }

    private static void ClearRomanceState(WorldState world, NPCState npc)
    {
        npc.Mind.PendingRomanceFrom = null;
        npc.Mind.RomancePartnerNpcId = null;
        npc.Mind.RomanceLeaderNpcId = null;
        npc.Mind.RomanceClipKey = string.Empty;
        npc.Mind.RomanceForced = false;
        npc.Mind.RomanceAnchorX = 0f;
        npc.Mind.RomanceAnchorY = 0f;
        npc.Mind.RomanceFacingDegrees = 0f;
        npc.Mind.RomanceCooldownUntilTick = System.Math.Max(
            npc.Mind.RomanceCooldownUntilTick, world.Tick + Spec127.CooldownTicks);
        if (npc.Execution.CurrentInteraction == InteractionType.Romance)
        {
            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.CurrentInteraction = null;
            npc.Execution.StartTick = 0;
            npc.Execution.EndTick = 0;
        }
    }
}

}
