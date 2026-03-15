// Engine-level event deduplication for startup log hardening.
// When multiple strategy instances run (one per instrument), identical engine-level
// events are logged once per instance. This dedupe ensures each event is logged
// at most once per ENGINE_LOG_DEDUPE_WINDOW_SECONDS.
//
// Events: EXECUTION_BLOCKED, RECONCILIATION_QTY_MISMATCH, POSITION_DRIFT_DETECTED, EXPOSURE_INTEGRITY_VIOLATION

using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core;

/// <summary>
/// Deduplicates engine-level log events so identical messages within the window appear only once.
/// Key format: event_type:reason (e.g. EXECUTION_BLOCKED:NT_CONTEXT_NOT_SET).
/// </summary>
internal static class EngineLogDedupe
{
    public const int ENGINE_LOG_DEDUPE_WINDOW_SECONDS = 10;

    private static readonly Dictionary<string, DateTimeOffset> _engineLogDedupe = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    private static readonly HashSet<string> DedupeEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "EXECUTION_BLOCKED",
        "RECONCILIATION_QTY_MISMATCH",
        "POSITION_DRIFT_DETECTED",
        "EXPOSURE_INTEGRITY_VIOLATION"
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

        lock (_lock)
        {
            if (_engineLogDedupe.TryGetValue(key, out var lastLog) &&
                (now - lastLog).TotalSeconds < ENGINE_LOG_DEDUPE_WINDOW_SECONDS)
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
            if ((now - kvp.Value).TotalSeconds >= ENGINE_LOG_DEDUPE_WINDOW_SECONDS)
                toRemove.Add(kvp.Key);
        }
        foreach (var k in toRemove)
            _engineLogDedupe.Remove(k);
    }
}
