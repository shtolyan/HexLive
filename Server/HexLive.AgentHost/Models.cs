using System.Text.Json;
using System.Text.Json.Serialization;

namespace HexLive.AgentHost;

public sealed class MemoryUpdate
{
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("value")] public string Value { get; set; } = string.Empty;
    [JsonPropertyName("importance")] public float Importance { get; set; }
}

public sealed class CompanionAction
{
    [JsonPropertyName("tool")] public string Tool { get; set; } = string.Empty;
    [JsonPropertyName("arguments")] public JsonElement Arguments { get; set; }
}

public sealed class CompanionDecision
{
    [JsonPropertyName("speech")] public string Speech { get; set; } = string.Empty;
    [JsonPropertyName("emotion")] public string Emotion { get; set; } = "neutral";
    [JsonPropertyName("action")] public CompanionAction? Action { get; set; }
    [JsonPropertyName("reaction")] public string Reaction { get; set; } = "None";
    [JsonPropertyName("intentSummary")] public string IntentSummary { get; set; } = string.Empty;
    [JsonPropertyName("memoryUpserts")] public List<MemoryUpdate> MemoryUpserts { get; set; } = new();
    [JsonPropertyName("journalText")] public string JournalText { get; set; } = string.Empty;
}
