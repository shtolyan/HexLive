using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// Reusable request/drain implementation for offline and future host providers.
/// It owns the worker tasks and result queue, so <see cref="TryRequest"/> never
/// executes provider reasoning on the simulation thread.
/// </summary>
public abstract class QueuedLlmControlProvider : ILlmControlProvider
{
    private readonly ConcurrentQueue<LlmControlResult> _results = new();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _requests = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _lifecycleGate = new();
    private volatile bool _disposed;

    public bool TryRequest(LlmControlRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        CancellationTokenSource linkedCancellation;
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return false;
            }

            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                request.CancellationToken, _lifetime.Token);
            if (!_requests.TryAdd(request.RequestId, linkedCancellation))
            {
                linkedCancellation.Dispose();
                return false;
            }

            // Deliberately do not pass the cancellation token to Task.Run. The
            // worker must start in order to publish a canceled result even when
            // cancellation raced with request submission.
            _ = Task.Run(() => CompleteAsync(request, linkedCancellation));
        }

        return true;
    }

    public bool TryDequeueResult(out LlmControlResult result) =>
        _results.TryDequeue(out result);

    protected abstract Task<LlmDecision> DecideAsync(
        LlmDecisionContext context, CancellationToken cancellationToken);

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelWithoutThrowing(_lifetime);
        }

        foreach (var cancellation in _requests.Values)
        {
            CancelWithoutThrowing(cancellation);
        }

        while (_results.TryDequeue(out _))
        {
        }

        _lifetime.Dispose();
    }

    private async Task CompleteAsync(
        LlmControlRequest request, CancellationTokenSource cancellation)
    {
        LlmControlResult result;
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var decision = await DecideAsync(request.Context, cancellation.Token)
                .ConfigureAwait(false);
            result = cancellation.IsCancellationRequested
                ? LlmControlResult.Canceled(request)
                : LlmControlResult.Completed(request, decision);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            result = LlmControlResult.Canceled(request);
        }
        catch (Exception exception)
        {
            result = LlmControlResult.Failed(request, exception);
        }
        finally
        {
            _requests.TryRemove(request.RequestId, out _);
            cancellation.Dispose();
        }

        lock (_lifecycleGate)
        {
            if (!_disposed)
            {
                _results.Enqueue(result);
            }
        }
    }

    private static void CancelWithoutThrowing(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (AggregateException)
        {
            // Teardown must continue even if third-party cancellation callbacks
            // are faulty. Provider exceptions are otherwise surfaced as results.
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

}
