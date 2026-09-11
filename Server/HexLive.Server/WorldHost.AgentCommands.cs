using System;
using System.Linq;
using System.Text.Json.Serialization;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;

namespace HexLive.Server;

public sealed record AgentCommandRequest(int NpcId, long Sequence, string Id, string Fingerprint)
{
    public string RestNeed { get; init; } = "";
    public float RestTarget { get; init; }
}
public sealed record AgentCommandResult(
    [property: JsonPropertyName("highestSequence")] long HighestSequence,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("commandId")] string CommandId,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("reason")] string Reason);

public sealed partial class WorldHost
{
    public AgentCommandResult ReadAgentCommand(int npcId, long sequence, string commandId)
    {
        lock (_gate)
        {
            if (_engine.World.Entities.Npcs.TryGetValue(new EntityId(npcId), out var npc))
                AgentCommandLedger.Observe(_engine.World, npc);
            if (!_engine.World.AgentCommands.TryGetValue(npcId, out var ledger))
                return new(0, sequence, commandId, "unknown", "NotRecorded");
            var receipt = ledger.Receipts.Find(r => r.Sequence == sequence && r.Id == commandId);
            return receipt == null ? new(ledger.HighestSequence, sequence, commandId, "unknown", "NotRecorded") :
                new(ledger.HighestSequence, sequence, receipt.Id, receipt.Outcome, receipt.Reason);
        }
    }

    public AgentCommandResult SubmitTrackedManualCommand(AgentCommandRequest request, ISimulationCommand command)
    {
        lock (_gate)
        {
            var world = _engine.World;
            if (request.Sequence <= 0 || request.Id.Length is 0 or > 96 ||
                !request.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ||
                request.Fingerprint.Length != 64 || command.TargetEntity?.Value != request.NpcId ||
                !world.Entities.Npcs.TryGetValue(new EntityId(request.NpcId), out var npc))
                throw new ArgumentException("InvalidAgentCommand");
            if (request.RestNeed.Length > 0 && (request.RestNeed is not ("Energy" or "Stamina") ||
                !float.IsFinite(request.RestTarget) || request.RestTarget <= 0 || request.RestTarget > 1 ||
                command is not SelfActionCommand rest || rest.Kind !=
                    (request.RestNeed == "Energy" ? SelfActionKind.GroundSleep : SelfActionKind.GroundSit)))
                throw new ArgumentException("InvalidRestCommand");
            if (!world.AgentCommands.TryGetValue(request.NpcId, out var ledger))
                world.AgentCommands.Add(request.NpcId, ledger = new AgentCommandLedger());
            AgentCommandLedger.Observe(world, npc);
            if (request.Sequence <= ledger.HighestSequence)
            {
                var prior = ledger.Receipts.Find(r => r.Sequence == request.Sequence);
                if (prior != null && (prior.Id != request.Id || prior.Fingerprint != request.Fingerprint))
                    return new(ledger.HighestSequence, request.Sequence, request.Id, "unknown", "CommandIdentityConflict");
                return ReadAgentCommand(request.NpcId, request.Sequence, request.Id);
            }
            if (request.Sequence != ledger.HighestSequence + 1 || ledger.ActiveSequence != 0)
                return new(ledger.HighestSequence, request.Sequence, request.Id, "unknown", "CommandSequenceConflict");
            var receipt = new AgentCommandReceipt { Sequence = request.Sequence, Id = request.Id,
                Fingerprint = request.Fingerprint, Outcome = "unknown", Reason = "DispatchInterrupted" };
            ledger.HighestSequence = request.Sequence;
            ledger.Receipts.Add(receipt);
            if (ledger.Receipts.Count > AgentCommandLedger.Capacity) ledger.Receipts.RemoveAt(0);
            // The number is consumed before entering the ordinary command bus. Even an exception
            // cannot make a retry execute this command again.
            // Reaching the requested reserve needs no new sleeping/sitting site.
            // Stop still uses ordinary actor validation and interruption cleanup.
            var restSatisfied = request.RestNeed == "Energy" && npc.Needs.Energy >= request.RestTarget ||
                request.RestNeed == "Stamina" && npc.Needs.Stamina >= request.RestTarget;
            var admission = SubmitManualCommand(restSatisfied ? new StopCommand(npc.Id) : command);
            if (!admission.Accepted)
            { receipt.Outcome = "failed"; receipt.Reason = admission.Reason.Length <= 96 ? admission.Reason : "Rejected"; }
            else if (restSatisfied)
            { receipt.Outcome = "completed"; receipt.Reason = "RestTargetReached"; }
            else if (command is StopCommand or RequestItemCommand or MergeCampsCommand or ManageInventoryCommand ||
                command is SelfActionCommand { Kind: SelfActionKind.CallForHelp })
            { receipt.Outcome = "completed"; receipt.Reason = "Completed"; }
            else if (npc.Plan.Status is PlanStatus.Failed or PlanStatus.Invalid)
            { receipt.Outcome = "failed"; receipt.Reason = "PlanFailed"; }
            else if (npc.Plan.Status == PlanStatus.Active || npc.Execution.Status == ExecutionStatus.InProgress ||
                command is AttackMobCommand or AttackNpcCommand ||
                command is SelfActionCommand { Kind: SelfActionKind.EatFromPack or SelfActionKind.DrinkFromPack })
            { receipt.Outcome = "accepted"; receipt.Reason = "Accepted"; ledger.ActiveSequence = request.Sequence;
                ledger.RestNeed = request.RestNeed; ledger.RestTarget = request.RestTarget; }
            else
            { receipt.Outcome = "completed"; receipt.Reason = "Completed"; }
            return ReadAgentCommand(request.NpcId, request.Sequence, request.Id);
        }
    }
}
