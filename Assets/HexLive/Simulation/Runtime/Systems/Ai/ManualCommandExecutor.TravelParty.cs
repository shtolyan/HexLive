using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{
internal static partial class ManualCommandExecutor
{
    private static void ApplyRequestTravelParty(WorldState world, RequestTravelPartyCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(world, command.Npc, "RequestTravelParty", true, admission, out var leader)) return;
        if (Incapacitated(world, leader) || leader.Mind.TravelLeaderNpcId != null ||
            leader.IsFighting || !world.Entities.Npcs.TryGetValue(command.Target,out var target) ||
            target.Id == leader.Id || target.Health <= 0f || target.IsUnconscious(world.Tick) || target.IsFighting ||
            target.IsBeingCarried || target.Mind.ExternalControl?.IsActive == true)
        { Reject(world,leader.Id,"RequestTravelParty","TargetUnavailable",admission); return; }
        if (target.Mind.TravelLeaderNpcId == leader.Id) return;
        if (target.Mind.ManualControl || target.Mind.TravelLeaderNpcId != null ||
            (admission.AssignedNpcIds != null && world.PlayerControlledNpcs.Contains(target.Id.Value) &&
             !admission.AssignedNpcIds.Contains(target.Id.Value)))
        { Reject(world,leader.Id,"RequestTravelParty","ControlledByOther",admission); return; }
        if (target.Social.GetOrCreate(leader.Id).Trust <= TravelPartyMath.RequiredTrust)
        { Reject(world,leader.Id,"RequestTravelParty","TravelTrustTooLow",admission); return; }
        if (leader.Fragment != target.Fragment || !TravelPartyMath.IsWithinInviteRange(leader.Position,target.Position))
        { Reject(world,leader.Id,"RequestTravelParty","TravelTargetTooFar",admission); return; }
        ClearForNewOrder(world,target,"Accepted travel invitation");
        target.Mind.CurrentGoal = GoalType.None;
        target.Mind.TravelLeaderNpcId = leader.Id;
        target.Mind.ManualControl = true;
        ManualControlMath.RenewInactivityLease(world,target);
        SocialCueSignals.Stamp(world,target,"DirectiveYes",leader.Id);
        Trace.Emit(world,leader.Id,"TravelPartyJoined",$"Target=NPC{target.Id.Value}");
    }

    private static void ApplyReleaseTravelParty(WorldState world, ReleaseTravelPartyCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(world,command.Npc,"ReleaseTravelParty",false,admission,out var leader)) return;
        if (!world.Entities.Npcs.TryGetValue(command.Target,out var target) || target.Mind.TravelLeaderNpcId != leader.Id)
        { Reject(world,leader.Id,"ReleaseTravelParty","TargetUnavailable",admission); return; }
        ReleaseToAi(world,target,"Released travel companion",expired:false);
        Trace.Emit(world,leader.Id,"TravelPartyReleased",$"Target=NPC{target.Id.Value}");
    }
}
}
