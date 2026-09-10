using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HexLive.AgentHost;

// §163.3c: execution state, not a transcript and not evidence that the goal is achieved.
public sealed record AgentExecutionStep(string Id, string Tool, JsonElement Arguments);

public sealed record AgentExecutionPlan
{
    public int SchemaVersion { get; init; } = 1;
    public required string Id { get; init; }
    public required string WorldKey { get; init; }
    public required int NpcId { get; init; }
    public required long ObjectiveRevision { get; init; }
    public required AgentExecutionStep[] Steps { get; init; }
    public long Revision { get; init; }
    public int Cursor { get; init; }
    public string Status { get; init; } = "active";
    public string Reason { get; init; } = "";
    public AgentExecutionCommand? Command { get; init; }
}

public sealed record AgentExecutionCommand(string Id, string StepId, string Status);

// Only an authoritative command-specific receipt can resolve an uncertain send.
// An idle NPC, an accepted request or a model's prose cannot establish completion.
public sealed record AgentExecutionReceipt(string CommandId, string Outcome);

public static class AgentExecutionPlanPolicy
{
    public const int MaxSteps = 64;

    public static AgentExecutionPlan Create(AgentObjective objective,
        string worldKey, int npcId, IEnumerable<AgentExecutionStep> steps)
    {
        if (objective.Status != "active" || objective.Revision <= 0 || objective.WorldKey != worldKey ||
            objective.AvatarNpcId != npcId || npcId <= 0 || string.IsNullOrWhiteSpace(worldKey))
            throw new InvalidDataException("InvalidExecutionPlanBinding");
        var copy = steps.Take(MaxSteps + 1).ToArray();
        if (copy.Length is 0 or > MaxSteps || copy.Any(s => s == null || !Identifier(s.Id) ||
                !AgentProviders.IsAllowedTool(s.Tool) || s.Tool == "query_known_objects" ||
                s.Arguments.ValueKind != JsonValueKind.Object || s.Arguments.GetRawText().Length > 8192) ||
            copy.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new InvalidDataException("InvalidExecutionPlanSteps");
        return new AgentExecutionPlan
        {
            Id = Guid.NewGuid().ToString("N"), WorldKey = worldKey, NpcId = npcId, ObjectiveRevision = objective.Revision,
            Steps = copy.Select(s => s with { Arguments = s.Arguments.Clone() }).ToArray(),
        };
    }

    public static AgentExecutionPlan Prepare(AgentExecutionPlan plan, long expectedRevision,
        string worldKey, int npcId, long objectiveRevision)
    {
        CheckRevision(plan, expectedRevision);
        if (plan.Status != "active" || plan.Command != null || plan.Cursor >= plan.Steps.Length ||
            plan.WorldKey != worldKey || plan.NpcId != npcId || plan.ObjectiveRevision != objectiveRevision)
            throw new InvalidOperationException("ExecutionPlanNotDispatchable");
        var step = plan.Steps[plan.Cursor];
        // Plan IDs are allocated by the host, never reused for a replacement plan.
        var commandId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new[] { plan.WorldKey, plan.NpcId.ToString(System.Globalization.CultureInfo.InvariantCulture), plan.Id, step.Id })))).ToLowerInvariant();
        return plan with { Revision = checked(plan.Revision + 1),
            Command = new(commandId, step.Id, "sending") };
    }

    public static AgentExecutionPlan Observe(AgentExecutionPlan plan, long expectedRevision,
        AgentExecutionReceipt receipt)
    {
        CheckRevision(plan, expectedRevision);
        if (plan.Command == null || plan.Command.Id != receipt.CommandId ||
            plan.Status is "completed" or "canceled" ||
            receipt.Outcome is not ("accepted" or "completed" or "failed" or "unknown"))
            throw new InvalidOperationException("InvalidExecutionReceipt");
        if (plan.Command.Status == receipt.Outcome) return plan;
        if (plan.Command.Status == "failed") throw new InvalidOperationException("ExecutionCommandAlreadyFailed");
        // A replayed ACK after completion never advances the next step: its command ID differs.
        if (receipt.Outcome == "completed")
        {
            var next = checked(plan.Cursor + 1);
            return plan with { Revision = checked(plan.Revision + 1), Cursor = next, Command = null,
                Status = next == plan.Steps.Length ? "completed" : plan.Status,
                Reason = next == plan.Steps.Length ? "CommandsCompleted" : plan.Reason };
        }
        if (receipt.Outcome == "accepted" && plan.Command.Status == "unknown")
            return plan with { Revision = checked(plan.Revision + 1),
                Command = plan.Command with { Status = "accepted" } };
        return plan with { Revision = checked(plan.Revision + 1),
            Command = plan.Command with { Status = receipt.Outcome },
            Status = receipt.Outcome is "failed" or "unknown" ? "paused" : plan.Status,
            Reason = receipt.Outcome switch { "failed" => "CommandFailed", "unknown" => "CommandOutcomeUnknown", _ => plan.Reason } };
    }

    public static AgentExecutionPlan Pause(AgentExecutionPlan plan, long expectedRevision, string reason)
    {
        CheckRevision(plan, expectedRevision);
        if (plan.Status != "active" || !Identifier(reason)) throw new InvalidOperationException("InvalidExecutionPause");
        return plan with { Revision = checked(plan.Revision + 1), Status = "paused", Reason = reason };
    }

    public static AgentExecutionPlan Resume(AgentExecutionPlan plan, long expectedRevision,
        string worldKey, int npcId, long objectiveRevision)
    {
        CheckRevision(plan, expectedRevision);
        if (plan.Status != "paused" || plan.Command != null || plan.WorldKey != worldKey ||
            plan.NpcId != npcId || plan.ObjectiveRevision != objectiveRevision)
            throw new InvalidOperationException("ExecutionPlanCannotResume");
        return plan with { Revision = checked(plan.Revision + 1), Status = "active", Reason = "" };
    }

    public static AgentExecutionPlan Cancel(AgentExecutionPlan plan, long expectedRevision)
    {
        CheckRevision(plan, expectedRevision);
        if (plan.Status is not ("active" or "paused")) throw new InvalidOperationException("InvalidExecutionCancel");
        // Retain the command for diagnosis/reconciliation; cancellation never claims undo.
        return plan with { Revision = checked(plan.Revision + 1), Status = "canceled", Reason = "ExplicitCancel" };
    }

    public static AgentExecutionPlan Recover(AgentExecutionPlan plan)
    {
        ValidateState(plan);
        if (plan.Status is "completed" or "canceled") return plan;
        if (plan.Command is { Status: "sending" or "accepted" })
            return Observe(plan, plan.Revision, new(plan.Command.Id, "unknown"));
        if (plan.Status == "active") return Pause(plan, plan.Revision, "Reconnected");
        return plan;
    }

    private static bool Identifier(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 96 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    private static void CheckRevision(AgentExecutionPlan plan, long expected)
    {
        ValidateState(plan);
        if (plan.Revision != expected) throw new InvalidOperationException("ExecutionPlanRevisionConflict");
    }

    private static void ValidateState(AgentExecutionPlan plan)
    {
        if (plan.SchemaVersion != 1 || !Identifier(plan.Id) || string.IsNullOrWhiteSpace(plan.WorldKey) ||
            plan.NpcId <= 0 || plan.ObjectiveRevision <= 0 || plan.Revision < 0 || plan.Steps == null ||
            plan.Steps.Length is 0 or > MaxSteps || plan.Cursor < 0 || plan.Cursor > plan.Steps.Length ||
            plan.Status is not ("active" or "paused" or "completed" or "canceled") ||
            plan.Steps.Any(s => s == null || !Identifier(s.Id) || !AgentProviders.IsAllowedTool(s.Tool) ||
                s.Tool == "query_known_objects" || s.Arguments.ValueKind != JsonValueKind.Object ||
                s.Arguments.GetRawText().Length > 8192) ||
            plan.Steps.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != plan.Steps.Length ||
            (plan.Status == "completed") != (plan.Cursor == plan.Steps.Length) ||
            plan.Command is { } command && (plan.Cursor == plan.Steps.Length ||
                command.StepId != plan.Steps[plan.Cursor].Id || !Identifier(command.Id) ||
                command.Status is not ("sending" or "accepted" or "failed" or "unknown")))
            throw new InvalidDataException("InvalidExecutionPlanState");
    }
}
