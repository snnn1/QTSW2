namespace QTSW2.Robot.Core;

/// <summary>
/// Robot runtime config (configs/robot/robot.json).
/// Defaults: enforce_single_instance_per_market = true, diagnostic_slow_logs = false, diagnostic_logging_during_historical = false, cancel_entry_stops_on_manual_flatten = true.
/// </summary>
public sealed class RobotRuntimeConfig
{
    public bool enforce_single_instance_per_market { get; set; } = true;
    public bool diagnostic_slow_logs { get; set; } = false;
    /// <summary>When true, emit TraceLifecycle, ONBARUPDATE_*, BAR_TIME_DETECTION_*, TICK_TIME_SOURCE during Historical. When false (default), skip to reduce chart lag.</summary>
    public bool diagnostic_logging_during_historical { get; set; } = false;
    /// <summary>When true, cancel entry stops when position goes flat (manual flatten). When false, entry stops are left working.</summary>
    public bool cancel_entry_stops_on_manual_flatten { get; set; } = true;
}
