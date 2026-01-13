using System;
using System.IO;

namespace QTSW2.Robot.Core;

public sealed class LoggingConfig
{
    public int max_file_size_mb { get; set; } = 50;
    public int max_rotated_files { get; set; } = 5;
    public string min_log_level { get; set; } = "INFO";
    public bool enable_diagnostic_logs { get; set; } = false;
    public DiagnosticRateLimits? diagnostic_rate_limits { get; set; }
    public int archive_days { get; set; } = 7;

    public static LoggingConfig LoadFromFile(string projectRoot)
    {
        var configPath = Path.Combine(projectRoot, "configs", "robot", "logging.json");
        if (!File.Exists(configPath))
        {
            // Return defaults if config doesn't exist
            return new LoggingConfig();
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var config = JsonUtil.Deserialize<LoggingConfig>(json);
            return config ?? new LoggingConfig();
        }
        catch
        {
            // Return defaults on error (fail-open)
            return new LoggingConfig();
        }
    }
}

public sealed class DiagnosticRateLimits
{
    public int tick_heartbeat_minutes { get; set; } = 5;
    public int bar_diagnostic_seconds { get; set; } = 300;
    public int slot_gate_diagnostic_seconds { get; set; } = 60;
}
