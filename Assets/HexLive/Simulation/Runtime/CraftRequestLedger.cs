using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{
// §138.6.1: immutable admission receipts, not a second craft execution system.
public sealed class CraftRequestReceipt
{
    public string Owner { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string OutputId { get; set; } = "";
    public bool Accepted { get; set; }
    public string Reason { get; set; } = "";

    internal ManualCommandAdmission Admission(EntityId npc) => new(
        Accepted ? ManualCommandAdmissionStatus.Accepted : ManualCommandAdmissionStatus.Rejected,
        npc, "Craft", Reason);
}

public sealed class CraftRequestLedger
{
    public const int Capacity = 1024;
    public const int MaxRequestIdLength = 96;
    public const int MaxOwnerLength = 256;
    public const int MaxOutputLength = 256;
    public const int MaxReasonLength = 96;
    public List<CraftRequestReceipt> Receipts { get; } = new();

    public static bool ValidRequestId(string id) => id != null && id.Length > 0 &&
        id.Length <= MaxRequestIdLength && id.All(c =>
            c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' ||
            c >= '0' && c <= '9' || c == '-' || c == '_' || c == '.');

    internal static ManualCommandAdmission Apply(WorldState world, CraftByOutputCommand command,
        string owner, Func<ManualCommandAdmission> apply)
    {
        ManualCommandAdmission Reject(string reason) => new(
            ManualCommandAdmissionStatus.Rejected, command.Npc, "Craft", reason);
        owner ??= "";
        if (!ValidRequestId(command.RequestId) || owner.Length > MaxOwnerLength ||
            command.OutputDefinitionId.Length > MaxOutputLength)
            return Reject("InvalidCraftRequest");

        world.CraftRequests.TryGetValue(command.Npc.Value, out var ledger);
        var prior = ledger?.Receipts.Find(r => r.Owner == owner && r.RequestId == command.RequestId);
        if (prior != null)
            return prior.OutputId == command.OutputDefinitionId ? prior.Admission(command.Npc)
                : Reject("CraftRequestConflict");
        if (!world.Entities.Npcs.ContainsKey(command.Npc)) return Reject("NpcMissing");
        if (ledger == null)
            world.CraftRequests.Add(command.Npc.Value, ledger = new CraftRequestLedger());
        // No eviction: arbitrary keys have no high-water mark. Forgetting a key
        // would let a late retry pay again; fail closed when the bound is reached.
        if (ledger.Receipts.Count >= Capacity) return Reject("CraftRequestJournalFull");
        var receipt = new CraftRequestReceipt { Owner = owner, RequestId = command.RequestId,
            OutputId = command.OutputDefinitionId, Reason = "CraftRequestInterrupted" };
        ledger.Receipts.Add(receipt); // reserve before applying, including exceptional exits
        var admission = apply();
        receipt.Accepted = admission.Accepted;
        receipt.Reason = admission.Reason.Length <= MaxReasonLength ? admission.Reason : "Rejected";
        return receipt.Admission(command.Npc);
    }
}
}
