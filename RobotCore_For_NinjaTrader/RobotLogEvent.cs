// SINGLE SOURCE OF TRUTH
// This file is the authoritative implementation of RobotLogEvent.
// It is compiled into Robot.Core.dll and should be referenced from that DLL.
// Do not duplicate this file elsewhere - if source is needed, reference Robot.Core.dll instead.
//
// Linked into: Robot.Core.csproj (modules/robot/core/)
// Referenced by: RobotCore_For_NinjaTrader (via Robot.Core.dll)

using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core;

/// <summary>
/// Strict, stable schema for robot log events.
/// One JSON object per line - no multi-line formatting.
/// </summary>
public sealed class RobotLogEvent
{
    /// <summary>ISO8601 UTC timestamp string</summary>
    public string ts_utc { get; set; } = "";

    /// <summary>Log level: DEBUG, INFO, WARN, ERROR</summary>
    public string level { get; set; } = "INFO";

    /// <summary>Source component name: RobotEngine, NinjaTraderAdapter, StrategyHost, etc.</summary>
    public string source { get; set; } = "";

    /// <summary>Instrument identifier: ES, NQ, CL, GC, etc.</summary>
    public string instrument { get; set; } = "";

    /// <summary>Trading date (YYYY-MM-DD format)</summary>
    public string? trading_date { get; set; }

    /// <summary>Stream identifier</summary>
    public string? stream { get; set; }

    /// <summary>Session identifier</summary>
    public string? session { get; set; }

    /// <summary>Slot time in Chicago timezone</summary>
    public string? slot_time_chicago { get; set; }

    /// <summary>Account name if available</summary>
    public string? account { get; set; }

    /// <summary>Current run ID if available</summary>
    public string? run_id { get; set; }

    /// <summary>Short event name/type</summary>
    public string @event { get; set; } = "";

    /// <summary>Human-readable message</summary>
    public string message { get; set; } = "";

    /// <summary>Optional structured payload</summary>
    public Dictionary<string, object?>? data { get; set; }

    public RobotLogEvent()
    {
    }

    public RobotLogEvent(
        DateTimeOffset utcNow,
        string level,
        string source,
        string instrument,
        string eventType,
        string message,
        string? account = null,
        string? runId = null,
        Dictionary<string, object?>? data = null)
    {
        ts_utc = utcNow.ToString("o");
        this.level = level;
        this.source = source;
        this.instrument = instrument ?? "";
        this.account = account;
        this.run_id = runId;
        this.@event = eventType;
        this.message = message;
        this.data = data;
    }
}
