using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

public readonly struct PlayerOrderView
{
    public PlayerOrderView(int id, string action, string detail, bool active)
    {
        Id = id;
        Action = action;
        Detail = detail;
        Active = active;
    }

    public int Id { get; }
    public string Action { get; }
    public string Detail { get; }
    public bool Active { get; }
}

// Orders belong to the current player control session, like a build or gather session.
internal sealed class PlayerOrderQueue
{
    public const int Capacity = 16;
    internal sealed class Entry
    {
        public Entry(int id, ISimulationCommand command, string action, string detail)
        {
            Id = id;
            Command = command;
            Action = action;
            Detail = detail;
        }

        public int Id { get; }
        public ISimulationCommand Command { get; }
        public string Action { get; }
        public string Detail { get; }
    }

    public readonly List<Entry> Pending = new();
    public Entry Active;
    public int NextId;

    public void Clear()
    {
        Pending.Clear();
        Active = null;
    }

    public void CopyTo(List<PlayerOrderView> items)
    {
        if (Active is { } active)
            items.Add(new PlayerOrderView(active.Id, active.Action, active.Detail, true));
        foreach (var entry in Pending)
            items.Add(new PlayerOrderView(entry.Id, entry.Action, entry.Detail, false));
    }
}

internal static partial class ManualCommandExecutor
{
    private static void ApplyQueuePlayerOrder(
        WorldState world, QueuePlayerOrderCommand command, AdmissionTracker admission)
    {
        if (!QueuePlayerOrderCommand.Supports(command.Order) || command.TargetEntity is not { } actor)
        {
            admission.Reject("InvalidOrder");
            return;
        }

        if (admission.AssignedNpcIds is { } assignments && !assignments.Contains(actor.Value))
        {
            Reject(world, actor, "Queue", "NotAssigned", admission);
            return;
        }

        if (!TryTakeOrder(world, actor, "Queue", requireManual: false, admission, out var npc))
            return;
        if (Incapacitated(world, npc))
        {
            Reject(world, actor, "Queue", "Incapacitated", admission);
            return;
        }

        var queue = npc.Mind.PlayerOrders;
        if (queue.Pending.Count + (queue.Active == null ? 0 : 1) >= PlayerOrderQueue.Capacity)
        {
            Reject(world, actor, "Queue", "OrderQueueFull", admission);
            return;
        }

        var detail = command.Order switch
        {
            SelfActionCommand self => self.Kind.ToString(),
            InteractCommand interaction => interaction.Interaction + "|" +
                (interaction.InteractionId.Length <= 128 ? interaction.InteractionId : string.Empty),
            _ => string.Empty
        };
        queue.Pending.Add(new PlayerOrderQueue.Entry(++queue.NextId, command.Order, OrderName(command.Order), detail));

        if (!npc.Mind.ManualControl)
            ApplySetManual(world, new SetManualControlCommand(actor, true), admission);
        ManualControlMath.RenewInactivityLease(world, npc);
    }

    internal static void AdvancePlayerOrders(WorldState world, NPCState npc)
    {
        var queue = npc.Mind.PlayerOrders;
        if (queue.Active == null && queue.Pending.Count == 0)
            return;
        if (!PlayerAuthority.IsPlayerOwned(world, npc))
        {
            queue.Clear();
            return;
        }
        if (npc.Mind.CurrentGoal != GoalType.None || ManualControlMath.HasActiveOrder(npc) ||
            ManualGatherTargets.IsActive(npc.Mind) || ManualBuildSession.IsActive(npc.Mind) ||
            npc.Mind.ManualAttackNpcId.HasValue || npc.Mind.ManualAttackMobId.HasValue)
            return;

        queue.Active = null;
        if (queue.Pending.Count == 0 || Incapacitated(world, npc))
            return;

        var entry = queue.Pending[0];
        queue.Pending.RemoveAt(0);
        var result = ApplyCore(world, entry.Command, new[] { npc.Id.Value });
        if (result.Accepted)
            queue.Active = entry;
    }
}
}
