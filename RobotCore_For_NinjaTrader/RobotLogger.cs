using System;
using System.Collections.Generic;
using System.IO;

namespace QTSW2.Robot.Core;

public sealed class RobotLogger
{
    private readonly string _jsonlPath;
    private readonly object _lock = new();

    public RobotLogger(string projectRoot, string? customLogDir = null)
    {
        if (customLogDir != null)
        {
            Directory.CreateDirectory(customLogDir);
            _jsonlPath = Path.Combine(customLogDir, "robot_dryrun.jsonl");
        }
        else
        {
            var dir = Path.Combine(projectRoot, "logs", "robot");
            Directory.CreateDirectory(dir);
            _jsonlPath = Path.Combine(dir, "robot_skeleton.jsonl");
        }
    }

    public void Write(object evt)
    {
        var line = JsonUtil.Serialize(evt);
        lock (_lock)
        {
            File.AppendAllText(_jsonlPath, line + Environment.NewLine);
        }
    }
}

public static class RobotEvents
{
    public static object EngineBase(
        DateTimeOffset utcNow,
        string tradingDate,
        string eventType,
        string state,
        object? extra = null
    )
    {
        // Engine events still need the full base schema; use Chicago conversion independent of spec.
        var chicagoNow = ConvertUtcToChicago(utcNow);
        return new Dictionary<string, object?>
        {
            ["ts_utc"] = utcNow.ToString("o"),
            ["ts_chicago"] = chicagoNow.ToString("o"),
            ["trading_date"] = tradingDate,
            ["stream"] = "__engine__",
            ["instrument"] = "",
            ["session"] = "",
            ["slot_time_chicago"] = "",
            ["slot_time_utc"] = null,
            ["event_type"] = eventType,
            ["state"] = state,
            ["data"] = extra
        };
    }

    public static object Base(
        TimeService time,
        DateTimeOffset utcNow,
        string tradingDate,
        string stream,
        string instrument,
        string session,
        string slotTimeChicago,
        DateTimeOffset? slotTimeUtc,
        string eventType,
        string state,
        object? extra = null
    )
    {
        var chicagoNow = time.GetChicagoNow(utcNow);
        return new Dictionary<string, object?>
        {
            ["ts_utc"] = utcNow.ToString("o"),
            ["ts_chicago"] = chicagoNow.ToString("o"),
            ["trading_date"] = tradingDate,
            ["stream"] = stream,
            ["instrument"] = instrument,
            ["session"] = session,
            ["slot_time_chicago"] = slotTimeChicago,
            ["slot_time_utc"] = slotTimeUtc?.ToString("o"),
            ["event_type"] = eventType,
            ["state"] = state,
            ["data"] = extra
        };
    }

    public static object ExecutionBase(
        DateTimeOffset utcNow,
        string intentId,
        string instrument,
        string eventType,
        object? extra = null
    )
    {
        var chicagoNow = ConvertUtcToChicago(utcNow);
        return new Dictionary<string, object?>
        {
            ["ts_utc"] = utcNow.ToString("o"),
            ["ts_chicago"] = chicagoNow.ToString("o"),
            ["intent_id"] = intentId,
            ["instrument"] = instrument,
            ["event_type"] = eventType,
            ["data"] = extra
        };
    }

    private static DateTimeOffset ConvertUtcToChicago(DateTimeOffset utcNow)
    {
        var tz = ResolveChicagoTimeZone();
        return TimeZoneInfo.ConvertTime(utcNow, tz);
    }

    private static TimeZoneInfo ResolveChicagoTimeZone()
    {
        var candidates = new[] { "America/Chicago", "Central Standard Time" };
        foreach (var id in candidates)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* try next */ }
        }
        throw new TimeZoneNotFoundException("Could not resolve Chicago timezone (America/Chicago / Central Standard Time).");
    }
}

