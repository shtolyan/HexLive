namespace HexLive.AgentHost;

public sealed class AgentHostOptions
{
    public required Uri McpUri { get; init; }
    public required string McpToken { get; init; }
    public required string ProfileId { get; init; }
    public required string DisplayName { get; init; }
    public required string MemoryDirectory { get; init; }
    public required string StateDirectory { get; init; }
    public required string WorldId { get; init; }
    public required string XaiKey { get; init; }
    public required string ElevenLabsKey { get; init; }
    public required string XaiModel { get; init; }
    public required string ElevenLabsModel { get; init; }
    public required string ElevenLabsVoiceId { get; init; }
    public bool FakeProviders { get; init; }

    public string StatusPath => Path.Combine(StateDirectory, "agent-host-status.json");
    public string OutboxPath => Path.Combine(StateDirectory, "agent-outbox.json");

    public AgentProviderOptions ProviderOptions => new()
    {
        McpUri = McpUri,
        McpToken = McpToken,
        XaiKey = XaiKey,
        ElevenLabsKey = ElevenLabsKey,
        XaiModel = XaiModel,
        ElevenLabsModel = ElevenLabsModel,
        ElevenLabsVoiceId = ElevenLabsVoiceId,
        FakeProviders = FakeProviders,
    };

    public static AgentHostOptions Load(bool requireProviders = true)
    {
        static string Env(string name, string fallback = "") =>
            Environment.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } value
                ? value
                : fallback;

        var profileId = Env("HEXLIVE_AGENT_PROFILE", "masha");
        var memory = Env("MASHA_HOME", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Masha"));
        var state = Env("HEXLIVE_AGENT_STATE", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HexLive", "AgentHost", profileId));
        var fake = Env("HEXLIVE_AGENT_FAKE", Env("HEXLIVE_MOLLY_FAKE")) == "1";
        var result = new AgentHostOptions
        {
            McpUri = new Uri(Env("HEXLIVE_MCP_URL", "http://127.0.0.1:5123/mcp")),
            McpToken = Env("HEXLIVE_MCP_TOKEN"),
            ProfileId = profileId,
            DisplayName = Env("HEXLIVE_AGENT_NAME", "Маша"),
            MemoryDirectory = Path.GetFullPath(memory),
            StateDirectory = Path.GetFullPath(state),
            WorldId = Env("HEXLIVE_WORLD_ID"),
            XaiKey = Env("XAI_API_KEY"),
            ElevenLabsKey = Env("ELEVENLABS_API_KEY"),
            XaiModel = Env("HEXLIVE_AGENT_MODEL", Env("HEXLIVE_MOLLY_MODEL", "grok-4.20-0309-reasoning")),
            ElevenLabsModel = Env("HEXLIVE_TTS_MODEL", "eleven_multilingual_v2"),
            ElevenLabsVoiceId = Env("HEXLIVE_MASHA_VOICE_ID", "NsFK0aDGLbVusA7tQfOB"),
            FakeProviders = fake,
        };
        if (result.McpToken.Length == 0)
            throw new InvalidOperationException("HEXLIVE_MCP_TOKEN is required.");
        if (result.ProfileId.Length is < 1 or > 48 || result.DisplayName.Length is < 1 or > 48)
            throw new InvalidOperationException("Agent profile and display name must contain 1..48 characters.");
        if (requireProviders && !fake &&
            (result.XaiKey.Length == 0 || result.ElevenLabsKey.Length == 0))
            throw new InvalidOperationException(
                "XAI_API_KEY and ELEVENLABS_API_KEY are required unless HEXLIVE_AGENT_FAKE=1.");
        Directory.CreateDirectory(result.MemoryDirectory);
        Directory.CreateDirectory(result.StateDirectory);
        return result;
    }
}
