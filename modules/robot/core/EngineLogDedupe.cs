// Engine-level event deduplication for startup log hardening.
// When multiple strategy instances run (one per instrument), identical engine-level
// events are logged once per instance. This dedupe ensures each event is logged
// at most once per window.
//
// Events: EXECUTION_BLOCKED, RECONCILIATION_QTY_MISMATCH, POSITION_DRIFT_DETECTED, EXPOSURE_INTEGRITY_VIOLATION,
//         RECONCILIATION_ORDER_SOURCE_BREAKDOWN (30s window, key: instrument:broker_working:iea_working)

using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core;

/// <summary>
/// Deduplicates engine-level log events so identical messages within the window appear only once.
/// Key format: event_type:reason (e.g. EXECUTION_BLOCKED:NT_CONTEXT_NOT_SET).
/// RECONCILIATION_ORDER_SOURCE_BREAKDOWN uses instrument:broker_working:iea_working with 30s window.
/// </summary>
internal static class EngineLogDedupe
{
    public const int ENGINE_LOG_DEDUPE_WINDOW_SECONDS = 10;
    /// <summary>Extended window for RECONCILIATION_ORDER_SOURCE_BREAKDOWN (fires every reconciliation pass per instrument).</summary>
    public const int RECONCILIATION_ORDER_SOURCE_BREAKDOWN_WINDOW_SECONDS = 30;

    private static readonly Dictionary<string, DateTimeOffset> _engineLogDedupe = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    private static readonly HashSet<string> DedupeEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "EXECUTION_BLOCKED",
        "RECONCILIATION_QTY_MISMATCH",
        "POSITION_DRIFT_DETECTED",
        "EXPOSURE_INTEGRITY_VIOLATION",
        "RECONCILIATION_ORDER_SOURCE_BREAKDOWN"
    };

    /// <summary>
    /// Returns true if the event should be logged (and records it). Returns false if it was recently logged and should be skipped.
    /// </summary>
    public static bool ShouldLog(string eventType, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(eventType)) return true;
        if (!DedupeEventTypes.Contains(eventType)) return true;

        var key = eventType + ":" + (reason ?? "");
        var now = DateTimeOffset.UtcNow;
        var windowSeconds = string.Equals(eventType, "RECONCILIATION_ORDER_SOURCE_BREAKDOWN", StringComparison.OrdinalIgnoreCase)
            ? RECONCILIATION_ORDER_SOURCE_BREAKDOWN_WINDOW_SECONDS
            : ENGINE_LOG_DEDUPE_WINDOW_SECONDS;

        lock (_lock)
        {
            if (_engineLogDedupe.TryGetValue(key, out var lastLog) &&
                (now - lastLog).TotalSeconds < windowSeconds)
            {
                return false; // Skip - recently logged
            }
            _engineLogDedupe[key] = now;
            PruneStaleEntriesUnsafe(now);
            return true;
        }
    }

    private static void PruneStaleEntriesUnsafe(DateTimeOffset now)
    {
        var toRemove = new List<string>();
        foreach (var kvp in _engineLogDedupe)
        {
            var windowSeconds = kvp.Key.StartsWith("RECONCILIATION_ORDER_SOURCE_BREAKDOWN:", StringComparison.OrdinalIgnoreCase)
                ? RECONCILIATION_ORDER_SOURCE_BREAKDOWN_WINDOW_SECONDS
                : ENGINE_LOG_DEDUPE_WINDOW_SECONDS;
            if ((now - kvp.Value).TotalSeconds >= windowSeconds)
                toRemove.Add(kvp.Key);
        }
        foreach (var k in toRemove)
            _engineLogDedupe.Remove(k);
    }
}
