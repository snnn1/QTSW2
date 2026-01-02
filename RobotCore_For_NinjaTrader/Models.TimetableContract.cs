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
}

public sealed class TimetableMetadata
{
    public bool? Replay { get; set; }
}