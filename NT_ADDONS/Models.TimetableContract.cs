using System;
using System.Collections.Generic;
using System.IO;

namespace QTSW2.Robot.Core;

public sealed class TimetableContract
{
    public string? as_of { get; set; }

    public string session_trading_date { get; set; } = "";

    /// <summary>
    /// DEPRECATED: pre-migration JSON only. TODO(2026-Q4): remove when all artifacts use session_trading_date.
    /// Live RobotEngine must not use this for execution — session_trading_date is required.
    /// </summary>
    public string trading_date { get; set; } = "";

    public string timezone { get; set; } = "";

    public string? source { get; set; }

    /// <summary>Optional publisher version metadata (ignored for arms/eligibility; forward-compatible).</summary>
    public string? timetable_hash { get; set; }

    public string? previous_hash { get; set; }

    public string? version_timestamp { get; set; }

    public TimetableMetadata? metadata { get; set; }

    public List<TimetableStream> streams { get; set; } = new();

    public static TimetableContract LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        var contract = JsonUtil.Deserialize<TimetableContract>(json);
        if (contract == null)
            throw new InvalidOperationException("Failed to parse timetable_current.json");
        return contract;
    }

    public static TimetableContract? LoadFromBytes(byte[] bytes)
    {
        var json = System.Text.Encoding.UTF8.GetString(bytes);
        return JsonUtil.Deserialize<TimetableContract>(json);
    }

    public string GetSessionTradingDateForHashCompatibility()
    {
        if (!string.IsNullOrWhiteSpace(session_trading_date)) return session_trading_date.Trim();
        return (trading_date ?? "").Trim();
    }
}

public sealed class TimetableStream
{
    public string stream { get; set; } = "";

    /// <summary>
    /// Optional in <c>timetable_current.json</c>; derived from <see cref="stream"/> (e.g. ES1 → ES) when empty.
    /// </summary>
    public string instrument { get; set; } = "";

    /// <summary>
    /// Optional in JSON; derived from <see cref="stream"/> (…1 → S1, …2 → S2) when empty.
    /// </summary>
    public string session { get; set; } = "";

    public string slot_time { get; set; } = "";

    public bool enabled { get; set; }

    /// <summary>Legacy field; omitted from current Python publisher JSON (null after deserialize).</summary>
    public bool? matrix_final_allowed { get; set; }

    /// <summary>
    /// Reason why stream is blocked (if enabled = false). Execution contract:
    /// <c>no_valid_execution_slot</c>, <c>session_filter_blocked</c>, or null when armed.
    /// </summary>
    public string? block_reason { get; set; }

    /// <summary>
    /// Optional in JSON; in-memory default aligns with <see cref="slot_time"/> for the sequencer.
    /// </summary>
    public string decision_time { get; set; } = "";

    public static string DeriveSessionFromStreamId(string streamId)
    {
        if (string.IsNullOrWhiteSpace(streamId)) return "";
        var s = streamId.Trim();
        return s.EndsWith("2", StringComparison.Ordinal) ? "S2" : "S1";
    }

    public static string DeriveInstrumentFromStreamId(string streamId)
    {
        if (string.IsNullOrWhiteSpace(streamId)) return "";
        var s = streamId.Trim();
        if (s.Length < 2) return s.ToUpperInvariant();
        var i = s.Length - 1;
        while (i > 0 && char.IsDigit(s[i])) i--;
        var root = s.Substring(0, i + 1).Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(root) ? s.ToUpperInvariant() : root;
    }

    /// <summary>Fills <see cref="session"/>, <see cref="instrument"/>, <see cref="decision_time"/> when omitted in JSON.</summary>
    public static void EnsureExecutionFields(TimetableStream? d)
    {
        if (d == null) return;
        var sid = (d.stream ?? "").Trim();
        if (string.IsNullOrWhiteSpace(sid)) return;
        if (string.IsNullOrWhiteSpace(d.session))
            d.session = DeriveSessionFromStreamId(sid);
        if (string.IsNullOrWhiteSpace(d.instrument))
            d.instrument = DeriveInstrumentFromStreamId(sid);
        if (string.IsNullOrWhiteSpace(d.decision_time))
            d.decision_time = d.slot_time ?? "";
    }
}

public sealed class TimetableMetadata
{
    public bool? replay { get; set; }
}