namespace HexLive.AgentCore.Studio;

public sealed record AgentFleetStatus(Guid ProfileId, AgentRunState State, string? ErrorCode, string IntentSummary,
    string DiagnosticsErrorCode = "");

/// <summary>§163: one controller and immutable run configuration per profile; no shared model/voice.</summary>
public sealed class AgentFleet : IAsyncDisposable
{
    private readonly Func<AgentProfile, ServerProfile, CancellationToken, Task<IAgentSession>> _connect;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, AgentController> _controllers = new();
    private bool _disposed;
    public AgentFleet(Func<AgentProfile, ServerProfile, CancellationToken, Task<IAgentSession>> connect) => _connect = connect;

    public async Task StartAsync(AgentProfile profile, ServerProfile server, CancellationToken cancellationToken = default)
    {
        profile.Validate(); server.Validate();
        if (profile.ServerId != server.Id) throw new ArgumentException("ProfileServerMismatch");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_controllers.TryGetValue(profile.Id, out var prior))
            {
                if (prior.State is not (AgentRunState.Stopped or AgentRunState.Error))
                    throw new InvalidOperationException("AgentAlreadyRunning");
                await prior.DisposeAsync().ConfigureAwait(false);
            }
            // Capture this run's selections, not mutable global/default integration settings.
            var controller = new AgentController((selected, token) => _connect(selected, server, token));
            _controllers[profile.Id] = controller;
            await controller.StartAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(Guid profileId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_controllers.TryGetValue(profileId, out var controller))
                await controller.StopAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AgentFleetStatus>> SnapshotAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return _controllers.Select(x => new AgentFleetStatus(x.Key, x.Value.State, x.Value.ErrorCode, x.Value.IntentSummary, x.Value.DiagnosticsErrorCode)).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task StopAllAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await Task.WhenAll(_controllers.Values.Select(c => c.StopAsync())).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await Task.WhenAll(_controllers.Values.Select(async c => await c.DisposeAsync())).ConfigureAwait(false);
            _controllers.Clear();
        }
        finally { _gate.Release(); }
    }
}
