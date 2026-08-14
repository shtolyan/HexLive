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
    private readonly SemaphoreSlim _outstandingSlots;
    private readonly SemaphoreSlim _workerSlots;
    private readonly object _lifecycleGate = new();
    private volatile bool _disposed;

    protected QueuedLlmControlProvider()
        : this(
            SpecLlmControl.MaxProviderQueuedRequests,
            SpecLlmControl.MaxProviderConcurrentRequests)
    {
    }

    /// <summary>
    /// Bounds both sides of the provider-owned pump. Queued work is waiting to
    /// enter <see cref="DecideAsync"/>; outstanding work also includes active
    /// calls and completed results not yet drained by the simulation.
    /// </summary>
    protected QueuedLlmControlProvider(
        int maxQueuedRequests,
        int maxConcurrentRequests)
    {
        if (maxQueuedRequests < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxQueuedRequests));
        }

        if (maxConcurrentRequests <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentRequests));
        }

        var maxOutstandingRequests = checked(maxQueuedRequests + maxConcurrentRequests);
        _outstandingSlots = new SemaphoreSlim(
            maxOutstandingRequests, maxOutstandingRequests);
        _workerSlots = new SemaphoreSlim(
            maxConcurrentRequests, maxConcurrentRequests);
    }

    public bool TryRequest(LlmControlRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        // This is the only admission operation and it never waits. The lease is
        // held until the result is drained (or the provider is disposed), which
        // bounds queued work, active calls and the provider-owned result queue as
        // one budget.
        if (!_outstandingSlots.Wait(0))
        {
            return false;
        }

        CancellationTokenSource linkedCancellation;
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                _outstandingSlots.Release();
                return false;
            }

            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                request.CancellationToken, _lifetime.Token);
            if (!_requests.TryAdd(request.RequestId, linkedCancellation))
            {
                linkedCancellation.Dispose();
                _outstandingSlots.Release();
                return false;
            }

            // Deliberately do not pass the cancellation token to Task.Run. The
            // worker must start in order to publish a canceled result even when
            // cancellation raced with request submission.
            _ = Task.Run(() => CompleteAsync(request, linkedCancellation));
        }

        return true;
    }

    public bool TryDequeueResult(out LlmControlResult result)
    {
        if (!_results.TryDequeue(out result))
        {
            return false;
        }

        _outstandingSlots.Release();
        return true;
    }

    protected abstract Task<LlmDecision> DecideAsync(
        LlmDecisionContext context, CancellationToken cancellationToken);

    public virtual void Dispose()
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
            _outstandingSlots.Release();
        }

        _lifetime.Dispose();
    }

    private async Task CompleteAsync(
        LlmControlRequest request, CancellationTokenSource cancellation)
    {
        LlmControlResult result;
        var enteredWorker = false;
        try
        {
            await _workerSlots.WaitAsync(cancellation.Token).ConfigureAwait(false);
            enteredWorker = true;
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
            if (enteredWorker)
            {
                _workerSlots.Release();
            }

            _requests.TryRemove(request.RequestId, out _);
            cancellation.Dispose();
        }

        lock (_lifecycleGate)
        {
            if (!_disposed)
            {
                _results.Enqueue(result);
                return;
            }
        }

        // Dispose drained every queued result under the same gate. A worker
        // completing afterwards owns the remaining outstanding lease.
        _outstandingSlots.Release();
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
