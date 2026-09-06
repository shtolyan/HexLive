namespace HexLive.AgentHost;

/// <summary>Secrets and provider choices owned by the local AgentHost.</summary>
public sealed class AgentProviderOptions
{
    public required Uri McpUri { get; init; }
    public required string McpToken { get; init; }
    public required string XaiKey { get; init; }
    public required string ElevenLabsKey { get; init; }
    public required string XaiModel { get; init; }
    public required string ElevenLabsModel { get; init; }
    public required string ElevenLabsVoiceId { get; init; }
    public bool FakeProviders { get; init; }
    public string LlmBackend { get; init; } = "xai";
    public string CodexExecutable { get; init; } = "/Applications/ChatGPT.app/Contents/Resources/codex";
}
