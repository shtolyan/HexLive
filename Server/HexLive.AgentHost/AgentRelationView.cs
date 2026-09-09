using System.Text.Encodings.Web;
using System.Text.Json;

namespace HexLive.AgentHost;

/// <summary>§159.10: public speaker-specific assessment; private memory never enters the view.</summary>
public static class AgentRelationView
{
    // AgentCore has no Simulation reference; the integration test pins this MCP wire budget.
    public const int MaxCharacters = 1024;
    public static string Serialize(MashaArchive archive, string key)
    {
        var bond = MashaMemoryStore.BondFor(archive, key);
        archive.Speakers.TryGetValue(key, out var speaker);
        var reason = speaker?.LastAssessmentReason ?? "";
        // Keep a bounded plain explanation; JSON escaping must not turn control
        // characters into an oversized wire payload, or markup into UI formatting.
        reason = new string(reason.Where(c => !char.IsControl(c) || c == '\n').Take(240).ToArray());
        string Encode() => JsonSerializer.Serialize(new
        {
            familiarity = Math.Clamp(bond.Familiarity, 0f, 1f),
            trust = Math.Clamp(bond.Trust, 0f, 1f),
            affinity = Math.Clamp(bond.Affinity, -1f, 1f),
            speakerId = key.Length > 0 ? key.Split(':').Last() : "",
            voiceName = speaker?.VoiceName ?? AgentPromptFiles.Text("AgentHostRuntime.15"),
            reason,
            hasChange = speaker?.LastFamiliarityDelta != null && speaker.LastTrustDelta != null && speaker.LastAffinityDelta != null,
            familiarityDelta = speaker?.LastFamiliarityDelta ?? 0f,
            trustDelta = speaker?.LastTrustDelta ?? 0f,
            affinityDelta = speaker?.LastAffinityDelta ?? 0f,
        }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var encoded = Encode();
        while (encoded.Length > MaxCharacters && reason.Length > 0)
        {
            reason = reason[..^1];
            if (reason.Length > 0 && char.IsHighSurrogate(reason[^1])) reason = reason[..^1];
            encoded = Encode();
        }
        return encoded;
    }
}
