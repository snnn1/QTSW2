using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core;

/// <summary>
/// Health monitor configuration (loaded from configs/robot/health_monitor.json).
/// Property names use snake_case to match JSON (JavaScriptSerializer convention).
/// Simplified: Only monitors connection loss and data loss (no robot stall detection).
/// Phase 2: Instrument-aware stall thresholds for quant-grade detection.
/// </summary>
public class HealthMonitorConfig
{
    public bool enabled { get; set; }
    /// <summary>Legacy: used when instrument_stall_threshold_seconds has no entry. Kept for backward compatibility.</summary>
    public int data_stall_seconds { get; set; } = 180;
    /// <summary>Per-instrument stall threshold (seconds). Overrides data_stall_seconds when key matches.</summary>
    public Dictionary<string, int>? instrument_stall_threshold_seconds { get; set; }
    /// <summary>Default threshold when instrument not in instrument_stall_threshold_seconds. Default 300.</summary>
    public int default_stall_seconds { get; set; } = 300;

    private static readonly Dictionary<string, int> BuiltInInstrumentThresholds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MNG"] = 420, ["M2K"] = 300, ["MYM"] = 300, ["MES"] = 240, ["MNQ"] = 240,
        ["MCL"] = 300, ["MGC"] = 300, ["NG"] = 420, ["RTY"] = 300, ["YM"] = 300,
        ["ES"] = 240, ["NQ"] = 240, ["CL"] = 300, ["GC"] = 300
    };

    /// <summary>Get effective stall threshold (seconds) for an instrument.</summary>
    public int GetStallThresholdSeconds(string instrument)
    {
        if (instrument_stall_threshold_seconds != null &&
            instrument_stall_threshold_seconds.TryGetValue(instrument, out var configVal))
            return configVal;
        if (BuiltInInstrumentThresholds.TryGetValue(instrument, out var builtIn))
            return builtIn;
        return default_stall_seconds > 0 ? default_stall_seconds : data_stall_seconds;
    }
    
    /// <summary>
    /// DEPRECATED: Robot stall detection removed. This field is kept for backward compatibility but is not used.
    /// </summary>
    [Obsolete("Robot stall detection has been removed. This field is ignored.")]
    public int robot_stall_seconds { get; set; } = 30;
    
    /// <summary>
    /// DEPRECATED: Monitoring windows removed. This field is kept for backward compatibility but is not used.
    /// </summary>
    [Obsolete("Monitoring windows have been removed. This field is ignored.")]
    public int grace_period_seconds { get; set; } = 300;
    
    public int min_notify_interval_seconds { get; set; } = 600;
    public bool pushover_enabled { get; set; }
    public string pushover_user_key { get; set; } = "";
    public string pushover_app_token { get; set; } = "";
    public int? pushover_priority { get; set; }
}
