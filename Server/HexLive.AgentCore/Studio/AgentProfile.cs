namespace HexLive.AgentCore.Studio;

public enum ModelProviderKind { OpenAI, Grok, Claude, DeepSeek, Codex }
public enum AgentRunState { Stopped, Connecting, Running, WorldPaused, Reconnecting, Stopping, Error }

// Profiles deliberately contain references to credentials, never their values.
public sealed record ModelSelection(ModelProviderKind Provider, string IntegrationId,
    string ModelId, string? Reasoning = null);
public sealed record VoiceSelection(string IntegrationId, string VoiceId, string ModelId);
public sealed record AgentProfile(Guid Id, string Name, string Workspace,
    Guid ServerId, string WorldId, int NpcId, ModelSelection Model,
    VoiceSelection? Voice = null, int HeartbeatSeconds = 30)
{
    public void Validate()
    {
        if (Id == Guid.Empty || ServerId == Guid.Empty || NpcId <= 0 ||
            string.IsNullOrWhiteSpace(WorldId) || string.IsNullOrWhiteSpace(Workspace) ||
            string.IsNullOrWhiteSpace(Name) || Name.Length > 48 ||
            HeartbeatSeconds is < 5 or > 3600 || string.IsNullOrWhiteSpace(Model.ModelId))
            throw new InvalidDataException("InvalidAgentProfile");
    }
}

public sealed record ServerProfile(Guid Id, string Name, Uri McpEndpoint, string CredentialId)
{
    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 48 ||
            string.IsNullOrWhiteSpace(CredentialId) || McpEndpoint == null || !McpEndpoint.IsAbsoluteUri ||
            (McpEndpoint.Scheme != "https" && !(McpEndpoint.IsLoopback && McpEndpoint.Scheme == "http")) ||
            McpEndpoint.UserInfo.Length != 0 || McpEndpoint.Query.Length != 0 ||
            McpEndpoint.Fragment.Length != 0)
            throw new InvalidDataException("SecureMcpEndpointRequired");
    }
}

public sealed record ModelDescriptor(string Id, IReadOnlyList<string> ReasoningModes);
public sealed record ModelRequest(string Instructions, string Context, string Input);
public sealed record ModelAnswer(string DecisionJson, long? InputTokens = null, long? OutputTokens = null);
public interface IModelAdapter
{
    Task<ModelAnswer> CompleteAsync(ModelSelection selection, ModelRequest request, CancellationToken cancellationToken);
}
public interface IModelCatalog
{
    Task<IReadOnlyList<ModelDescriptor>> ListAsync(CancellationToken cancellationToken);
}
public interface IVoiceAdapter
{
    Task<byte[]> SynthesizeWavAsync(VoiceSelection selection, string text, CancellationToken cancellationToken);
}
public interface ISecretStore
{
    Task<string?> ReadAsync(string id, CancellationToken cancellationToken);
    Task WriteAsync(string id, string value, CancellationToken cancellationToken);
    Task DeleteAsync(string id, CancellationToken cancellationToken);
}
