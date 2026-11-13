using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

public readonly struct PlayerOrderView
{
    public PlayerOrderView(int id, string action, string detail, bool active,
        int? targetNpcId = null, int? targetObjectId = null)
    {
        Id = id;
        Action = action;
        Detail = detail;
        Active = active;
        TargetNpcId = targetNpcId;
        TargetObjectId = targetObjectId;
    }

    public int Id { get; }
    public string Action { get; }
    public string Detail { get; }
    public bool Active { get; }
    // §121.13: who or what the order is about, for the hover caption («Обнять: Марта»).
    public int? TargetNpcId { get; }
    public int? TargetObjectId { get; }
}

// Orders belong to the current player control session, like a build or gather session.
// §121.13: this queue is the single source of truth for the player's action list —
// the UI only mirrors it from the snapshot and edits it through commands.
internal sealed class PlayerOrderQueue
{
    public const int Capacity = 16;
    internal sealed class Entry
    {
        public Entry(int id, ISimulationCommand command, string action, string detail,
            int? targetNpcId = null, int? targetObjectId = null)
        {
            Id = id;
            Command = command;
            Action = action;
            Detail = detail;
            TargetNpcId = targetNpcId;
            TargetObjectId = targetObjectId;
        }

        public int Id { get; }
        public ISimulationCommand Command { get; }
        public string Action { get; }
        public string Detail { get; }
        public int? TargetNpcId { get; }
        public int? TargetObjectId { get; }
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
            items.Add(View(active, true));
        foreach (var entry in Pending)
            items.Add(View(entry, false));
    }

    private static PlayerOrderView View(Entry entry, bool active) => new(
        entry.Id, entry.Action, entry.Detail, active, entry.TargetNpcId, entry.TargetObjectId);

    /// <summary>
    /// §121.13: moves one WAITING order so that it stands right before
    /// <paramref name="beforeOrderId"/> (0 = to the end of the waiting tail).
    /// Both are named by their stable queue id, never by a list position, so a
    /// drag made against an older snapshot cannot move a different order. The
    /// running order is not part of the tail: it can be neither moved nor used
    /// as an anchor. Returns the refusal reason, or an empty string.
    /// </summary>
    public string TryMove(int orderId, int beforeOrderId)
    {
        if (Active != null && (Active.Id == orderId || Active.Id == beforeOrderId))
            return "OrderRunning";
        var from = Pending.FindIndex(entry => entry.Id == orderId);
        if (from < 0)
            return "QueueChanged";
        if (beforeOrderId == orderId)
            return string.Empty;
        if (beforeOrderId != 0 && Pending.FindIndex(entry => entry.Id == beforeOrderId) < 0)
            return "QueueChanged";

        var moved = Pending[from];
        Pending.RemoveAt(from);
        var to = beforeOrderId == 0
            ? Pending.Count
            : Pending.FindIndex(entry => entry.Id == beforeOrderId);
        Pending.Insert(to, moved);
        return string.Empty;
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
        if (npc.Mind.ExternalControl?.IsActive == true)
        {
            Reject(world, actor, "Queue", "ControlledByAgent", admission);
            return;
        }
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
            CraftByOutputCommand craft => craft.OutputDefinitionId.Length <= 128
                ? craft.OutputDefinitionId : string.Empty,
            ManageInventoryCommand inventory => inventory.Action.ToString(),
            // §121.13: the kind of a social order picks its own icon (hug ≠ kiss ≠ talk).
            TalkToCommand talk => talk.RequestedTopic?.ToString() ?? string.Empty,
            AidPersonCommand aid => aid.Kind.ToString(),
            RequestItemCommand request => request.DefinitionId.Length <= 128
                ? request.DefinitionId : string.Empty,
            GatherAllOnHexCommand gather => gather.Interaction.ToString(),
            _ => string.Empty
        };
        var (targetNpc, targetObject) = OrderTargets(command.Order);
        queue.Pending.Add(new PlayerOrderQueue.Entry(++queue.NextId, command.Order, OrderName(command.Order), detail,
            targetNpc, targetObject));

