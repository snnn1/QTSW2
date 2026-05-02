// SINGLE SOURCE OF TRUTH
// This file is the authoritative implementation of RobotLoggingService.
// It is compiled into Robot.Core.dll and should be referenced from that DLL.
// Do not duplicate this file elsewhere - if source is needed, reference Robot.Core.dll instead.
//
// Linked into: Robot.Core.csproj (modules/robot/core/)
// Referenced by: RobotCore_For_NinjaTrader (via Robot.Core.dll)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QTSW2.Robot.Core;

public sealed partial class RobotLoggingService
{
    private static readonly HashSet<string> HEALTH_SINK_INFO_EVENTS = new(StringComparer.OrdinalIgnoreCase)
    {
        "TRADE_COMPLETED",
        "CRITICAL_EVENT_REPORTED"  // Critical events timeline for forensic analysis
        // Note: ENGINE_HEARTBEAT not included as it doesn't exist in current event registry
    };

    /// <summary>
    /// Non-blocking enqueue. Never throws from OnBarUpdate.
    /// </summary>
    public void Log(RobotLogEvent evt)
    {
        if (_disposed) return;

        // CRITICAL: ERROR and CRITICAL events always bypass all filtering
        bool isErrorOrCritical = string.Equals(evt.level, "ERROR", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(evt.level, "CRITICAL", StringComparison.OrdinalIgnoreCase);
        
        if (!isErrorOrCritical)
        {
            // Diagnostics filtering: drop DEBUG events if diagnostics disabled
            if (!_config.diagnostics_enabled && string.Equals(evt.level, "DEBUG", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            
            // Rate limiting: apply to DEBUG, INFO, and WARN when event type is in event_rate_limits
            // (CRITICAL_NOTIFICATION_SKIPPED is WARN but floods when multi-instance; throttle via config)
            var eventTypeForRateLimit = evt.@event ?? "";
            var isRateLimitableLevel = string.Equals(evt.level, "DEBUG", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(evt.level, "INFO", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(evt.level, "WARN", StringComparison.OrdinalIgnoreCase) &&
                 _config.event_rate_limits != null && _config.event_rate_limits.ContainsKey(eventTypeForRateLimit));
            if (isRateLimitableLevel)
            {
                if (!CheckRateLimit(evt))
                {
                    return; // Rate limit exceeded, drop event
                }
            }
        }

        // Log level filtering: drop events below minimum level
        if (!ShouldLog(evt.level))
        {
            return;
        }
        
        // Sensitive data filtering (if enabled)
        if (_config.enable_sensitive_data_filter && evt.data != null)
        {
            evt.data = SensitiveDataFilter.FilterDictionary(evt.data);
        }

        // Engine-level event deduplication: log once per engine state change, not once per instrument
        var eventType = evt.@event ?? "";
        var reason = BuildDedupeReason(eventType, evt.data);
        if (!EngineLogDedupe.ShouldLog(eventType, reason))
            return;

        // Backpressure: if queue exceeds max size, drop DEBUG first, then INFO
        if (_queue.Count >= MAX_QUEUE_SIZE)
        {
            if (evt.level == "DEBUG")
            {
                Interlocked.Increment(ref _droppedDebugCount);
                // Emit rate-limited ERROR event for backpressure drops
                EmitBackpressureEvent("DEBUG");
                return;
            }
            if (evt.level == "INFO")
            {
                Interlocked.Increment(ref _droppedInfoCount);
                // Emit rate-limited ERROR event for backpressure drops
                EmitBackpressureEvent("INFO");
                return;
            }
            // WARN, ERROR, and CRITICAL are never dropped
        }

        _queue.Enqueue(evt);
    }
    
    /// <summary>
    /// Build dedupe key reason for EngineLogDedupe.
    /// RECONCILIATION_ORDER_SOURCE_BREAKDOWN uses instrument:broker_working:iea_working for per-instrument dedupe.
    /// </summary>
    private static string? BuildDedupeReason(string eventType, Dictionary<string, object?>? data)
    {
        if (data == null) return null;
        if (string.Equals(eventType, "RECONCILIATION_ORDER_SOURCE_BREAKDOWN", StringComparison.OrdinalIgnoreCase))
        {
            var inst = data.TryGetValue("instrument", out var i) ? i?.ToString() ?? "" : "";
            var bw = data.TryGetValue("broker_working", out var b) ? b?.ToString() ?? "" : "";
            var iw = data.TryGetValue("iea_working", out var ie) ? ie?.ToString() ?? "" : "";
            return $"{inst}:{bw}:{iw}";
        }
        return data.TryGetValue("reason", out var reasonObj) ? reasonObj?.ToString() : null;
    }

    /// <summary>
    /// Check rate limit for event. Returns true if event should be logged, false if rate limit exceeded.
    /// Rate limits apply ONLY to DEBUG and INFO levels. ERROR and CRITICAL always bypass.
    /// </summary>
    private bool CheckRateLimit(RobotLogEvent evt)
    {
        if (_config.event_rate_limits == null || _config.event_rate_limits.Count == 0)
        {
            return true; // No rate limits configured
        }
        
        var eventType = evt.@event ?? "";
        if (string.IsNullOrWhiteSpace(eventType))
        {
            return true; // No event type, can't rate limit
        }
        
        if (!_config.event_rate_limits.TryGetValue(eventType, out int maxPerMinute))
        {
            return true; // No rate limit for this event type
        }
        
        var now = DateTimeOffset.UtcNow;
        
        lock (_rateLimitLock)
        {
            // Reset counters every minute
            if ((now - _lastRateLimitResetUtc).TotalMinutes >= 1.0)
            {
                _eventCountsPerMinute.Clear();
                _lastRateLimitResetUtc = now;
            }
            
            // Get current count for this event type
            if (!_eventCountsPerMinute.TryGetValue(eventType, out int currentCount))
            {
                currentCount = 0;
            }
            
            // Check if rate limit exceeded
            if (currentCount >= maxPerMinute)
            {
                return false; // Rate limit exceeded
            }
            
            // Increment count
            _eventCountsPerMinute[eventType] = currentCount + 1;
            return true;
        }
    }

    /// <summary>
    /// Check if event should be logged based on minimum log level.
    /// </summary>
    private bool ShouldLog(string level)
    {
        return _minLogLevel switch
        {
            "DEBUG" => true, // DEBUG level logs everything
            "INFO" => level == "INFO" || level == "WARN" || level == "ERROR" || level == "CRITICAL",
            "WARN" => level == "WARN" || level == "ERROR" || level == "CRITICAL",
            "ERROR" => level == "ERROR" || level == "CRITICAL", // ERROR level logs errors and criticals
            _ => true // Default: log everything if level is unknown
        };
    }

    /// <summary>
    /// Synchronously drains queued log events before shutdown summary classification reads JSONL artifacts.
    /// </summary>
}
