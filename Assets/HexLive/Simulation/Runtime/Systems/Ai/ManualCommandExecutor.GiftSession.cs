using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{
internal static partial class ManualCommandExecutor
{
    private static bool GiftActor(WorldState world, EntityId id, AdmissionTracker admission,
        IReadOnlyCollection<int> assigned, out NPCState actor)
    {
        actor = null;
        if (assigned != null && !assigned.Contains(id.Value) ||
            !PlayerAuthority.CanMutateInventory(world, id, out actor) || actor.Health <= 0f)
        { Reject(world, id, admission.Order, "NotOwned", admission); return false; }
        return true;
    }

    private static void ApplyBeginGiftSession(WorldState world, BeginGiftSessionCommand command,
        AdmissionTracker admission, IReadOnlyCollection<int> assigned)
    {
        if (!GiftActor(world, command.Npc, admission, assigned, out var giver)) return;
        if (!GiftSessionMath.TryStart(world, giver, command.Recipient, out var receiver, out var approach, out var reason))
        { Reject(world, giver.Id, "BeginGiftSession", reason, admission); return; }
        ClearForNewOrder(world, giver, "Going to give a gift", keepCarriedPerson: true, keepGiftSession: true);
        ClearAttackOrder(world, giver);
        if (approach is { } junction)
            InstallMovePlan(world, giver, junction, world.Junctions.Items[junction], PaceFor(giver, null));
        giver.Plan.TargetAgentId = receiver.Id;
    }

    private static void ApplyEndGiftSession(WorldState world, EndGiftSessionCommand command,
        AdmissionTracker admission, IReadOnlyCollection<int> assigned)
    {
        if (GiftActor(world, command.Npc, admission, assigned, out var giver))
            GiftSessionMath.RequestEnd(world, giver, command.Recipient);
    }
}
}
