using HexLive.Simulation.Agents;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{
public static partial class CampDiplomacyMath
{
    /// <summary>#655: voluntary individual recruitment after care/friendship.</summary>
    internal static bool TryRecruitOutsider(WorldState world, NPCState inviter, NPCState target, out string reason)
    {
        reason = "RelationshipTooLow";
        if (!FactionRelations.IsGirlCamp(inviter.Faction) || target.Faction != Faction.Outsiders)
        { reason = "NotNeighbourCamp"; return false; }
        if (target.Health <= 0f || target.IsUnconscious(world.Tick) || target.IsFighting ||
            target.Body.IsProne || target.IsBeingCarried || ManualControlMath.IsManual(target) ||
            target.Mind.ExternalControl?.IsActive == true)
        { reason = "TargetUnavailable"; return false; }
        if (HexSpatialMath.Distance(inviter.Position, target.Position) > InteractionReach.Talk)
        { reason = "TooFarToTalk"; return false; }
        if (target.Social.GetOrCreate(inviter.Id).Affinity <= MergeAffinityThreshold ||
            inviter.Social.GetOrCreate(target.Id).Affinity <= MergeAffinityThreshold ||
            target.Social.GetOrCreate(inviter.Id).Trust <= MergeAffinityThreshold) return false;
        if (!world.FactionHomes.ContainsKey(inviter.Faction))
        { reason = "CampMissing"; return false; }
        if (PopulationArrivalMath.CampResidents(world, inviter.Faction) >=
            PopulationArrivalMath.CampCapacity(world, inviter.Faction))
        { reason = "PopulationLimit"; return false; }

        PlanInterruption.TryAbort(world, target, InterruptionCause.PlayerCommand, "Accepted camp invitation");
        target.Mind.CurrentGoal = AI.GoalType.None;
        target.Faction = inviter.Faction;
        if (world.CreationConfig?.Owns(target) == true) world.PlayerControlledNpcs.Add(target.Id.Value);
        foreach (var observer in world.Entities.Npcs.Values)
            if (observer.Memory.KnownAgents.TryGetValue(target.Id, out var memory)) memory.Faction = target.Faction;
        world.DoorStateVersion++;
        SocialCueSignals.Stamp(world, inviter, "TalkSuccess", target.Id);
        SocialCueSignals.Stamp(world, target, "TalkSuccess", inviter.Id);
        Trace.Emit(world, inviter.Id, "OutsiderRecruited", $"Target=NPC{target.Id.Value} Camp={target.Faction}");
        reason = string.Empty;
        return true;
    }
}
}
