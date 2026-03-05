using System;
using System.Collections.Generic;
using System.IO;

namespace QTSW2.Robot.Core;

public sealed class TimetableContract
{
    public string? as_of { get; set; }

    public string trading_date { get; set; } = "";

    public string timezone { get; set; } = "";

    public string? source { get; set; }

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
}

public sealed class TimetableStream
{
    public string stream { get; set; } = "";

    public string instrument { get; set; } = "";

    public string session { get; set; } = "";

    public string slot_time { get; set; } = "";

    public bool enabled { get; set; }

    /// <summary>
    /// Reason why stream is blocked (if enabled = false).
    /// Examples: "dom_blocked_5", "scf_blocked", "no_rs_data", "not_in_master_matrix"
    /// </summary>
    public string? block_reason { get; set; }

    /// <summary>
    /// Decision time (sequencer intent) - the time slot the sequencer would use if enabled.
    /// Always present even if stream is blocked, representing current sequencer state.
    /// </summary>
    public string decision_time { get; set; } = "";
}

public sealed class TimetableMetadata
{
    public bool? replay { get; set; }
}