namespace HexLive.VoiceBrain;

/// <summary>§171.7: everything comes from the environment; no secret is ever logged or returned.</summary>
public sealed class BrainOptions
{
    public required Uri McpUri { get; init; }
    public required string McpToken { get; init; }
    public required string OpenAiKey { get; init; }
    public required string ElevenLabsKey { get; init; }
    /// <summary>Shared secret ElevenLabs sends as Bearer to /v1/chat/completions.</summary>
    public required string BrainSecret { get; init; }
    /// <summary>HMAC key for session tickets; random per process when unset.</summary>
    public required byte[] TicketKey { get; init; }
    public string AgentId { get; init; } = "";
    public string Model { get; init; } = "gpt-5.6-luna";
    public string ReasoningEffort { get; init; } = "none"; // chat-completions + tools require none for gpt-5.6-luna
    public string UsageDirectory { get; init; } = "";
    public string TariffsPath { get; init; } = "";
    public string StateDirectory { get; init; } = "";
    public string VoicesPath { get; init; } = "";

    public static BrainOptions FromEnvironment()
    {
        static string Need(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v
            ? v : throw new InvalidOperationException("Missing environment variable " + name);
        static string Opt(string name, string fallback) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;
        var ticket = Environment.GetEnvironmentVariable("VOICEBRAIN_TICKET_KEY");
        return new BrainOptions
        {
            McpUri = new Uri(Need("HEXLIVE_MCP_URI")),
            McpToken = Need("HEXLIVE_MCP_TOKEN"),
            OpenAiKey = Need("OPENAI_API_KEY"),
            ElevenLabsKey = Need("ELEVENLABS_API_KEY"),
            BrainSecret = Need("VOICEBRAIN_SECRET"),
            TicketKey = ticket is { Length: > 0 } ? Convert.FromBase64String(ticket)
                : System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),
            AgentId = Opt("VOICEBRAIN_AGENT_ID", ""),
            Model = Opt("VOICEBRAIN_MODEL", "gpt-5.6-luna"),
            ReasoningEffort = Opt("VOICEBRAIN_REASONING", "none"),
            UsageDirectory = Opt("VOICEBRAIN_USAGE_DIR", ""),
            TariffsPath = Opt("VOICEBRAIN_TARIFFS", ""),
            StateDirectory = Opt("VOICEBRAIN_STATE_DIR", Path.Combine(Path.GetTempPath(), "hexlive-voicebrain")),
            VoicesPath = Opt("VOICEBRAIN_VOICES", ""),
        };
    }
}
