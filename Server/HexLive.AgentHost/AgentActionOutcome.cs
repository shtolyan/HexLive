using System.Text.Json;

namespace HexLive.AgentHost;

/// <summary>§160: controller outcome after the simulation has swept its transient plan status.</summary>
public sealed class AgentActionOutcome
{
    private readonly int _npcId;
    private readonly string _epoch;
    public long Watermark { get; private set; }
    public string? Result { get; private set; }
    public bool HasMore { get; private set; }

    public AgentActionOutcome(int npcId, JsonElement boundary)
    {
        _npcId = npcId;
        _epoch = boundary.TryGetProperty("sessionEpoch", out var epoch) ? epoch.GetString() ?? "" : "";
        Watermark = boundary.TryGetProperty("watermark", out var mark) ? mark.GetInt64() : 0;
    }

    public void Observe(JsonElement batch)
    {
        if (_epoch.Length == 0) return;
        HasMore = batch.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True;
        if (!batch.TryGetProperty("sessionEpoch", out var epoch) || epoch.GetString() != _epoch ||
            (batch.TryGetProperty("sessionReset", out var reset) && reset.ValueKind == JsonValueKind.True))
        {
            Result = "ActionOutcomeUnavailable";
            return;
        }
        if (batch.TryGetProperty("gap", out var gap) && gap.ValueKind == JsonValueKind.True)
            Result ??= "ActionOutcomeUnavailable";
        if (batch.TryGetProperty("events", out var events))
            foreach (var item in events.EnumerateArray())
            {
                if (item.GetProperty("seq").GetInt64() <= Watermark ||
                    item.GetProperty("entityId").GetInt32() != _npcId ||
                    item.GetProperty("type").GetString() != "ManualOrderFinished") continue;
                var message = item.GetProperty("message").GetString() ?? "";
                // Exact fixed controller payloads only, never copy arbitrary event text.
                Result ??= message switch
                {
                    "Order=PlayerOrder Outcome=Completed" => "PlanCompleted",
                    "Order=PlayerOrder Outcome=Failed" => "PlanFailed",
                    "Order=PlayerOrder Outcome=Invalid" => "PlanInvalid",
                    "Order=PlayerAttack Outcome=TargetDown" => "PlanCompleted",
                    "Order=PlayerAttack Outcome=TargetGone" => "TargetGone",
                    _ => null
                };
            }
        if (batch.TryGetProperty("watermark", out var watermark)) Watermark = watermark.GetInt64();
    }
}
