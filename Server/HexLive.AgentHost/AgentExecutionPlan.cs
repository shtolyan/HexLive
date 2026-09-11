using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HexLive.AgentHost;

// §163.3c: execution state, not a transcript and not evidence that the goal is achieved.
public sealed record AgentExecutionStep(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("arguments")] JsonElement Arguments)
{
    [JsonPropertyName("condition"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentExecutionCondition? Condition { get; init; }
    [JsonPropertyName("repeat")]
    public int Repeat { get; init; } = 1;
}

public sealed record AgentExecutionCondition(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("operator")] string Operator,
    [property: JsonPropertyName("value")] double Value,
    [property: JsonPropertyName("onFalseStepId")] string OnFalseStepId);

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
    public int Iteration { get; init; }
    public string Status { get; init; } = "active";
    public string Reason { get; init; } = "";
    public AgentExecutionCommand? Command { get; init; }
}

public sealed record AgentExecutionCommand(string Id, string StepId, string Status)
{
    public long Sequence { get; init; }
    public string Reason { get; init; } = "";
}

// Only an authoritative command-specific receipt can resolve an uncertain send.
// An idle NPC, an accepted request or a model's prose cannot establish completion.
public sealed record AgentExecutionReceipt(string CommandId, string Outcome, string Reason = "");

public sealed record AgentExecutionProgress(string WorldKey, int NpcId, string PlanId,
    string CommandId, long Sequence, AgentExecutionStep Step, DateTimeOffset RecordedUtc);

internal static class AgentExecutionProgressView
{
    public static int BuildTarget(AgentExecutionProgress row) => row.Step.Tool == "interact" &&
        row.Step.Arguments.ValueKind == JsonValueKind.Object &&
        row.Step.Arguments.TryGetProperty("interaction", out var verb) && verb.ValueKind == JsonValueKind.String &&
        verb.GetString() == "Build" && row.Step.Arguments.TryGetProperty("objectId", out var id) &&
        id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var value) && value > 0 ? value : 0;

    public static AgentExecutionProgress[] BuildTargets(IEnumerable<AgentExecutionProgress> rows) => rows
        .Select((row, index) => (row, index, target: BuildTarget(row))).Where(x => x.target > 0)
        .GroupBy(x => (x.row.WorldKey, x.row.NpcId, x.target)).Select(g => g.Last())
        .OrderBy(x => x.index).TakeLast(8).Select(x => x.row).ToArray();

    public static void Trim(List<AgentExecutionProgress> rows)
    {
        if (rows.Count <= 64) return;
        var targets = BuildTargets(rows).Select(r => r.CommandId).ToHashSet(StringComparer.Ordinal);
        while (rows.Count > 64)
        {
            var discard = rows.FindIndex(r => !targets.Contains(r.CommandId));
            rows.RemoveAt(Math.Max(0, discard));
        }
    }
}

public sealed class AgentExecutionPlanUpdate
{
    [JsonPropertyName("operation")] public string Operation { get; set; } = "";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("steps")] public AgentExecutionStep[] Steps { get; set; } = [];
}

public static class AgentExecutionPlanPolicy
{
    public const int MaxSteps = 64;
    public const int MaxDispatches = 256;

    // Older/provider-specific decisions may express a one-command continuation
    // as action. Give it the same durable receipt semantics as an explicit queue.
    public static bool CompletesWithPendingWork(CompanionDecision decision, AgentExecutionPlan? current) =>
        decision.ObjectiveUpdate?.Operation == "complete" &&
        (current is { Status: "active" or "paused" } || decision.ExecutionPlanUpdate != null || decision.Action != null);

    public static void NormalizeContinuation(CompanionDecision decision, MashaArchive state, string worldKey, int npcId)
    {
        if (decision.Action is not { } action || action.Tool == "query_known_objects" ||
            decision.ExecutionPlanUpdate != null || decision.MemoryRequests.Count > 0 ||
            decision.ObjectiveUpdate?.Operation is "pause" or "clear" or "complete") return;
        var newGoal = decision.ObjectiveUpdate?.Operation == "set";
        if (!newGoal && !(state.Objective is { Status: "active" } goal &&
            goal.WorldKey == worldKey && goal.AvatarNpcId == npcId)) return;
        if (!newGoal && state.ExecutionPlan is { } plan &&
            plan.Status is not ("completed" or "canceled") && plan.Command?.Status != "failed")
            throw new AgentActionValidationException("ActiveExecutionPlanRequiresExplicitUpdate");
        decision.ExecutionPlanUpdate = new() { Operation = "replace", Reason = "GoalContinuation",
            Steps = [new("continue", action.Tool, action.Arguments.Clone())] };
        decision.Action = null;
    }

