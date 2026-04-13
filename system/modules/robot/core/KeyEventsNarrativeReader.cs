using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace QTSW2.Robot.Core;

/// <summary>One ordered row for operator timeline (subset of KEY_EVENTS).</summary>
public readonly struct KeyEventTimelineEntry
{
    public KeyEventTimelineEntry(string eventType, string? tsUtc, string? reason, string? instrument)
    {
        Event = eventType;
        TsUtc = tsUtc;
        Reason = reason;
        Instrument = instrument;
    }

    public string Event { get; }
    public string? TsUtc { get; }
    public string? Reason { get; }
    public string? Instrument { get; }
}

/// <summary>
/// Reconstructs a coarse operator narrative from <see cref="RunRootArtifacts.KeyEventsFileName"/> only (no ENGINE logs).
/// </summary>
public sealed class KeyEventsTradingNarrative
{
    /// <summary>True if the last construction outcome in the file is STREAMS_READY.</summary>
    public bool SetupStreamsReady { get; init; }

    /// <summary>Alias: infrastructure armed at least one stream (last outcome STREAMS_READY).</summary>
    public bool WasSetupValid => SetupStreamsReady;

    /// <summary>True if the last NO_STREAMS outcome had data.failure == true.</summary>
    public bool ConstructionFailedException { get; init; }

    /// <summary>Ordered list of top-level reasons for each STREAMS_CONSTRUCTION_OUTCOME line (retries / upgrades).</summary>
    public IReadOnlyList<string> ConstructionOutcomeHistory { get; init; } = Array.Empty<string>();

    /// <summary>First successfully parsed line: event name + ts_utc.</summary>
    public string? FirstEventName { get; init; }
    public string? FirstEventTsUtc { get; init; }

    /// <summary>Last successfully parsed line: event name + ts_utc.</summary>
    public string? LastEventName { get; init; }
    public string? LastEventTsUtc { get; init; }

    /// <summary>Significant events in file order (subset of types; see <see cref="KeyEventsContractCatalog.NarrativeTrackedEventTypes"/>).</summary>
    public IReadOnlyList<KeyEventTimelineEntry> OrderedHighlights { get; init; } = Array.Empty<KeyEventTimelineEntry>();

    /// <summary>Last STREAMS_CONSTRUCTION_OUTCOME trading_date (may be UNKNOWN).</summary>
    public string? LastConstructionTradingDate { get; init; }

