using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;

namespace HexLive.Simulation.Runtime
{

public sealed partial class PlanningSystem
{
    private void BuildRomancePlan(WorldState world, NPCState npc)
    {
        if ((npc.Plan.TargetAgentId ?? npc.Mind.RomancePartnerNpcId) is not { } targetId ||
            !world.Entities.Npcs.TryGetValue(targetId, out var target) ||
            target.CurrentJunction is not { } targetJunction ||
            !TryInstallRomancePlan(world, npc, target, targetJunction,
                npc.Mind.RomanceForced, out _))
        {
            npc.Plan.Status = PlanStatus.Failed;
            npc.Mind.CurrentGoal = GoalType.None;
            SetGoalCooldown(world, npc, GoalType.Romance);
        }
    }

    internal static bool TryInstallRomancePlan(
        WorldState world, NPCState npc, NPCState partner,
        JunctionId partnerJunction, bool forced,
        out JunctionId approachJunction)
    {
        if (!TryInstallTalkPlan(world, npc, partner, partner.Id,
                partnerJunction, partner.Tile, out approachJunction))
        {
            return false;
        }

        // Replace the generic talk claim/cues with the stronger typed claim.
        if (partner.Mind.PendingTalkFrom == npc.Id)
        {
            partner.Mind.PendingTalkFrom = null;
        }
        partner.Mind.PendingRomanceFrom = npc.Id;
        partner.Mind.PendingRomanceSinceTick = world.Tick;
        npc.Plan.Steps[npc.Plan.Steps.Count - 1].Interaction =
            forced ? InteractionType.Romance : InteractionType.Talk;
        npc.Plan.RequestedTalkTopic = forced ? null : HexLive.Simulation.Social.TalkTopic.Flirt;
        npc.Plan.Goal = GoalType.Romance;
        npc.Mind.CurrentGoal = GoalType.Romance;
        // Scene ownership begins after an answer, not while walking over.
        npc.Mind.RomancePartnerNpcId = forced ? partner.Id : null;
        npc.Mind.RomanceLeaderNpcId = forced ? npc.Id : null;
        npc.Mind.RomanceForced = forced;
        SocialCueSignals.Stamp(world, npc,
            forced ? "RomanceForcedRequest" : "RomanceRequest", partner.Id);
        SocialCueSignals.Stamp(world, partner,
            forced ? "RomanceForcedIncoming" : "RomanceIncoming", npc.Id);
        return true;
    }

    /// <summary>
    /// §127.13: the Sims slap. The jealous witness drops what she was doing
    /// and heads to arm's length from the cheater; ExecutionSystem lands one
    /// bare-handed blow there. A player-driven or busy body only stays
    /// jealous — the strike is a choice, and §121.5 gives choices to the player.
    /// </summary>
    internal static bool TryInstallJealousStrikePlan(
        WorldState world, NPCState witness, NPCState cheater)
    {
        if (ManualControlMath.IsManual(witness) ||
            witness.IsFighting || witness.IsCarryingPerson ||
            witness.IsLyingDown(world.Tick) || witness.Body.IsProne ||
            witness.CurrentJunction is null ||
            cheater.CurrentJunction is not { } cheaterJunction)
        {
            return false;
        }

        if ((witness.Plan.Status == PlanStatus.Active ||
             witness.Execution.Status == ExecutionStatus.InProgress) &&
            !PlanInterruption.TryAbort(world, witness, InterruptionCause.SceneInitiator,
                $"Jealousy at NPC{cheater.Id.Value}"))
        {
            return false;
        }

        if (TryReserveArmsLengthApproach(world, witness, cheater, cheaterJunction)
            is not { } approach)
        {
            witness.Mind.CurrentGoal = GoalType.None;
            return false;
        }

        // A stale Blocked/Arrived from the walk she just dropped must not
        // read as this route failing before the pathfinder has seen it.
        witness.Movement.JunctionPath.Clear();
        witness.Movement.PathIndex = 0;
        witness.Movement.IsMoving = false;
        witness.Movement.SetStatus(MovementStatus.Idle);
        witness.Plan.Steps.Clear();
        witness.Plan.Goal = GoalType.Jealousy;
        witness.Plan.TargetAgentId = cheater.Id;
        witness.Plan.TargetObjectId = null;
        witness.Plan.TargetJunctionId = approach;
        witness.Plan.TargetTile = cheater.Tile;
        witness.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approach
        });
        witness.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.JealousStrike,
            TargetJunction = approach,
            TimeoutEndTick = world.Tick + Spec127.JealousStrikeApproachTicks
        });
        witness.Plan.CurrentStepIndex = 0;
        witness.Plan.Status = PlanStatus.Active;
        witness.Mind.CurrentGoal = GoalType.Jealousy;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, witness.Id, "JealousStrikeHeading",
                $"Cheater=NPC{cheater.Id.Value} Approach={approach.Value}");
        }

        return true;
    }
}

}
