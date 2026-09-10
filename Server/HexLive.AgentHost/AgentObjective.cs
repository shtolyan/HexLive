using System.Text.Json.Serialization;

namespace HexLive.AgentHost;

// One profile-owned objective, bound to its actual game world/avatar. This is
// durable motivation, never a claim about the body's current physical action.
public sealed class AgentObjective
{
    public long Revision { get; set; }
    // Stable across pause/resume; Revision still protects every state mutation.
    public long StartedRevision { get; set; }
    public string Status { get; set; } = "none";
    public string Text { get; set; } = "";
    public string Reason { get; set; } = "";
    public string WorldKey { get; set; } = "";
    public int AvatarNpcId { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class AgentObjectiveUpdate
{
    [JsonPropertyName("operation")] public string Operation { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}

public static class AgentObjectivePolicy
{
    public const int TextLimit = 600, ReasonLimit = 240;

    // Pure validation/projection: the store applies the returned value only in
    // the same transaction as AppliedTurnIds. Host preflight uses this same rule.
    public static AgentObjective Apply(AgentObjective? current, AgentObjectiveUpdate update,
        long expectedRevision, string worldKey, int avatarNpcId, DateTimeOffset now)
    {
        current ??= new AgentObjective();
        if (expectedRevision != current.Revision)
            throw new AgentObjectiveConflictException();
        ValidateUpdate(update);
        var text = update.Text.Trim();
        var reason = update.Reason.Trim();
        var sameBody = current.WorldKey == worldKey && current.AvatarNpcId == avatarNpcId;
        var status = update.Operation switch
        {
            "set" when text.Length > 0 && worldKey.Length > 0 && avatarNpcId > 0 => "active",
            "pause" when current.Status == "active" && text.Length == 0 => "paused",
            "resume" when current.Status == "paused" && sameBody && text.Length == 0 => "active",
            "complete" when (current.Status is "active" or "paused") && sameBody && text.Length == 0 => "completed",
            "clear" when (current.Status is "active" or "paused" or "completed") && text.Length == 0 => "canceled",
            _ => throw new InvalidDataException("InvalidObjectiveTransition"),
        };
        var replaced = update.Operation == "set";
        return new AgentObjective
        {
            Revision = checked(current.Revision + 1), Status = status,
            StartedRevision = replaced ? checked(current.Revision + 1) :
                current.StartedRevision > 0 ? current.StartedRevision : current.Revision,
            Text = replaced ? text : current.Text, Reason = reason,
            WorldKey = replaced ? worldKey : current.WorldKey,
            AvatarNpcId = replaced ? avatarNpcId : current.AvatarNpcId, UpdatedUtc = now,
        };
    }

    public static void ValidateUpdate(AgentObjectiveUpdate update)
    {
        if (update.Operation is not ("set" or "pause" or "resume" or "complete" or "clear") ||
            update.Text == null || update.Reason == null || update.Text.Length > TextLimit ||
            update.Reason.Length > ReasonLimit || string.IsNullOrWhiteSpace(update.Reason) ||
            (update.Operation == "set" ? string.IsNullOrWhiteSpace(update.Text) : update.Text.Trim().Length != 0))
            throw new InvalidDataException("InvalidObjectiveUpdate");
    }

    public static string Describe(AgentObjective? goal, string worldKey, int npcId)
    {
        if (goal == null || goal.Status == "none") return "Долгосрочная цель: нет. Версия: 0.";
        var inScope = goal.WorldKey == worldKey && goal.AvatarNpcId == npcId;
        static string Bounded(string? value, int limit)
        {
            var clean = (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
            return clean.Length > limit ? clean[..limit] : clean;
        }
        return $"Долгосрочная цель (версия {goal.Revision}, статус {Bounded(goal.Status, 16)}): {Bounded(goal.Text, TextLimit)}\n" +
            $"Причина последнего изменения: {Bounded(goal.Reason, ReasonLimit)}\n" +
            (inScope ? "Это цель, а не уже выполненное физическое действие." :
                "Цель относится к другому миру/персонажу: не исполняй её здесь автоматически. Пересмотри, приостанови или сними.");
    }
}

public sealed class AgentObjectiveConflictException : InvalidOperationException
{
    public AgentObjectiveConflictException() : base("ObjectiveRevisionConflict") { }
}
