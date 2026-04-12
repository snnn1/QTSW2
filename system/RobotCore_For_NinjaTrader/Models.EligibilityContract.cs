using System;
using System.Collections.Generic;
using System.IO;

namespace QTSW2.Robot.Core;

/// <summary>
/// Session Eligibility Freeze - immutable for the session_trading_date (CME).
/// Written once per session at 18:00 CT. Robot refuses to trade without it (fail-closed).
/// </summary>
public sealed class EligibilityContract
{
    public string session_trading_date { get; set; } = "";

    /// <summary>
    /// DEPRECATED: pre-migration JSON only. TODO(2026-Q4): remove. Robot execution uses session_trading_date only.
    /// </summary>
    public string trading_date { get; set; } = "";

    public string freeze_time_utc { get; set; } = "";

    public string? source_matrix_hash { get; set; }

    public List<EligibleStream> eligible_streams { get; set; } = new();

    public static EligibilityContract? LoadFromFile(string path)
    {
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);
        return JsonUtil.Deserialize<EligibilityContract>(json);
    }

    public static EligibilityContract? LoadFromBytes(byte[] bytes)
    {
        var json = System.Text.Encoding.UTF8.GetString(bytes);
        return JsonUtil.Deserialize<EligibilityContract>(json);
    }

    public HashSet<string> GetEligibleStreamSet() => GetEligibleSet(this);

    public static HashSet<string> GetEligibleSet(EligibilityContract? contract)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (contract?.eligible_streams == null) return set;

        foreach (var s in contract.eligible_streams)
        {
            if (s.enabled && !string.IsNullOrWhiteSpace(s.stream_key))
                set.Add(s.stream_key.Trim());
        }
        return set;
    }

    public string GetSessionTradingDateForHashCompatibility()
    {
        if (!string.IsNullOrWhiteSpace(session_trading_date)) return session_trading_date.Trim();
        return (trading_date ?? "").Trim();
    }
}

public sealed class EligibleStream
{
    public string stream_key { get; set; } = "";

    public bool enabled { get; set; }

    public string? reason { get; set; }
}
