using System;

namespace QTSW2.Robot.Core;

/// <summary>
/// Health monitor configuration (loaded from configs/robot/health_monitor.json).
/// Property names use snake_case to match JSON (JavaScriptSerializer convention).
/// </summary>
public class HealthMonitorConfig
{
    public bool enabled { get; set; }
    public int data_stall_seconds { get; set; } = 180;
    public int robot_stall_seconds { get; set; } = 30;
    public int grace_period_seconds { get; set; } = 300;
    public int min_notify_interval_seconds { get; set; } = 600;
    public bool pushover_enabled { get; set; }
    public string pushover_user_key { get; set; } = "";
    public string pushover_app_token { get; set; } = "";
    public int? pushover_priority { get; set; }
}