    /// <summary>Counts of STREAM_SKIPPED by payload data.reason (normalized).</summary>
    public IReadOnlyDictionary<string, int> StreamSkipReasonCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-instrument: skip reason → count (instrument key may be UNKNOWN).</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> SkipReasonsByInstrument { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether TIMETABLE_APPLY_PARTIAL_REFUSAL appeared.</summary>
    public bool HadTimetablePartialRefusal { get; init; }

    /// <summary>Last timetable decision_type if any (full_refusal / partial_refusal).</summary>
    public string? LastTimetableDecisionType { get; init; }

    public int ExecutionBlockedCount { get; init; }
    public int EntryRejectedCount { get; init; }

    /// <summary>True if any EXECUTION_BLOCKED or ENTRY_REJECTED appeared (aggregate; not per-stream).</summary>
    public bool HadExecutionDenials => ExecutionBlockedCount > 0 || EntryRejectedCount > 0;

    /// <summary>
    /// Aggregate: setup succeeded without construction exception and no recorded execution-layer denials.
    /// Per-instrument nuance may still exist; use <see cref="OrderedHighlights"/> and skip maps for detail.
    /// </summary>
    public bool WasExecutionReachableAggregate =>
        WasSetupValid && !ConstructionFailedException && !HadExecutionDenials;

    /// <summary>
    /// Structural: streams armed and construction did not fail with exception.
    /// Does not imply orders could fill — see <see cref="WasExecutionReachableAggregate"/>.
    /// </summary>
    public bool WasTradingStructurallyPossible =>
        WasSetupValid && !ConstructionFailedException;

    /// <summary>Primary human explanation: why trading was not possible or what blocked it.</summary>
    public string WhyNotTradingOrBlocked =>
        Summarize();

    /// <summary>Single-line summary for operators.</summary>
    public string Summarize()
    {
        var parts = new List<string>();
        if (ConstructionOutcomeHistory.Count > 0)
            parts.Add($"Construction sequence: {string.Join(" → ", ConstructionOutcomeHistory)}.");
        if (FirstEventName != null)
            parts.Add($"First line: {FirstEventName} @ {FirstEventTsUtc ?? "?"}.");

        if (ConstructionFailedException)
            parts.Add("Stream construction threw before completion (failure flag on outcome).");
        else if (!SetupStreamsReady)
            parts.Add("No streams armed after construction (NO_STREAMS or no outcome).");
        else
            parts.Add("Setup reports streams ready (STREAMS_READY).");

        if (StreamSkipReasonCounts.Count > 0)
        {
            var detail = string.Join(", ", StreamSkipReasonCounts.OrderBy(kv => kv.Key)
                .Select(kv => $"{kv.Key}={kv.Value}"));
            parts.Add($"Stream skips: {detail}.");
        }

        if (SkipReasonsByInstrument.Count > 0)
        {
            var instBits = SkipReasonsByInstrument.OrderBy(kv => kv.Key)
                .Select(kv =>
                {
                    var inner = string.Join(", ", kv.Value.OrderBy(i => i.Key).Select(i => $"{i.Key}={i.Value}"));
                    return $"{kv.Key}: [{inner}]";
                });
            parts.Add($"By instrument: {string.Join("; ", instBits)}.");
        }

        if (HadTimetablePartialRefusal)
            parts.Add($"Timetable tradability reduced ({LastTimetableDecisionType ?? "unknown"}).");

        if (ExecutionBlockedCount > 0)
            parts.Add($"Execution blocked: {ExecutionBlockedCount}.");
        if (EntryRejectedCount > 0)
            parts.Add($"Entry rejected: {EntryRejectedCount}.");

        if (WasSetupValid && HadExecutionDenials)
            parts.Add("Execution denials recorded after setup (aggregate).");

        if (LastEventName != null && LastEventName != FirstEventName)
            parts.Add($"Last line: {LastEventName} @ {LastEventTsUtc ?? "?"}.");

        return string.Join(" ", parts);
    }
}

/// <summary>Parses KEY_EVENTS.jsonl into <see cref="KeyEventsTradingNarrative"/>.</summary>
public static class KeyEventsNarrativeReader
{
    private static readonly HashSet<string> HighlightEvents =
        new(KeyEventsContractCatalog.NarrativeTrackedEventTypes, StringComparer.OrdinalIgnoreCase);

    public static KeyEventsTradingNarrative FromFile(string keyEventsJsonlPath)
    {
        if (string.IsNullOrWhiteSpace(keyEventsJsonlPath) || !File.Exists(keyEventsJsonlPath))
            return Empty();
        return FromLines(File.ReadAllLines(keyEventsJsonlPath));
    }

    public static KeyEventsTradingNarrative FromLines(IEnumerable<string> lines)
    {
        var skipReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var skipByInstrument = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var hadTimetable = false;
        string? lastTimetableDecision = null;
        var setupReady = false;
        var constructionFail = false;
        string? lastConstructionDate = null;
        var execBlocks = 0;
        var entryRej = 0;
        var constructionHistory = new List<string>();
        var highlights = new List<KeyEventTimelineEntry>();
        string? firstEv = null, firstTs = null, lastEv = null, lastTs = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("event", out var evEl)) continue;
                var ev = evEl.GetString() ?? "";
                var ts = root.TryGetProperty("ts_utc", out var tsEl) && tsEl.ValueKind == JsonValueKind.String
                    ? tsEl.GetString()
                    : null;

                if (firstEv == null)
                {
                    firstEv = ev;
                    firstTs = ts;
                }
                lastEv = ev;
                lastTs = ts;

                string? rootInstrument = null;
                if (root.TryGetProperty("instrument", out var insRoot) && insRoot.ValueKind == JsonValueKind.String)
                    rootInstrument = insRoot.GetString();