    public static void ValidateUpdate(AgentExecutionPlanUpdate update)
    {
        if (update.Operation is not ("replace" or "pause" or "resume" or "cancel") ||
            !Identifier(update.Reason) || update.Steps == null ||
            (update.Operation == "replace" ? update.Steps.Length is 0 or > MaxSteps : update.Steps.Length != 0) ||
            update.Steps.Any(s => s == null || !Identifier(s.Id) || !AgentProviders.IsAllowedTool(s.Tool) ||
                s.Tool == "query_known_objects" || s.Arguments.ValueKind != JsonValueKind.Object || s.Arguments.GetRawText().Length > 8192) ||
            update.Steps.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != update.Steps.Length)
            throw new InvalidDataException("InvalidExecutionPlanUpdate");
        ValidateConditions(update.Steps);
        ValidateRepeats(update.Steps);
    }

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
        ValidateConditions(copy);
        ValidateRepeats(copy);
        return new AgentExecutionPlan
        {
            SchemaVersion = copy.Any(s => s.Repeat > 1) ? 3 : copy.Any(s => s.Condition != null) ? 2 : 1,
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
        var identity = new List<string> { plan.WorldKey, plan.NpcId.ToString(System.Globalization.CultureInfo.InvariantCulture), plan.Id, step.Id };
        if (step.Repeat > 1) identity.Add(plan.Iteration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var commandId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(identity)))).ToLowerInvariant();
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
            if (plan.Iteration + 1 < plan.Steps[plan.Cursor].Repeat)
                return plan with { Revision = checked(plan.Revision + 1), Iteration = plan.Iteration + 1, Command = null };
            var next = checked(plan.Cursor + 1);
            return plan with { Revision = checked(plan.Revision + 1), Cursor = next, Iteration = 0, Command = null,
                Status = next == plan.Steps.Length ? "completed" : plan.Status,
                Reason = next == plan.Steps.Length ? "CommandsCompleted" : plan.Reason };
        }
        if (receipt.Outcome == "accepted" && plan.Command.Status == "unknown")
            return plan with { Revision = checked(plan.Revision + 1),
                Command = plan.Command with { Status = "accepted" } };
        return plan with { Revision = checked(plan.Revision + 1),
            Command = plan.Command with { Status = receipt.Outcome,
                Reason = Identifier(receipt.Reason) ? receipt.Reason : "" },
            Status = receipt.Outcome is "failed" or "unknown" ? "paused" : plan.Status,
            Reason = receipt.Outcome switch { "failed" => "CommandFailed",
                "unknown" when plan.Status != "paused" => "CommandOutcomeUnknown", _ => plan.Reason } };
    }

    public static AgentExecutionPlan Pause(AgentExecutionPlan plan, long expectedRevision, string reason)
    {
        CheckRevision(plan, expectedRevision);
        if (plan.Status != "active" || !Identifier(reason)) throw new InvalidOperationException("InvalidExecutionPause");
        return plan with { Revision = checked(plan.Revision + 1), Status = "paused", Reason = reason };
    }

    public static bool ConditionSatisfied(AgentExecutionCondition condition, JsonElement observation)
    {
        var current = observation;
        foreach (var part in condition.Path.Split('.'))
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
                throw new InvalidDataException("ExecutionObservationMissing");
        if (current.ValueKind != JsonValueKind.Number || !current.TryGetDouble(out var value) || !double.IsFinite(value))
            throw new InvalidDataException("ExecutionObservationInvalid");
        return condition.Operator switch { "gte" => value >= condition.Value, "lte" => value <= condition.Value,
            _ => throw new InvalidDataException("InvalidExecutionCondition") };
    }

    public static AgentExecutionPlan Branch(AgentExecutionPlan plan, long expectedRevision)
    {
        CheckRevision(plan, expectedRevision);
        if (plan.Status != "active" || plan.Command != null || plan.Cursor >= plan.Steps.Length ||
            plan.Steps[plan.Cursor].Condition is not { } condition)
            throw new InvalidOperationException("ExecutionBranchUnavailable");
        if (condition.OnFalseStepId.Length == 0) return Pause(plan, expectedRevision, "ConditionsNotMet");
        var next = Array.FindIndex(plan.Steps, s => s.Id == condition.OnFalseStepId);
        return plan with { Cursor = next, Iteration = 0, Revision = checked(plan.Revision + 1), Reason = "ConditionBranch" };
    }

    private static void ValidateRepeats(AgentExecutionStep[] steps)
    {
        if (steps.Any(s => s.Repeat is < 1 or > 64) || steps.Sum(s => (long)s.Repeat) > MaxDispatches)
            throw new InvalidDataException("InvalidExecutionPlanUpdate");
    }

    private static void ValidateConditions(AgentExecutionStep[] steps)
    {
        for (var i = 0; i < steps.Length; i++)
        {
            if (steps[i].Condition is not { } c) continue;
            if (c.Path is not ("inventorySummary.freeSlots" or "bodyNeeds.energy.value" or
                    "bodyNeeds.stamina.value" or "bodyNeeds.hunger" or "bodyNeeds.thirst" or "restReadiness.adrenalineTicksRemaining" or "restReadiness.idleRestCooldownTicksRemaining") ||
                c.Operator is not ("gte" or "lte") || !double.IsFinite(c.Value) ||
                c.OnFalseStepId == null || c.OnFalseStepId.Length > 0 &&
                Array.FindIndex(steps, s => s.Id == c.OnFalseStepId) <= i)
            {
                var error = new InvalidDataException("InvalidExecutionCondition");
                error.Data["conditionStep"] = steps[i].Id;
                error.Data["conditionTarget"] = c.OnFalseStepId;
                error.Data["laterStepIds"] = steps.Skip(i + 1).Select(s => s.Id).ToArray();
                throw error;
            }
        }
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
        if (plan.SchemaVersion is not (1 or 2 or 3) || !Identifier(plan.Id) || string.IsNullOrWhiteSpace(plan.WorldKey) ||
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
        if (plan.SchemaVersion == 1 && plan.Steps.Any(s => s.Condition != null))
            throw new InvalidDataException("ExecutionConditionRequiresSchema2");
        ValidateRepeats(plan.Steps);
        if (plan.Iteration < 0 || (plan.Cursor == plan.Steps.Length ? plan.Iteration != 0 : plan.Iteration >= plan.Steps[plan.Cursor].Repeat) ||
            plan.SchemaVersion < 3 && (plan.Iteration != 0 || plan.Steps.Any(s => s.Repeat > 1)))
            throw new InvalidDataException("ExecutionRepeatRequiresSchema3");
        ValidateConditions(plan.Steps);
    }
}
