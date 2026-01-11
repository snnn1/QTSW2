using System;

namespace QTSW2.Robot.Core;

/// <summary>
/// Health monitor configuration (loaded from configs/robot/health_monitor.json).
/// </summary>
public class HealthMonitorConfig
{
    public bool Enabled { get; set; }
    public int DataStallSeconds { get; set; } = 180;
    public int RobotStallSeconds { get; set; } = 30;
    public int GracePeriodSeconds { get; set; } = 300;
    public int MinNotifyIntervalSeconds { get; set; } = 600;
    public bool PushoverEnabled { get; set; }
    public string PushoverUserKey { get; set; } = "";
    public string PushoverAppToken { get; set; } = "";
    public int? PushoverPriority { get; set; }
}
