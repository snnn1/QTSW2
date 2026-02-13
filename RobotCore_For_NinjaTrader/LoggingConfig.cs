using System;
using System.Collections.Generic;
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
    
    /// <summary>
    /// Log profile: "PRODUCTION" (default) or "DIAGNOSTIC".
    /// When PRODUCTION: DEBUG events dropped, stream/bar diagnostics disabled.
    /// When DIAGNOSTIC: DEBUG events permitted, stream status snapshots, bar diagnostics, risk gate logs.
    /// If not set, falls back to enable_diagnostic_logs || diagnostics_enabled for backward compatibility.
    /// </summary>
    public string? log_profile { get; set; }
    
    /// <summary>
    /// [DEPRECATED] Use log_profile = "DIAGNOSTIC" instead.
    /// Kept for backward compatibility; ignored when log_profile is set.
    /// </summary>
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
    
    /// <summary>
    /// [DEPRECATED] Use log_profile = "DIAGNOSTIC" instead.
    /// Kept for backward compatibility; ignored when log_profile is set.
    /// </summary>
    public bool diagnostics_enabled { get; set; } = false;
    
    /// <summary>
    /// Maximum DEBUG events per minute when diagnostics enabled (default: 600).
    /// Prevents DEBUG volume from starving INFO. 0 = no cap.
    /// </summary>
    public int debug_volume_cap_per_minute { get; set; } = 600;
    
    /// <summary>
    /// Event rate limits: max occurrences per minute per event type.
    /// Example: { "BAR_ACCEPTED": 60, "SLOT_GATE_DIAGNOSTIC": 1 }
    /// Rate limits apply ONLY to DEBUG and INFO levels.
    /// ERROR and CRITICAL events always bypass rate limits.
    /// </summary>
    public Dictionary<string, int>? event_rate_limits { get; set; }
    
    /// <summary>
    /// Enable health sink (default: true).
    /// When enabled, events >= WARN + selected INFO events are written to logs/health/ directory.
    /// </summary>
    public bool enable_health_sink { get; set; } = true;

    /// <summary>
    /// True when diagnostics are enabled (stream/bar diagnostics, DEBUG events, risk gate logs).
    /// Uses log_profile if set; otherwise enable_diagnostic_logs || diagnostics_enabled.
    /// </summary>
    public bool DiagnosticsEnabled =>
        !string.IsNullOrWhiteSpace(log_profile)
            ? string.Equals(log_profile.Trim(), "DIAGNOSTIC", StringComparison.OrdinalIgnoreCase)
            : (enable_diagnostic_logs || diagnostics_enabled);

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
