using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{
public static class TravelPartyMath
{
    public const float RequiredTrust = 0.60f;

    /// <summary>Only this caller's actual assigned leaders extend its authority.
    /// No persistent player/account assignments are added.</summary>
    public static HashSet<int> EffectiveAssignments(WorldState world, IEnumerable<int> assigned)
    {
        var result = new HashSet<int>(assigned);
        var leaders = new HashSet<int>(result);
        foreach (var npc in world.Entities.Npcs.Values)
            if (npc.Health > 0f && npc.Mind.TravelLeaderNpcId is { } leader && leaders.Contains(leader.Value) &&
                world.Entities.Npcs.TryGetValue(leader, out var person) && person.Health > 0f)
                result.Add(npc.Id.Value);
        return result;
    }

    public static bool ArePartyMates(NPCState a, NPCState b) =>
        a.Mind.TravelLeaderNpcId == b.Id || b.Mind.TravelLeaderNpcId == a.Id ||
        (a.Mind.TravelLeaderNpcId is { } leader && b.Mind.TravelLeaderNpcId == leader);

    internal static void Sweep(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
            if (npc.Mind.TravelLeaderNpcId is { } leader)
            {
                if (npc.Health <= 0f || !npc.Mind.ManualControl || npc.Mind.ExternalControl?.IsActive == true ||
                 !world.Entities.Npcs.TryGetValue(leader, out var person) ||
                 person.Health <= 0f || !person.Mind.ManualControl)
                    ManualCommandExecutor.ReleaseToAi(world, npc, "Travel party ended", expired:false);
                else ManualControlMath.RenewInactivityLease(world,npc);
            }
    }
}
}
