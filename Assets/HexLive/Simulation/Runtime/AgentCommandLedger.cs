using System.Collections.Generic;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Core;

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
    public List<AgentCommandReceipt> Receipts { get; } = new();

    public static void Finish(WorldState world, NPCState npc, string outcome, string reason)
    {
        if (!world.AgentCommands.TryGetValue(npc.Id.Value, out var ledger) || ledger.ActiveSequence == 0) return;
        var receipt = ledger.Receipts.Find(r => r.Sequence == ledger.ActiveSequence);
        if (receipt != null && receipt.Outcome == "accepted")
        { receipt.Outcome = outcome; receipt.Reason = reason; }
        ledger.ActiveSequence = 0;
    }

    public static void Observe(WorldState world, NPCState npc)
    {
        if (!world.AgentCommands.TryGetValue(npc.Id.Value, out var ledger) || ledger.ActiveSequence == 0) return;
        if (npc.Health <= 0 || !npc.Mind.ManualControl)
            Finish(world, npc, "failed", "ControlLost");
        else if (npc.Execution.Status != ExecutionStatus.InProgress)
        {
            if (npc.Plan.Status == PlanStatus.Completed) Finish(world, npc, "completed", "Completed");
            else if (npc.Plan.Status == PlanStatus.Failed || npc.Plan.Status == PlanStatus.Invalid)
                Finish(world, npc, "failed", "PlanFailed");
        }
    }
}
}
