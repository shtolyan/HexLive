using System.Text.Json;
using HexLive.AgentHost;

namespace HexLive.AgentCore.Studio;

/// <summary>One profile, one transport, one set of providers. No process-wide provider environment.</summary>
public sealed class AgentSessionFactory(ISecretStore secrets, string codexExecutable)
{
    public async Task<IAgentSession> ConnectAsync(AgentProfile profile, ServerProfile server,
        CancellationToken token, HttpMessageHandler? mcpHandler = null)
    {
        profile.Validate(); server.Validate();
        if (profile.ServerId != server.Id) throw new InvalidDataException("ProfileServerMismatch");
        var credential = await RequireSecret(server.CredentialId, token);
        var options = new AgentHostOptions
        {
            McpUri = server.McpEndpoint, McpToken = credential,
            ProfileId = profile.Id.ToString("N"), DisplayName = profile.Name,
            MemoryDirectory = profile.Workspace, StateDirectory = Path.Combine(profile.Workspace, ".state", "runtime"),
            WorldId = profile.WorldId, ExpectedWorldId = profile.WorldId, NpcId = profile.NpcId,
            HeartbeatSeconds = profile.HeartbeatSeconds,
            InitialIdentity = new MashaIdentity { Id = profile.Id.ToString("N"), Name = profile.Name, Age = 23, Traits = [] },
            XaiKey = "", ElevenLabsKey = "", XaiModel = "", ElevenLabsModel = "", ElevenLabsVoiceId = "",
        };
        // Read-only preflight before creating/modifying a workspace or attachment.
        using (var check = new McpClient(options.ProviderOptions, mcpHandler))
        {
            var world = await check.CallToolAsync("world_status", new { }, token);
            if (!world.TryGetProperty("worldId", out var id) || id.GetString() != profile.WorldId)
                throw new InvalidDataException("SelectedWorldChanged");
            var roster = await check.CallToolAsync("list_colonists", new { }, token);
            if (!roster.GetProperty("colonists").EnumerateArray().Any(n =>
                n.GetProperty("npcId").GetInt32() == profile.NpcId &&
                (!n.TryGetProperty("health", out var health) || health.GetSingle() > 0)))
                throw new InvalidDataException("SelectedCharacterUnavailable");
        }
        token.ThrowIfCancellationRequested();
        IModelAdapter model;
        if (profile.Model.Provider == ModelProviderKind.Codex)
        {
            await CodexDecisionRunner.CheckLoginAsync(codexExecutable, token);
            model = new CodexModelAdapter(codexExecutable, profile.Model.IntegrationId);
        }
        else
        {
            // Fail missing credentials before attaching; never fall back to a different account.
            _ = await RequireSecret(profile.Model.IntegrationId, token);
            model = new HttpModelAdapter(profile.Model.Provider, profile.Model.IntegrationId,
                t => RequireSecret(profile.Model.IntegrationId, t));
        }
        McpClient? knowledge = null;
        IAgentProviders? providers = null;
        try
        {
            IVoiceAdapter voice = profile.Voice == null ? new NoVoiceAdapter() :
                new ElevenLabsVoiceAdapter(profile.Voice.IntegrationId, t => RequireSecret(profile.Voice.IntegrationId, t));
            var inner = new AgentProviders(options.ProviderOptions, model, profile.Model,
                voice, profile.Voice ?? new VoiceSelection("none", "none", "none"));
            knowledge = new McpClient(options.ProviderOptions, mcpHandler);
            providers = new KnowledgeAwareAgentProviders(inner, knowledge);
            return new RuntimeSession(new AgentHostRuntime(options, providers, mcpHandler), model as IDisposable);
        }
        catch
        {
            providers?.Dispose(); knowledge?.Dispose(); (model as IDisposable)?.Dispose(); throw;
        }
    }

    private async Task<string> RequireSecret(string id, CancellationToken token) =>
        await secrets.ReadAsync(id, token) is { Length: > 0 } value ? value :
            throw new InvalidOperationException("MissingIntegrationCredential");

    private sealed class RuntimeSession(AgentHostRuntime runtime, IDisposable? model) : IAgentSession, IAgentSessionStatus
    {
        public AgentRunState State => runtime.CurrentPhase switch
        {
            "Sleeping" => AgentRunState.WorldPaused,
            "Reconnecting" => AgentRunState.Reconnecting,
            "Starting" => AgentRunState.Connecting,
            _ => AgentRunState.Running
        };
        public string IntentSummary => runtime.LastIntentSummary;
        private readonly CancellationTokenSource _stop = new();
        private Task? _run;
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            if (_run != null) throw new InvalidOperationException("SessionAlreadyStarted");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
            _run = runtime.RunAsync(linked.Token);
            await _run.ConfigureAwait(false);
            if (!linked.IsCancellationRequested && runtime.TerminalErrorCode != null)
                throw new InvalidOperationException("AgentTargetOrAuthorizationChanged");
        }
        public async Task DetachAsync(CancellationToken token)
        {
            _stop.Cancel();
            if (_run != null) await _run.WaitAsync(token).ConfigureAwait(false);
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (_run != null) await _run.ConfigureAwait(false);
            model?.Dispose(); _stop.Dispose();
        }
    }
}