                if (HighlightEvents.Contains(ev))
                {
                    string? topReason = null;
                    if (root.TryGetProperty("reason", out var tr) && tr.ValueKind == JsonValueKind.String)
                        topReason = tr.GetString();
                    highlights.Add(new KeyEventTimelineEntry(ev, ts, topReason, rootInstrument));
                }

                switch (ev)
                {
                    case "STREAM_SKIPPED":
                        if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
                        {
                            if (TryGetString(d, "reason", out var r) && !string.IsNullOrEmpty(r))
                            {
                                if (!skipReasons.TryGetValue(r, out var c)) c = 0;
                                skipReasons[r] = c + 1;
                            }

                            var instKey = "UNKNOWN";
                            if (TryGetString(d, "instrument", out var di) && !string.IsNullOrWhiteSpace(di))
                                instKey = di.Trim();
                            else if (!string.IsNullOrWhiteSpace(rootInstrument))
                                instKey = rootInstrument.Trim();

                            if (TryGetString(d, "reason", out var r2) && !string.IsNullOrEmpty(r2))
                            {
                                if (!skipByInstrument.TryGetValue(instKey, out var byR))
                                {
                                    byR = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                                    skipByInstrument[instKey] = byR;
                                }
                                if (!byR.TryGetValue(r2, out var rc)) rc = 0;
                                byR[r2] = rc + 1;
                            }
                        }
                        break;
                    case "TIMETABLE_APPLY_PARTIAL_REFUSAL":
                        hadTimetable = true;
                        if (root.TryGetProperty("data", out var td) && td.ValueKind == JsonValueKind.Object &&
                            TryGetString(td, "decision_type", out var dt))
                            lastTimetableDecision = dt;
                        break;
                    case "STREAMS_CONSTRUCTION_OUTCOME":
                        if (root.TryGetProperty("reason", out var topR))
                        {
                            var tr = topR.GetString() ?? "";
                            constructionHistory.Add(tr);
                            setupReady = string.Equals(tr, "STREAMS_READY", StringComparison.OrdinalIgnoreCase);
                        }
                        constructionFail = false;
                        if (root.TryGetProperty("data", out var cd) && cd.ValueKind == JsonValueKind.Object)
                        {
                            if (TryGetString(cd, "trading_date", out var tdd))
                                lastConstructionDate = tdd;
                            if (cd.TryGetProperty("failure", out var f) && f.ValueKind == JsonValueKind.True)
                                constructionFail = true;
                        }
                        if (setupReady)
                            constructionFail = false;
                        break;
                    case "EXECUTION_BLOCKED":
                        execBlocks++;
                        break;
                    case "ENTRY_REJECTED":
                        entryRej++;
                        break;
                }
            }
            catch (JsonException)
            {
                continue;
            }
        }

        var skipInstReadonly = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in skipByInstrument)
            skipInstReadonly[kv.Key] = kv.Value;

        return new KeyEventsTradingNarrative
        {
            SetupStreamsReady = setupReady,
            ConstructionFailedException = constructionFail,
            ConstructionOutcomeHistory = constructionHistory,
            FirstEventName = firstEv,
            FirstEventTsUtc = firstTs,
            LastEventName = lastEv,
            LastEventTsUtc = lastTs,
            OrderedHighlights = highlights,
            LastConstructionTradingDate = lastConstructionDate,
            StreamSkipReasonCounts = skipReasons,
            SkipReasonsByInstrument = skipInstReadonly,
            HadTimetablePartialRefusal = hadTimetable,
            LastTimetableDecisionType = lastTimetableDecision,
            ExecutionBlockedCount = execBlocks,
            EntryRejectedCount = entryRej
        };
    }

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(name, out var p)) return false;
        if (p.ValueKind == JsonValueKind.String)
        {
            value = p.GetString() ?? "";
            return value.Length > 0;
        }
        return false;
    }

    private static KeyEventsTradingNarrative Empty() => new()
    {
        SetupStreamsReady = false,
        StreamSkipReasonCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        SkipReasonsByInstrument = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.OrdinalIgnoreCase),
        ConstructionOutcomeHistory = Array.Empty<string>(),
        OrderedHighlights = Array.Empty<KeyEventTimelineEntry>()
    };
}
