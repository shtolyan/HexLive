using System.Text.Json;

namespace HexLive.AgentHost;

public sealed partial class AgentHostRuntime
{
    private string _perceptionEpoch = "";
    private long _perceptionWatermark;

    private object PerceptionRequest(int npcId) => new
    {
        npcId, perceptionEpoch = _perceptionEpoch, perceptionSince = _perceptionWatermark,
    };

    private void ConsumePerception(JsonElement state)
    {
        // Called only after the decision is in the durable outbox. A failed,
        // cancelled or skipped model turn must receive these sightings again.
        // The response watermark also excludes sightings collected during LLM latency.
        if (!state.TryGetProperty("recentPerception", out var recent) ||
            recent.ValueKind != JsonValueKind.Object ||
            !recent.TryGetProperty("epoch", out var epoch) || epoch.ValueKind != JsonValueKind.String ||
            !recent.TryGetProperty("watermark", out var mark) || mark.ValueKind != JsonValueKind.Number ||
            !mark.TryGetInt64(out var sequence) || sequence < 0) return;
        _perceptionEpoch = epoch.GetString() ?? "";
        _perceptionWatermark = sequence;
    }
}
