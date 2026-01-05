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
        return JsonUtil.Deserialize<TimetableContract>(json)
               ?? throw new InvalidOperationException("Failed to parse timetable_current.json");
    }
}

public sealed class TimetableStream
{
    public string stream { get; set; } = "";

    public string instrument { get; set; } = "";

    public string session { get; set; } = "";

    public string slot_time { get; set; } = "";

    public bool enabled { get; set; }
}

public sealed class TimetableMetadata
{
    public bool? replay { get; set; }
}