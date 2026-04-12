// SINGLE SOURCE OF TRUTH
// Emergency fallback logger: Layer 1 of two-layer logging architecture.
// Must NEVER throw. Uses minimal serialization. Guarantees ENGINE events are never dropped.
//
// Layer 1: EmergencyLogger (this) - always writes to robot_ENGINE_fallback.jsonl
// Layer 2: RobotLoggingService - full async structured logger
//
// When RobotLoggingService is unavailable or conversion fails, ENGINE events
// are written here instead of being silently dropped.

using System;
using System.Collections.Generic;
using System.IO;

namespace QTSW2.Robot.Core;

/// <summary>
/// Emergency fallback logger. Never throws. Minimal code path.
/// Writes to robot_ENGINE_fallback.jsonl when the primary logging path fails.
/// </summary>
public static class EmergencyLogger
{
    private static readonly object _lock = new();

    /// <summary>
    /// Write an ENGINE event to the emergency fallback file under the active <c>logs/robot</c> directory (same tree as <see cref="RobotLoggingService"/>).
    /// Called when RobotLoggingService is unavailable or conversion fails.
    /// Never throws.
    /// </summary>
    public static void WriteEngineFallback(string robotLogDir, object evt)
    {
        try
        {
            var dir = robotLogDir;
            var path = Path.Combine(dir, "robot_ENGINE_fallback.jsonl");

            string line;
            try
            {
                line = JsonUtil.Serialize(evt) + Environment.NewLine;
            }
            catch
            {
                // Minimal format when JsonUtil fails - manual string building only
                var ts = DateTimeOffset.UtcNow.ToString("o");
                var et = "UNKNOWN";
                if (evt is Dictionary<string, object?> d && d.TryGetValue("event_type", out var x))
                    et = x?.ToString() ?? "UNKNOWN";
                line = $"{{\"ts_utc\":\"{Escape(ts)}\",\"event_type\":\"{Escape(et)}\",\"emergency\":\"serialize_failed\"}}{Environment.NewLine}";
            }

            lock (_lock)
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Never throw - last resort: give up silently
        }
    }

    /// <summary>Backward-compatible overload: resolves <c>logs/robot</c> from project root and optional override.</summary>
    public static void WriteEngineFallback(string projectRoot, string? customLogDir, object evt)
    {
        var dir = customLogDir ?? Path.Combine(projectRoot, "logs", "robot");
        WriteEngineFallback(dir, evt);
    }

    /// <summary>
    /// Write a critical event (e.g. LOGGING_CONFIG_LOAD_FAILED) when the main logger is unavailable.
    /// Never throws.
    /// </summary>
    /// <param name="robotLogDir">Absolute <c>logs/robot</c> directory (run-scoped when engine uses <c>_persistenceBase</c>).</param>
    public static void WriteConfigFailure(string robotLogDir, string eventType, string configPath, string error)
    {
        try
        {
            var dir = robotLogDir;
            var path = Path.Combine(dir, "robot_ENGINE_fallback.jsonl");
            var ts = DateTimeOffset.UtcNow.ToString("o");
            var line = $"{{\"ts_utc\":\"{Escape(ts)}\",\"event_type\":\"{Escape(eventType)}\",\"config_path\":\"{Escape(configPath)}\",\"error\":\"{Escape(error)}\"}}{Environment.NewLine}";

            lock (_lock)
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Never throw
        }
    }

    private static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
    }
}
