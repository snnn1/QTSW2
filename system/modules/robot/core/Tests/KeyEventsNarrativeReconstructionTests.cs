// Narrative reconstruction: KEY_EVENTS.jsonl alone → trading possible? why not? what blocked?
// Run: dotnet run --project system/modules/robot/core/key-events-narrative-tool/KeyEventsNarrative.Tool.csproj

using System;
using System.IO;

namespace QTSW2.Robot.Core.Tests;

public static class KeyEventsNarrativeReconstructionTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        var e = Case_NoTradeCanonicalMismatchAndOutcome();
        if (e != null) return (false, e);
        e = Case_StreamsReadyNoSkips();
        if (e != null) return (false, e);
        e = Case_ConstructionFailureFlag();
        if (e != null) return (false, e);
        e = Case_TimetableAndExecutionBlocks();
        if (e != null) return (false, e);
        e = Case_OrderingAndConstructionHistory();
        if (e != null) return (false, e);
        e = Case_SkipReasonsByInstrument();
        if (e != null) return (false, e);
        e = Case_ContractValidation();
        if (e != null) return (false, e);
        var (ok, err) = RunFileRoundTrip();
        if (!ok) return (false, err);
        return (true, null);
    }

    private static string? Case_NoTradeCanonicalMismatchAndOutcome()
    {
        var jsonl = "{\"ts_utc\":\"2026-04-11T14:30:00.0000000+00:00\",\"event\":\"TIMETABLE_APPLY_PARTIAL_REFUSAL\",\"instrument\":null,\"stream\":null,\"reason\":\"partial_refusal\",\"data\":{\"trading_date\":\"2026-04-11\",\"decision_type\":\"partial_refusal\",\"affected_streams\":2}}\n"
            + "{\"ts_utc\":\"2026-04-11T14:30:00.1000000+00:00\",\"event\":\"STREAM_SKIPPED\",\"instrument\":\"NQ\",\"stream\":\"NQ1\",\"reason\":\"canonical_mismatch\",\"data\":{\"stream\":\"NQ1\",\"instrument\":\"NQ\",\"reason\":\"canonical_mismatch\",\"trading_date\":\"2026-04-11\"}}\n"
            + "{\"ts_utc\":\"2026-04-11T14:30:01.0000000+00:00\",\"event\":\"STREAMS_CONSTRUCTION_OUTCOME\",\"instrument\":null,\"stream\":null,\"reason\":\"NO_STREAMS\",\"data\":{\"trading_date\":\"2026-04-11\",\"total_candidates\":3,\"streams_created\":0,\"streams_skipped\":2}}";
        var n = KeyEventsNarrativeReader.FromLines(jsonl.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        if (n.SetupStreamsReady)
            return "expected NO_STREAMS";
        if (!n.HadTimetablePartialRefusal || n.LastTimetableDecisionType != "partial_refusal")
            return "expected timetable partial refusal";
        if (!n.StreamSkipReasonCounts.TryGetValue("canonical_mismatch", out var c) || c != 1)
            return "expected one canonical_mismatch skip";
        if (n.WasTradingStructurallyPossible)
            return "expected trading attempt not possible";
        if (!n.Summarize().Contains("No streams armed", StringComparison.OrdinalIgnoreCase))
            return "summary should mention no streams";
        if (n.ConstructionOutcomeHistory.Count != 1 || n.ConstructionOutcomeHistory[0] != "NO_STREAMS")
            return "construction history";
        if (n.FirstEventName != "TIMETABLE_APPLY_PARTIAL_REFUSAL" || n.LastEventName != "STREAMS_CONSTRUCTION_OUTCOME")
            return "first/last event ordering";
        if (n.OrderedHighlights.Count != 3)
            return "highlights count";
        return null;
    }

    private static string? Case_StreamsReadyNoSkips()
    {
        var jsonl = "{\"ts_utc\":\"2026-04-11T15:00:00.0000000+00:00\",\"event\":\"STREAMS_CONSTRUCTION_OUTCOME\",\"instrument\":null,\"stream\":null,\"reason\":\"STREAMS_READY\",\"data\":{\"trading_date\":\"2026-04-11\",\"total_candidates\":1,\"streams_created\":1,\"streams_skipped\":0}}";
        var n = KeyEventsNarrativeReader.FromLines(new[] { jsonl });
        if (!n.SetupStreamsReady || !n.WasTradingStructurallyPossible || !n.WasExecutionReachableAggregate)
            return "expected STREAMS_READY and structurally + execution-clear aggregate";
        return null;
    }

    private static string? Case_ConstructionFailureFlag()
    {
        var jsonl = "{\"ts_utc\":\"2026-04-11T15:00:00.0000000+00:00\",\"event\":\"STREAMS_CONSTRUCTION_OUTCOME\",\"instrument\":null,\"stream\":null,\"reason\":\"NO_STREAMS\",\"data\":{\"trading_date\":\"2026-04-11\",\"total_candidates\":\"UNKNOWN\",\"streams_created\":0,\"streams_skipped\":\"UNKNOWN\",\"failure\":true}}";
        var n = KeyEventsNarrativeReader.FromLines(new[] { jsonl });
        if (!n.ConstructionFailedException)
            return "expected failure flag";
        if (n.WasTradingStructurallyPossible)
            return "failure should disallow structural possibility";
        if (!n.Summarize().Contains("threw", StringComparison.OrdinalIgnoreCase))
            return "summary should mention throw/failure";
        return null;
    }

    private static string? Case_TimetableAndExecutionBlocks()
    {
        var jsonl = "{\"ts_utc\":\"2026-04-11T16:00:00.0000000+00:00\",\"event\":\"STREAMS_CONSTRUCTION_OUTCOME\",\"reason\":\"STREAMS_READY\",\"data\":{\"trading_date\":\"2026-04-11\",\"streams_created\":1,\"streams_skipped\":0}}\n"
            + "{\"ts_utc\":\"2026-04-11T16:01:00.0000000+00:00\",\"event\":\"EXECUTION_BLOCKED\",\"instrument\":\"ES\",\"stream\":\"ES1\",\"reason\":\"risk\",\"data\":{\"layer\":\"stream_risk_gate\"}}\n"
            + "{\"ts_utc\":\"2026-04-11T16:01:01.0000000+00:00\",\"event\":\"ENTRY_REJECTED\",\"instrument\":\"ES\",\"reason\":\"duplicate\"}";
        var n = KeyEventsNarrativeReader.FromLines(jsonl.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        if (!n.SetupStreamsReady)
            return "streams ready";
        if (n.ExecutionBlockedCount != 1 || n.EntryRejectedCount != 1)
            return "counts";
        if (!n.Summarize().Contains("Execution blocked", StringComparison.OrdinalIgnoreCase))
            return "summary execution";
        if (!n.WasTradingStructurallyPossible || n.WasExecutionReachableAggregate)
            return "structural true but execution path not clear aggregate";
        if (!n.HadExecutionDenials)
            return "denials";
        return null;
    }

    private static string? Case_OrderingAndConstructionHistory()
    {
        var jsonl = "{\"ts_utc\":\"2026-05-01T10:00:00+00:00\",\"event\":\"STREAMS_CONSTRUCTION_OUTCOME\",\"reason\":\"NO_STREAMS\",\"data\":{\"trading_date\":\"2026-05-01\"}}\n"
            + "{\"ts_utc\":\"2026-05-01T10:05:00+00:00\",\"event\":\"STREAMS_CONSTRUCTION_OUTCOME\",\"reason\":\"STREAMS_READY\",\"data\":{\"trading_date\":\"2026-05-01\"}}";
        var n = KeyEventsNarrativeReader.FromLines(jsonl.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        if (n.ConstructionOutcomeHistory.Count != 2 || n.ConstructionOutcomeHistory[0] != "NO_STREAMS" || n.ConstructionOutcomeHistory[1] != "STREAMS_READY")
            return "upgrade history";
        if (!n.SetupStreamsReady)
            return "last outcome STREAMS_READY";
        if (n.FirstEventName != "STREAMS_CONSTRUCTION_OUTCOME" || n.LastEventName != "STREAMS_CONSTRUCTION_OUTCOME")
            return "first/last";
        return null;
    }

    private static string? Case_SkipReasonsByInstrument()
    {
        var jsonl = "{\"event\":\"STREAM_SKIPPED\",\"instrument\":\"NQ\",\"data\":{\"instrument\":\"NQ\",\"reason\":\"canonical_mismatch\",\"trading_date\":\"2026-05-01\"}}\n"
            + "{\"event\":\"STREAM_SKIPPED\",\"instrument\":\"ES\",\"data\":{\"instrument\":\"ES\",\"reason\":\"filtered_out\",\"trading_date\":\"2026-05-01\"}}";
        var n = KeyEventsNarrativeReader.FromLines(jsonl.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        if (!n.SkipReasonsByInstrument.TryGetValue("NQ", out var nq) || !nq.TryGetValue("canonical_mismatch", out var c) || c != 1)
            return "NQ bucket";
        if (!n.SkipReasonsByInstrument.TryGetValue("ES", out var es) || !es.TryGetValue("filtered_out", out var c2) || c2 != 1)
            return "ES bucket";
        return null;
    }

    private static string? Case_ContractValidation()
    {
        var e = KeyEventsContractValidation.ValidateCatalogAgainstReader();
        if (e != null) return e;
        e = KeyEventsContractValidation.ValidateReaderCoversTrackedEvents();
        if (e != null) return e;
        return null;
    }

    public static (bool Pass, string? Error) RunFileRoundTrip()
    {
        var jsonl = "{\"event\":\"STREAMS_CONSTRUCTION_OUTCOME\",\"reason\":\"NO_STREAMS\",\"data\":{\"trading_date\":\"2026-01-01\",\"failure\":false}}";
        var path = Path.Combine(Path.GetTempPath(), "key_evt_narr_" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            File.WriteAllText(path, jsonl);
            var n = KeyEventsNarrativeReader.FromFile(path);
            if (n.SetupStreamsReady) return (false, "expected NO_STREAMS from file");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
        return (true, null);
    }
}
