namespace HexLive.AgentCore.Studio;

public interface IAgentSession : IAsyncDisposable
{
    Task RunAsync(CancellationToken cancellationToken);
    Task DetachAsync(CancellationToken cancellationToken);
}

public interface IAgentSessionStatus
{
    AgentRunState State { get; }
    string IntentSummary { get; }
    string DiagnosticsErrorCode => "";
}

/// <summary>§163: explicit local run intent; the transport owns world/NPC validation.</summary>
public sealed class AgentController : IAsyncDisposable
{
    private readonly Func<AgentProfile, CancellationToken, Task<IAgentSession>> _connect;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _stop;
    private Task _run = Task.CompletedTask;
    private FileStream? _workspaceLease;
    private int _state = (int)AgentRunState.Stopped;
    private IAgentSession? _session;
    public AgentRunState State => (AgentRunState)Volatile.Read(ref _state) == AgentRunState.Running && _session is IAgentSessionStatus status
        ? status.State : (AgentRunState)Volatile.Read(ref _state);
    public string IntentSummary => (_session as IAgentSessionStatus)?.IntentSummary ?? string.Empty;
    public string DiagnosticsErrorCode => (_session as IAgentSessionStatus)?.DiagnosticsErrorCode ?? string.Empty;
    public string? ErrorCode { get; private set; }
    public AgentController(Func<AgentProfile, CancellationToken, Task<IAgentSession>> connect) => _connect = connect;

    public async Task StartAsync(AgentProfile profile, CancellationToken cancellationToken = default)
    {
        profile.Validate();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_run.IsCompleted) throw new InvalidOperationException("AgentAlreadyRunning");
            var root = Path.GetFullPath(profile.Workspace);
            Directory.CreateDirectory(root);
            // Lock file is never deleted: unlinking it while another process opens it splits the lock.
            _workspaceLease = new FileStream(Path.Combine(root, ".agent-studio.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            _stop?.Dispose();
            _stop = new CancellationTokenSource();
            ErrorCode = null;
            Volatile.Write(ref _state, (int)AgentRunState.Connecting);
            _run = RunAsync(profile, _stop.Token);
        }
        finally { _gate.Release(); }
    }

    private async Task RunAsync(AgentProfile profile, CancellationToken token)
    {
        IAgentSession? session = null;
        try
        {
            session = await _connect(profile, token).ConfigureAwait(false);
            _session = session;
            token.ThrowIfCancellationRequested();
            Volatile.Write(ref _state, (int)AgentRunState.Running);
            await session.RunAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Never expose exception bodies: network errors may contain transcript/credentials.
            ErrorCode = ex.GetType().Name;
        }
        finally
        {
            if (session != null)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await session.DetachAsync(deadline.Token).ConfigureAwait(false); }
                catch { ErrorCode ??= "DetachFailedAwaitingLeaseExpiry"; }
                try { await session.DisposeAsync().ConfigureAwait(false); }
                catch { ErrorCode ??= "SessionDisposalFailed"; }
            }
            _workspaceLease?.Dispose(); _workspaceLease = null;
            _session = null;
            Volatile.Write(ref _state, (int)(ErrorCode == null ? AgentRunState.Stopped : AgentRunState.Error));
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_run.IsCompleted)
            {
                Volatile.Write(ref _state, (int)AgentRunState.Stopping);
                _stop!.Cancel();
                await _run.ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync() { await StopAsync(); _stop?.Dispose(); }
}
