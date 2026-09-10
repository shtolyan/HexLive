using System.Collections.Generic;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Core;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Runtime
{
// §144: saved command receipts share the same snapshot as their effects.
public sealed class AgentCommandReceipt
{
    public long Sequence { get; set; }
    public string Id { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Outcome { get; set; } = "unknown";
    public string Reason { get; set; } = "";
}

public sealed class AgentCommandLedger
{
    public const int Capacity = 32;
    public long HighestSequence { get; set; }
    public long ActiveSequence { get; set; }
    public string RestNeed { get; set; } = "";
    public float RestTarget { get; set; }
    public List<AgentCommandReceipt> Receipts { get; } = new();

    public static void Finish(WorldState world, NPCState npc, string outcome, string reason)
    {
        if (!world.AgentCommands.TryGetValue(npc.Id.Value, out var ledger) || ledger.ActiveSequence == 0) return;
        var receipt = ledger.Receipts.Find(r => r.Sequence == ledger.ActiveSequence);
        if (receipt != null && receipt.Outcome == "accepted")
        { receipt.Outcome = outcome; receipt.Reason = reason; }
        ledger.ActiveSequence = 0;
        ledger.RestNeed = "";
        ledger.RestTarget = 0;
    }

    public static void Observe(WorldState world, NPCState npc)
    {
        if (!world.AgentCommands.TryGetValue(npc.Id.Value, out var ledger) || ledger.ActiveSequence == 0) return;
        if (npc.Health <= 0 || !npc.Mind.ManualControl)
            Finish(world, npc, "failed", "ControlLost");
        else if (ledger.RestNeed.Length > 0 && npc.Plan.Status == PlanStatus.Active &&
            npc.Execution.Status == ExecutionStatus.InProgress &&
            ((ledger.RestNeed == "Energy" && npc.Execution.CurrentInteraction == InteractionType.Sleep && npc.Needs.Energy >= ledger.RestTarget) ||
             (ledger.RestNeed == "Stamina" && npc.Execution.CurrentInteraction is InteractionType.Sit or InteractionType.Rest && npc.Needs.Stamina >= ledger.RestTarget)))
        {
            // Detach from the ordinary Stop's interruption hook. Completion is
            // recorded only after that native command actually wakes the actor.
            var receipt = ledger.Receipts.Find(r => r.Sequence == ledger.ActiveSequence)!;
            ledger.ActiveSequence = 0;
            ledger.RestNeed = "";
            ledger.RestTarget = 0;
            receipt.Outcome = "failed";
            receipt.Reason = "RestStopInterrupted";
            var stopped = ManualCommandExecutor.Apply(world, new StopCommand(npc.Id));
            if (stopped.Accepted)
            {
                receipt.Outcome = "completed";
                receipt.Reason = "RestTargetReached";
            }
            else receipt.Reason = "RestStopRejected";
        }
        else if (npc.Execution.Status != ExecutionStatus.InProgress)
        {
            if (npc.Plan.Status == PlanStatus.Completed) Finish(world, npc, "completed", "Completed");
            else if (npc.Plan.Status == PlanStatus.Failed || npc.Plan.Status == PlanStatus.Invalid)
                Finish(world, npc, "failed", "PlanFailed");
        }
    }
}
}