        if (!npc.Mind.ManualControl)
            ApplySetManual(world, new SetManualControlCommand(actor, true), admission);
        ManualControlMath.RenewInactivityLease(world, npc);
    }

    // §121.13: the person or object an order is about, as the queue shows it.
    private static (int? Npc, int? Object) OrderTargets(ISimulationCommand order) => order switch
    {
        InteractCommand c => (null, c.Target.Value),
        GatherAllOnHexCommand c => (null, c.Target.Value),
        PutPersonInBedCommand c => (null, c.Bed.Value),
        KnockDoorCommand c => (null, c.Door.Value),
        BreakDoorCommand c => (null, c.Door.Value),
        CraftItemCommand { Station: { } station } => (null, station.Value),
        AttackNpcCommand c => (c.Target.Value, null),
        CarryPersonCommand c => (c.Target.Value, null),
        TalkToCommand c => (c.Target.Value, null),
        RequestItemCommand c => (c.Target.Value, null),
        RomancePersonCommand c => (c.Target.Value, null),
        AidPersonCommand c => (c.Target.Value, null),
        TreatLimbsCommand c => (c.Target.Value, null),
        MedicalAidCommand c => (c.Target.Value, null),
        PreyPersonCommand c => (c.Target.Value, null),
        ButcherPersonCommand c => (c.Target.Value, null),
        AbusePersonCommand c => (c.Target.Value, null),
        _ => (null, null)
    };

    /// <summary>§121.13: «only walking» — the queue holds nothing but go orders.</summary>
    internal static bool HoldsOnlyMovement(PlayerOrderQueue queue)
    {
        if (queue.Active != null && queue.Active.Command is not MoveToCommand)
            return false;
        foreach (var entry in queue.Pending)
            if (entry.Command is not MoveToCommand)
                return false;
        return true;
    }

    // §121.13 r2: a plain ground click. With only walking in the queue it
    // REPLACES all of it at once (Diablo/RTS: the new point wins, nothing
    // accumulates). Any other action in the queue is never wiped by a plain
    // click: the client opens «Идти сюда» instead, and a raced command that
    // finds such an action here is refused with QueueHasAction.
    private static void ApplyReplaceMovement(
        WorldState world, ReplaceMovementOrderCommand command, AdmissionTracker admission)
    {
        var actor = command.Move.Npc;
        if (admission.AssignedNpcIds is { } assignments && !assignments.Contains(actor.Value))
        {
            Reject(world, actor, "Go", "NotAssigned", admission);
            return;
        }
        if (!TryTakeOrder(world, actor, "Go", requireManual: false, admission, out var npc))
            return;
        if (npc.Mind.ExternalControl?.IsActive == true)
        {
            Reject(world, actor, "Go", "ControlledByAgent", admission);
            return;
        }
        if (Incapacitated(world, npc))
        {
            Reject(world, actor, "Go", "Incapacitated", admission);
            return;
        }

        var queue = npc.Mind.PlayerOrders;
        if (!HoldsOnlyMovement(queue))
        {
            Reject(world, actor, "Go", "QueueHasAction", admission);
            return;
        }

        if (!npc.Mind.ManualControl)
            ApplySetManual(world, new SetManualControlCommand(actor, true), admission);
        if (!admission.Accepted)
            return;

        // ApplyMoveTo aborts the running walk through the ordinary plan transaction.
        ApplyMoveTo(world, command.Move, admission);
        if (!admission.Accepted)
            return;

        queue.Pending.Clear();
        queue.Active = new PlayerOrderQueue.Entry(++queue.NextId, command.Move, OrderName(command.Move), string.Empty);
        ManualControlMath.RenewInactivityLease(world, npc);
    }

    // §121.13: drag-to-reorder in the action list. Same gate as cancelling:
    // an assigned, owned, manually controlled character. Only the waiting
    // tail moves; the running order keeps running.
    private static void ApplyMovePlayerOrder(
        WorldState world, MovePlayerOrderCommand command, AdmissionTracker admission)
    {
        if (admission.AssignedNpcIds is { } assignments && !assignments.Contains(command.Npc.Value))
        {
            Reject(world, command.Npc, "MoveOrder", "NotAssigned", admission);
            return;
        }
        if (!TryTakeOrder(world, command.Npc, "MoveOrder", requireManual: true, admission, out var npc))
            return;

        var refusal = npc.Mind.PlayerOrders.TryMove(command.OrderId, command.BeforeOrderId);
        if (refusal.Length > 0)
        {
            Reject(world, command.Npc, "MoveOrder", refusal, admission);
            return;
        }

        ManualControlMath.RenewInactivityLease(world, npc);
    }

    private static void ApplyCancelPlayerOrder(
        WorldState world, CancelPlayerOrderCommand command, AdmissionTracker admission)
    {
        if (admission.AssignedNpcIds is { } assignments && !assignments.Contains(command.Npc.Value))
        {
            Reject(world, command.Npc, "CancelOrder", "NotAssigned", admission);
            return;
        }
        if (!TryTakeOrder(world, command.Npc, "CancelOrder", requireManual: true, admission, out var npc))
            return;

        var queue = npc.Mind.PlayerOrders;
        if (queue.Active?.Id == command.OrderId)
        {
            queue.Active = null;
            ClearForNewOrder(world, npc, "Игрок отменил действие", keepCarriedPerson: true,
                keepRestPose: KeepsRestPoseAcrossModeSwitch(world, npc));
            npc.Mind.CurrentGoal = GoalType.None;
            ClearAttackOrder(world, npc);
        }
        else if (queue.Pending.RemoveAll(order => order.Id == command.OrderId) == 0)
        {
            // The selected action may have completed between the snapshot and the click.
            return;
        }

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

        if (queue.Active != null)
        {
            queue.Active = null;
            ManualControlMath.RenewInactivityLease(world, npc);
        }
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
