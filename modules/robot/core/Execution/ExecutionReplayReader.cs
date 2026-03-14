// Gap 5: Read canonical execution events from JSONL stream for replay.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Reads canonical execution events from a JSONL stream in order.
/// </summary>
public static class ExecutionReplayReader
{
    /// <summary>
    /// Read events from a stream path. Yields events in file order.
    /// </summary>
    public static IEnumerable<CanonicalExecutionEvent> ReadEvents(string streamPath)
    {
        if (string.IsNullOrWhiteSpace(streamPath) || !File.Exists(streamPath))
            yield break;

        using var reader = new StreamReader(streamPath);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var evt = ParseEvent(line);
            if (evt != null)
                yield return evt;
        }
    }

    /// <summary>
    /// Resolve stream path for instrument and trading date.
    /// </summary>
    public static string GetStreamPath(string projectRoot, string tradingDate, string instrument)
    {
        var td = string.IsNullOrWhiteSpace(tradingDate) ? DateTimeOffset.UtcNow.ToString("yyyy-MM-dd") : tradingDate.Trim();
        var inst = string.IsNullOrWhiteSpace(instrument) ? "_unknown" : SanitizeFileName(instrument);
        return Path.Combine(projectRoot, "automation", "logs", "execution_events", td, inst + ".jsonl");
    }

    /// <summary>
    /// Read events for instrument and trading date.
    /// </summary>
    public static IEnumerable<CanonicalExecutionEvent> ReadEventsForInstrument(string projectRoot, string tradingDate, string instrument)
    {
        var path = GetStreamPath(projectRoot, tradingDate, instrument);
        return ReadEvents(path);
    }

    /// <summary>
    /// Read all events from all streams for a trading date, merged and sorted by timestamp.
    /// Used for scenario replay when events may span multiple instruments.
    /// </summary>
    public static IEnumerable<CanonicalExecutionEvent> ReadAllEventsForTradingDate(string projectRoot, string tradingDate)
    {
        var baseDir = Path.Combine(projectRoot, "automation", "logs", "execution_events",
            string.IsNullOrWhiteSpace(tradingDate) ? DateTimeOffset.UtcNow.ToString("yyyy-MM-dd") : tradingDate.Trim());
        if (!Directory.Exists(baseDir))
            yield break;

        var allEvents = new List<CanonicalExecutionEvent>();
        foreach (var file in Directory.GetFiles(baseDir, "*.jsonl"))
        {
            foreach (var evt in ReadEvents(file))
                allEvents.Add(evt);
        }
        allEvents.Sort((a, b) =>
        {
            var ts = string.CompareOrdinal(a.TimestampUtc ?? "", b.TimestampUtc ?? "");
            if (ts != 0) return ts;
            return a.EventSequence.CompareTo(b.EventSequence);
        });
        foreach (var evt in allEvents)
            yield return evt;
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static CanonicalExecutionEvent? ParseEvent(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<CanonicalExecutionEvent>(line, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = name;
        foreach (var c in invalid)
            result = result.Replace(c, '_');
        return result.Trim().ToUpperInvariant();
    }
}
