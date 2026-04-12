// Gap 5: Per-stream monotonic sequence for canonical execution events.
// Stream key: {tradingDate}|{instrument} for per-instrument per-trading-day ordering.

using System;
using System.Collections.Concurrent;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Manages monotonic event sequence numbers per stream.
/// Thread-safe: lock around increment.
/// </summary>
public sealed class ExecutionEventSequence
{
    private readonly ConcurrentDictionary<string, long> _sequences = new();
    private readonly object _lock = new object();

    /// <summary>
    /// Get next sequence number for the stream. Strictly increasing.
    /// </summary>
    public long GetNextSequence(string streamKey)
    {
        if (string.IsNullOrWhiteSpace(streamKey))
            streamKey = "_default";

        lock (_lock)
        {
            var current = _sequences.AddOrUpdate(streamKey, 1, (_, v) => v + 1);
            return current;
        }
    }

    /// <summary>
    /// Build stream key for per-instrument per-trading-day.
    /// </summary>
    public static string BuildStreamKey(string? tradingDate, string? instrument)
    {
        var td = string.IsNullOrWhiteSpace(tradingDate) ? "_unknown" : tradingDate.Trim();
        var inst = string.IsNullOrWhiteSpace(instrument) ? "_unknown" : instrument.Trim().ToUpperInvariant();
        return $"{td}|{inst}";
    }
}
