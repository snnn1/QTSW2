using System;
using System.IO;

namespace QTSW2.Robot.Core;

public sealed class LoggingConfig
{
    /// <summary>
    /// Optional override for the log directory.
    /// - Absolute paths are used as-is
    /// - Relative paths are resolved relative to project root
    /// If not set, defaults to &lt;projectRoot&gt;\\logs\\robot.
    /// </summary>
    public string? log_dir { get; set; }

    public int max_file_size_mb { get; set; } = 50;
    public int max_rotated_files { get; set; } = 5;
    public string min_log_level { get; set; } = "INFO";
    public bool enable_diagnostic_logs { get; set; } = false;
    public DiagnosticRateLimits? diagnostic_rate_limits { get; set; }
    public int archive_days { get; set; } = 7;
    
    /// <summary>
    /// Maximum queue size before backpressure kicks in (default: 50000).
    /// Events are dropped when queue exceeds this size (DEBUG first, then INFO).
    /// </summary>
    public int max_queue_size { get; set; } = 50000;
    
    /// <summary>
    /// Maximum batch size per flush (default: 2000).
    /// Background worker processes up to this many events per flush cycle.
    /// </summary>
    public int max_batch_per_flush { get; set; } = 2000;
    
    /// <summary>
    /// Flush interval in milliseconds (default: 500).
    /// Background worker flushes queue at this interval.
    /// </summary>
    public int flush_interval_ms { get; set; } = 500;
    
    /// <summary>
    /// Enable daily rotation regardless of file size (default: false).
    /// When true, log files are rotated at midnight UTC even if under size limit.
    /// </summary>
    public bool rotate_daily { get; set; } = false;
    
    /// <summary>
    /// Enable sensitive data filtering (default: true).
    /// When true, attempts to redact sensitive patterns (API keys, tokens, etc.) from log data.
    /// </summary>
    public bool enable_sensitive_data_filter { get; set; } = true;
    
    /// <summary>
    /// Archive cleanup: delete archives older than this many days (default: 30).
    /// Set to 0 to disable automatic cleanup.
    /// </summary>
    public int archive_cleanup_days { get; set; } = 30;

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
