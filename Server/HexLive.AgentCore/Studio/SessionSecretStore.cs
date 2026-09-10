namespace HexLive.AgentCore.Studio;

/// <summary>§160: one OS read per credential per application session, including denials.</summary>
public sealed class SessionSecretStore(ISecretStore native) : ISecretStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> _denied = new(StringComparer.Ordinal);

    public async Task<string?> ReadAsync(string id, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_denied.TryGetValue(id, out var failure)) throw failure;
            if (_values.TryGetValue(id, out var value)) return value;
            try { value = await native.ReadAsync(id, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _denied[id] = ex; throw; }
            _values[id] = value;
            token.ThrowIfCancellationRequested();
            return value;
        }
        finally { _gate.Release(); }
    }

    // Only an explicit user action may retry failures. Successful values stay cached.
    public async Task RetryFailedReadsAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { _denied.Clear(); }
        finally { _gate.Release(); }
    }

    public async Task WriteAsync(string id, string value, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await native.WriteAsync(id, value, token);
            _values[id] = value;
            _denied.Remove(id);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string id, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await native.DeleteAsync(id, token);
            _values[id] = null;
            _denied.Remove(id);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> InitializeAsync(IEnumerable<string> ids, CancellationToken token, Action<Exception>? onFailure = null)
    {
        var succeeded = true;
        foreach (var id in ids.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
        {
            try { await ReadAsync(id, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { succeeded = false; onFailure?.Invoke(ex); }
        }
        return succeeded;
    }
}
