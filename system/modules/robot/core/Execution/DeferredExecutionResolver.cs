// Deferred execution resolution for NinjaTrader event ordering.
// When execution arrives before order/registry update, defer and retry.
// Used by harness tests; NT build uses adapter's ProcessUnresolvedRetry.

using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Deferred execution record for retry queue. Test-friendly structure.
/// </summary>
public sealed class DeferredExecution
{
    public string ExecutionId { get; set; } = "";
    public string OrderId { get; set; } = "";
    public string BrokerOrderId { get; set; } = "";
    public string Instrument { get; set; } = "";
    public DateTimeOffset ReceivedUtc { get; set; }
    public int RetryCount { get; set; }
}

/// <summary>
/// Queue for unresolved executions with retry semantics. Max 5 retries, 75ms interval.
/// </summary>
public sealed class UnresolvedExecutionQueue
{
    private readonly Queue<DeferredExecution> _queue = new();
    private readonly object _lock = new();
    public const int MaxRetries = 5;
    public const double RetryIntervalMs = 75.0;

    public void Enqueue(DeferredExecution item)
    {
        lock (_lock)
            _queue.Enqueue(item);
    }

    public bool TryDequeue(out DeferredExecution? item)
    {
        lock (_lock)
        {
            if (_queue.Count > 0)
            {
                item = _queue.Dequeue();
                return true;
            }
            item = null;
            return false;
        }
    }

    public int Count
    {
        get { lock (_lock) return _queue.Count; }
    }

    private int _deferredCount;
    private int _resolutionRetries;
    public int DeferredCount => _deferredCount;
    public void IncrementDeferredCount() => System.Threading.Interlocked.Increment(ref _deferredCount);
    public int ResolutionRetries => _resolutionRetries;
    public void IncrementResolutionRetries() => System.Threading.Interlocked.Increment(ref _resolutionRetries);
}
