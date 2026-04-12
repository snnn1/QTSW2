// Execution deduplication guard for NinjaTrader event ordering.
// Prevents duplicate fill processing when NT delivers duplicate execution updates.
// Retention: 12 minutes. Used by harness tests; NT build uses IEA's built-in dedup.

using System;
using System.Collections.Concurrent;
using System.Linq;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Deduplicates execution updates by executionId. Prevents double fills, lifecycle corruption.
/// </summary>
public sealed class ExecutionDeduplicator
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _processedExecutionIds = new();
    private int _insertCount;
    private const int EvictionInterval = 100;
    private const double MaxAgeMinutes = 12.0;

    /// <summary>Returns true if duplicate (caller should skip), false if new (marks as processed).</summary>
    public bool TryMarkAndCheckDuplicate(string? executionId, string? orderId, long executionTimeTicks, int quantity, string? marketPosition)
    {
        var key = !string.IsNullOrEmpty(executionId)
            ? executionId
            : $"{orderId}|{executionTimeTicks}|{quantity}|{marketPosition ?? ""}|{orderId}";
        if (string.IsNullOrEmpty(key)) return false;

        if (_processedExecutionIds.TryAdd(key, DateTimeOffset.UtcNow))
        {
            var c = System.Threading.Interlocked.Increment(ref _insertCount);
            if (c % EvictionInterval == 0)
                EvictEntries();
            return false;
        }
        return true;
    }

    private void EvictEntries()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-MaxAgeMinutes);
        foreach (var k in _processedExecutionIds.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList())
            _processedExecutionIds.TryRemove(k, out _);
    }

    private int _duplicateCount;

    /// <summary>Count of duplicates detected.</summary>
    public int DuplicateCount => _duplicateCount;

    /// <summary>Call when duplicate detected to track metric.</summary>
    public void IncrementDuplicateCount() => System.Threading.Interlocked.Increment(ref _duplicateCount);
}
