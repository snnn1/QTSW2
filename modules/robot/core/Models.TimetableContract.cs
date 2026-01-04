using System;
using System.Collections.Generic;
using System.IO;

namespace QTSW2.Robot.Core;

public sealed class TimetableContract
{
    public string? AsOf { get; set; }

    public string TradingDate { get; set; } = "";

    public string Timezone { get; set; } = "";

    public string? Source { get; set; }

    public TimetableMetadata? Metadata { get; set; }

    public List<TimetableStream> Streams { get; set; } = new();

    public static TimetableContract LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        return JsonUtil.Deserialize<TimetableContract>(json)
               ?? throw new InvalidOperationException("Failed to parse timetable_current.json");
    }
}

public sealed class TimetableStream
{
    public string Stream { get; set; } = "";

    public string Instrument { get; set; } = "";

    public string Session { get; set; } = "";

    public string SlotTime { get; set; } = "";

    public bool Enabled { get; set; }

    /// <summary>
    /// Reason why stream is blocked (if Enabled = false).
    /// Examples: "dom_blocked_5", "scf_blocked", "no_rs_data", "not_in_master_matrix"
    /// </summary>
    public string? BlockReason { get; set; }

    /// <summary>
    /// Decision time (sequencer intent) - the time slot the sequencer would use if enabled.
    /// Always present even if stream is blocked, representing current sequencer state.
    /// </summary>
    public string DecisionTime { get; set; } = "";
}

public sealed class TimetableMetadata
{
    public bool? Replay { get; set; }
}