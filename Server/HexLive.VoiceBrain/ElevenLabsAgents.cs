using System.Text.Json;

namespace HexLive.VoiceBrain;

/// <summary>§171.7: the only two ElevenLabs Agents calls the brain makes; the API key never leaves this class.</summary>
public sealed class ElevenLabsAgents(HttpClient http, string apiKey)
{
    private const string Api = "https://api.elevenlabs.io";

    /// <summary>One-shot WebRTC conversation token for a private agent; the browser never sees the key.</summary>
    public async Task<string> ConversationTokenAsync(string agentId, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            Api + "/v1/convai/conversation/token?agent_id=" + Uri.EscapeDataString(agentId));
        request.Headers.Add("xi-api-key", apiKey);
        using var response = await http.SendAsync(request, token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("ElevenLabs token HTTP " + (int)response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("token", out var value) ||
            value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException("ElevenLabsTokenMissing");
        return value.GetString()!;
    }

    /// <summary>Billing facts of a finished conversation: duration in seconds and the provider's own cost figure, if given.</summary>
    public async Task<ConversationFacts> ConversationAsync(string conversationId, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Api + "/v1/convai/conversations/" + Uri.EscapeDataString(conversationId));
        request.Headers.Add("xi-api-key", apiKey);
        using var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) return new(null, null, null, "http-" + (int)response.StatusCode, "");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = doc.RootElement;
        double? seconds = null; decimal? cost = null, usd = null;
        if (root.TryGetProperty("metadata", out var meta))
        {
            if (meta.TryGetProperty("call_duration_secs", out var d) && d.ValueKind == JsonValueKind.Number) seconds = d.GetDouble();
            if (meta.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number) cost = c.GetDecimal();
            if (meta.TryGetProperty("cost_fiat", out var f) && f.ValueKind == JsonValueKind.Number && f.GetDecimal() >= 0) usd = f.GetDecimal();
        }
        var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
        var user = root.TryGetProperty("user_id", out var u) ? u.GetString() ?? "" : "";
        return new(seconds, cost, usd, status, user);
    }
}

public sealed record ConversationFacts(double? Seconds, decimal? ProviderCredits, decimal? CostUsd, string Status, string UserId);
