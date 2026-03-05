using System;
using System.Collections.Generic;
using System.IO;

namespace QTSW2.Robot.Core;

/// <summary>
/// Session Eligibility Freeze - immutable for the trading_date.
/// Written once per trading_date at 18:00 CT. Robot refuses to trade without it (fail-closed).
/// </summary>
public sealed class EligibilityContract
{
    /// <summary>Trading date (Chicago session convention, YYYY-MM-DD).</summary>
    public string trading_date { get; set; } = "";

    /// <summary>UTC timestamp when eligibility was frozen.</summary>
    public string freeze_time_utc { get; set; } = "";

    /// <summary>Optional: hash of source matrix used to compute eligibility.</summary>
    public string? source_matrix_hash { get; set; }

    /// <summary>List of streams with enabled status and optional block reason.</summary>
    public List<EligibleStream> eligible_streams { get; set; } = new();

    /// <summary>Load eligibility from file. Returns null if file does not exist.</summary>
    public static EligibilityContract? LoadFromFile(string path)
    {
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);
        return JsonUtil.Deserialize<EligibilityContract>(json);
    }

    /// <summary>Load eligibility from bytes (for EligibilityCache).</summary>
    public static EligibilityContract? LoadFromBytes(byte[] bytes)
    {
        var json = System.Text.Encoding.UTF8.GetString(bytes);
        return JsonUtil.Deserialize<EligibilityContract>(json);
    }

    /// <summary>Get set of stream keys that are enabled for this contract.</summary>
    public HashSet<string> GetEligibleStreamSet()
    {
        return GetEligibleSet(this);
    }

    /// <summary>Get set of stream keys that are enabled. Empty if contract is null.</summary>
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
}

public sealed class EligibleStream
{
    /// <summary>Stream identifier (e.g. ES1, NQ2, NG2).</summary>
    public string stream_key { get; set; } = "";

    /// <summary>Whether stream may trade this session.</summary>
    public bool enabled { get; set; }

    /// <summary>Reason if disabled (e.g. dom_blocked_5, MATRIX_DATE_MISSING).</summary>
    public string? reason { get; set; }
}
