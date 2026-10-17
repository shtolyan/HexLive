using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

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
}

}
