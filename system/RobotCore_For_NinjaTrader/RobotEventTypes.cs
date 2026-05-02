// SINGLE SOURCE OF TRUTH
// This file is the authoritative registry of all robot event types.
// It is compiled into Robot.Core.dll and should be referenced from that DLL.
// Do not duplicate this file elsewhere - if source is needed, reference Robot.Core.dll instead.
//
// Linked into: Robot.Core.csproj (modules/robot/core/)
// Referenced by: RobotCore_For_NinjaTrader (via Robot.Core.dll)

using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core;

public static partial class RobotEventTypes
{
    /// <summary>
    /// Get the log level for an event type.
    /// Returns "INFO" if event type is unknown (fail-safe).
    /// </summary>
    public static string GetLevel(string eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            return "INFO";
        
        if (_levelMap.TryGetValue(eventType, out var level))
            return level;
        
        // Fallback: use heuristics for unknown events
        var upper = eventType.ToUpperInvariant();
        if (upper.Contains("CRITICAL") || upper.Contains("KILL_SWITCH") || upper.Contains("FLATTEN_FAILED_ALL") ||
            upper.Contains("DUPLICATE_INSTANCE") || upper.Contains("FLATTEN_FAILED"))
            return "CRITICAL";
        if (upper.Contains("ERROR") || upper.Contains("FAIL") || upper.Contains("INVALID") || upper.Contains("VIOLATION"))
            return "ERROR";
        if (upper.Contains("WARN") || upper.Contains("BLOCKED"))
            return "WARN";
        if (upper.Contains("DEBUG") || upper.Contains("DIAGNOSTIC") || upper.Contains("TRACE") || upper.Contains("HEARTBEAT"))
            return "DEBUG";
        
        return "INFO";
    }
    
    /// <summary>
    /// Check if an event type is valid (exists in registry).
    /// </summary>
    public static bool IsValid(string eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            return false;
        
        return _levelMap.ContainsKey(eventType) || _allEvents.Contains(eventType);
    }
    
    /// <summary>
    /// Get all registered event types.
    /// </summary>
    public static IEnumerable<string> GetAllEventTypes()
    {
        return _allEvents;
    }
    
    // Level mapping: event type -> log level
}
