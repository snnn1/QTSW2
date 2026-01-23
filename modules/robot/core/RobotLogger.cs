using System;
using System.Collections.Generic;
using System.IO;

namespace QTSW2.Robot.Core;

public sealed class RobotLogger
{
    private readonly string _jsonlPath;
    private readonly object _lock = new();
    private DateTime _lastErrorLogTime = DateTime.MinValue;
    private const int ERROR_LOG_INTERVAL_SECONDS = 10;
    private readonly string _instanceId; // Unique instance identifier to prevent shared file writes
    private readonly string _projectRoot;
    private readonly string? _customLogDir;
    private RobotLoggingService? _loggingService; // Optional reference to singleton service for ENGINE logs
    private string? _runId; // Engine run identifier (GUID per engine start)

    public RobotLogger(string projectRoot, string? customLogDir = null, string? instrument = null, RobotLoggingService? loggingService = null)
    {
        _projectRoot = projectRoot;
        _customLogDir = customLogDir;
        _loggingService = loggingService;
        
        var dir = customLogDir ?? Path.Combine(projectRoot, "logs", "robot");
        Directory.CreateDirectory(dir);

        // Generate unique instance ID to prevent shared file writes in fallback mode
        // Note: Using Substring instead of range operator for .NET Framework compatibility
        var guidStr = Guid.NewGuid().ToString("N");
        _instanceId = guidStr.Substring(0, 8);

        // NOTE: customLogDir is a directory override (not "dryrun mode").
        // If the async logging service is unavailable, we fall back to per-instance files
        // in the selected directory to avoid file lock contention.
        if (!string.IsNullOrWhiteSpace(instrument))
        {
            // Per-instrument log file with instance ID: robot_ES_<instance>.jsonl
            var sanitizedInstrument = SanitizeFileName(instrument);
            _jsonlPath = Path.Combine(dir, $"robot_{sanitizedInstrument}_{_instanceId}.jsonl");
        }
        else
        {
            _jsonlPath = Path.Combine(dir, $"robot_skeleton_{_instanceId}.jsonl");
        }
    }

    /// <summary>
    /// Set the current engine run ID (GUID per engine start). This is propagated into every RobotLogEvent.
    /// Must be set before any logs are emitted in RobotEngine.Start().
    /// </summary>
    public void SetRunId(string runId)
    {
        _runId = runId;
    }

    private static string SanitizeFileName(string instrument)
    {
        // Remove invalid filename characters and ensure safe for filesystem
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = instrument;
        foreach (var c in invalidChars)
        {
            sanitized = sanitized.Replace(c, '_');
        }
        return sanitized.ToUpperInvariant();
    }

