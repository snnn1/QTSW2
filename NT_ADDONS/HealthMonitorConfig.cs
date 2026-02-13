using System;

namespace QTSW2.Robot.Core;

/// <summary>
/// Health monitor configuration (loaded from configs/robot/health_monitor.json).
/// Property names use snake_case to match JSON (JavaScriptSerializer convention).
/// Simplified: Only monitors connection loss and data loss (no robot stall detection).
/// </summary>
public class HealthMonitorConfig
{
    public bool enabled { get; set; }
    public int data_stall_seconds { get; set; } = 180;
    
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