    public void Write(object evt)
    {
        // CRITICAL RULE: ALL logs SHOULD go through RobotLoggingService singleton for proper routing.
        // - ENGINE events (stream == "__engine__") → robot_ENGINE.jsonl
        // - Execution events (has intent_id) → robot_<instrument>.jsonl (per-instrument)
        // - Stream events → robot_<instrument>.jsonl (per-instrument)
        // 
        // Fallback behavior (when service unavailable or conversion fails):
        // - ENGINE events are DROPPED (never written synchronously to shared files)
        // - Other events fall back to per-instance files (safe, no contention)
        //
        // NOTE: RobotEngine always passes _loggingService in constructor, so fallback is rarely used.
        // This fallback exists as a safety mechanism for edge cases (service unavailable, conversion errors).
        
        // Try to route through async service first (preferred path)
        if (_loggingService == null)
        {
            _loggingService = RobotLoggingService.GetInstance(_projectRoot, _customLogDir);
        }

        if (_loggingService != null)
        {
            try
            {
                // Convert to RobotLogEvent and route through service
                var logEvent = ConvertToRobotLogEvent(evt);
                if (logEvent != null)
                {
                    _loggingService.Log(logEvent);
                    return; // Successfully routed through service
                }
            }
            catch (Exception ex)
            {
                // Fail loudly: log conversion failure as ERROR event before falling back
                try
                {
                    var utcNow = DateTimeOffset.UtcNow;
                    string streamVal = "";
                    string instrumentVal = "";
                    if (evt is Dictionary<string, object?> evtDict)
                    {
                        if (evtDict.TryGetValue("stream", out var s)) streamVal = s?.ToString() ?? "";
                        if (evtDict.TryGetValue("instrument", out var i)) instrumentVal = i?.ToString() ?? "";
                    }
                    var errorEvent = new Dictionary<string, object?>
                    {
                        ["ts_utc"] = utcNow.ToString("o"),
                        ["event_type"] = "LOGGER_CONVERSION_ERROR",
                        ["stream"] = streamVal,
                        ["instrument"] = instrumentVal,
                        ["data"] = new Dictionary<string, object?>
                        {
                            ["payload"] = new Dictionary<string, object?>
                            {
                                ["exception_type"] = ex.GetType().Name,
                                ["error"] = ex.Message,
                                ["stack_trace"] = ex.StackTrace != null && ex.StackTrace.Length > 500 ? ex.StackTrace.Substring(0, 500) : ex.StackTrace,
                                ["note"] = "Failed to convert event to RobotLogEvent - falling back to sync logger"
                            }
                        }
                    };
                    // Replace evt with error event so the fallback path persists it
                    evt = errorEvent;
                }
                catch
                {
                    // If even error event creation fails, silently fall through
                }
            }
        }

        // Check if this is an ENGINE event (for fallback behavior)
        bool isEngineEvent = false;
        if (evt is Dictionary<string, object?> evtDict2)
        {
            if (evtDict2.TryGetValue("stream", out var streamObj) && streamObj is string streamStr && streamStr == "__engine__")
            {
                isEngineEvent = true;
            }
        }

        if (isEngineEvent)
        {
            // Service unavailable - DROP ENGINE log (never write to shared files)
            // This prevents file lock contention when multiple engines run simultaneously
            return;
        }

        // Non-ENGINE events: fallback to per-instance file (safe, no contention)
        // CRITICAL: This fallback should only be used when async logging service is unavailable.
        // Each instance writes to a unique file (via _instanceId) to prevent file lock contention.
        try
        {
            var line = JsonUtil.Serialize(evt);
            lock (_lock)
            {
                File.AppendAllText(_jsonlPath, line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            // Rate-limited error reporting: log to NinjaTrader internal log once per N seconds
            var now = DateTime.UtcNow;
            if ((now - _lastErrorLogTime).TotalSeconds >= ERROR_LOG_INTERVAL_SECONDS)
            {
                _lastErrorLogTime = now;
                // Note: In NinjaTrader context, this would use Log() or Print()
                // For now, we'll write to a separate error log file with instance ID
                try
                {
                    var errorLogPath = Path.Combine(Path.GetDirectoryName(_jsonlPath) ?? "", $"robot_logging_errors_{_instanceId}.txt");
                    var errorMsg = $"[{now:yyyy-MM-dd HH:mm:ss} UTC] [Instance {_instanceId}] Logging error: {ex.Message}\n";
                    File.AppendAllText(errorLogPath, errorMsg);
                }
                catch
                {
                    // Silently fail if we can't even write the error log
                }
            }
            // Do NOT throw - OnBarUpdate must not crash due to logging failures
        }
    }

    /// <summary>
    /// Convert old event dictionary format to RobotLogEvent.
    /// Returns null if conversion fails.
    /// </summary>
    private RobotLogEvent? ConvertToRobotLogEvent(object evt)
    {
        if (!(evt is Dictionary<string, object?> dict))
            return null;

        var utcNow = DateTimeOffset.UtcNow;
        if (dict.TryGetValue("ts_utc", out var tsObj) && tsObj is string tsStr && DateTimeOffset.TryParse(tsStr, out var parsed))
        {
            utcNow = parsed;
        }

        var eventType = dict.TryGetValue("event_type", out var et) ? et?.ToString() ?? "" : "";
        // Use centralized event registry for level assignment (replaces fragile string matching)
        var level = RobotEventTypes.GetLevel(eventType);

        var source = "RobotEngine";
        if (dict.TryGetValue("stream", out var streamObj) && streamObj is string streamStr)
        {
            if (streamStr == "__engine__")
                source = "RobotEngine";
            else if (streamStr.StartsWith("EXECUTION") || dict.ContainsKey("intent_id"))
                source = "ExecutionAdapter";
            else
                source = "StreamStateMachine";
        }

        var instrument = dict.TryGetValue("instrument", out var inst) ? inst?.ToString() ?? "" : "";
        var message = eventType;
        
        // Extract data payload
        var data = new Dictionary<string, object?>();
        if (dict.TryGetValue("data", out var dataObj))
        {
            if (dataObj is Dictionary<string, object?> dataDict)
            {
                foreach (var kvp in dataDict)
                    data[kvp.Key] = kvp.Value;
            }
            else if (dataObj != null)
            {
                data["payload"] = dataObj;
            }
        }

        // Include additional context fields in data
        foreach (var kvp in dict)
        {
            if (kvp.Key != "ts_utc" && kvp.Key != "ts_chicago" && kvp.Key != "event_type" && 
                kvp.Key != "instrument" && kvp.Key != "data" && kvp.Key != "stream")
            {
                data[kvp.Key] = kvp.Value;
            }
        }

        return new RobotLogEvent(utcNow, level, source, instrument, eventType, message, runId: _runId, data: data.Count > 0 ? data : null);
    }
}

public static class RobotEvents
{
    public static object EngineBase(
        DateTimeOffset utcNow,
        string tradingDate,
        string eventType,
        string state,
        object? extra = null
    )
    {
        // Engine events still need the full base schema; use Chicago conversion independent of spec.
        var chicagoNow = ConvertUtcToChicago(utcNow);
        return new Dictionary<string, object?>
        {
            ["ts_utc"] = utcNow.ToString("o"),
            ["ts_chicago"] = chicagoNow.ToString("o"),
            ["trading_date"] = tradingDate,
            ["stream"] = "__engine__",
            ["instrument"] = "",
            ["session"] = "",
            ["slot_time_chicago"] = "",
            ["slot_time_utc"] = null,
            ["event_type"] = eventType,
            ["state"] = state,
            ["data"] = extra
        };
    }

    public static object Base(
        TimeService time,
        DateTimeOffset utcNow,
        string tradingDate,
        string stream,
        string instrument,
        string session,
        string slotTimeChicago,
        DateTimeOffset? slotTimeUtc,
        string eventType,
        string state,
        object? extra = null
    )
    {
        var chicagoNow = time.ConvertUtcToChicago(utcNow);
        return new Dictionary<string, object?>
        {
            ["ts_utc"] = utcNow.ToString("o"),
            ["ts_chicago"] = chicagoNow.ToString("o"),
            ["trading_date"] = tradingDate,
            ["stream"] = stream,
            ["instrument"] = instrument,
            ["session"] = session,
            ["slot_time_chicago"] = slotTimeChicago,
            ["slot_time_utc"] = slotTimeUtc?.ToString("o"),
            ["event_type"] = eventType,
            ["state"] = state,
            ["data"] = extra
        };
    }

    public static object ExecutionBase(
        DateTimeOffset utcNow,
        string intentId,
        string instrument,
        string eventType,
        object? extra = null
    )
    {
        var chicagoNow = ConvertUtcToChicago(utcNow);
        return new Dictionary<string, object?>
        {
            ["ts_utc"] = utcNow.ToString("o"),
            ["ts_chicago"] = chicagoNow.ToString("o"),
            ["intent_id"] = intentId,
            ["instrument"] = instrument,
            ["event_type"] = eventType,
            ["data"] = extra
        };
    }

    // Note: Timezone conversion now delegates to TimeService to avoid duplication.
    // RobotEvents.EngineBase() and ExecutionBase() use this static helper when TimeService instance is not available.
    private static DateTimeOffset ConvertUtcToChicago(DateTimeOffset utcNow)
    {
        return TimeService.ConvertUtcToChicagoStatic(utcNow);
    }
}

