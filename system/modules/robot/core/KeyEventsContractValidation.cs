using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace QTSW2.Robot.Core;

/// <summary>
/// Drift guard: contract doc vs reader — each catalogued setup event must parse and expose minimum data keys.
/// </summary>
public static class KeyEventsContractValidation
{
    /// <summary>Sample minimal JSON lines (one per setup event type) aligned with emitter + contract doc.</summary>
    public static IReadOnlyDictionary<string, string> MinimalSampleLinesByEvent =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["STREAM_SKIPPED"] =
                "{\"ts_utc\":\"2026-01-01T00:00:00+00:00\",\"event\":\"STREAM_SKIPPED\",\"instrument\":\"NQ\",\"stream\":\"NQ1\",\"reason\":\"canonical_mismatch\",\"data\":{\"stream\":\"NQ1\",\"instrument\":\"NQ\",\"reason\":\"canonical_mismatch\",\"trading_date\":\"2026-01-01\"}}",
            ["TIMETABLE_APPLY_PARTIAL_REFUSAL"] =
                "{\"ts_utc\":\"2026-01-01T00:00:00+00:00\",\"event\":\"TIMETABLE_APPLY_PARTIAL_REFUSAL\",\"reason\":\"partial_refusal\",\"data\":{\"trading_date\":\"2026-01-01\",\"decision_type\":\"partial_refusal\",\"affected_streams\":1}}",
            ["STREAMS_CONSTRUCTION_OUTCOME"] =
                "{\"ts_utc\":\"2026-01-01T00:00:00+00:00\",\"event\":\"STREAMS_CONSTRUCTION_OUTCOME\",\"reason\":\"NO_STREAMS\",\"data\":{\"trading_date\":\"2026-01-01\",\"streams_created\":0}}",
            ["ENTRY_SUBMITTED"] =
                "{\"ts_utc\":\"2026-01-01T00:00:00+00:00\",\"event\":\"ENTRY_SUBMITTED\",\"instrument\":\"MGC\",\"stream\":\"GC2\",\"data\":{\"intent_id\":\"abc\",\"trading_date\":\"2026-01-01\",\"broker_order_id\":\"x\"}}",
            ["ENTRY_FILLED"] =
                "{\"ts_utc\":\"2026-01-01T00:00:00+00:00\",\"event\":\"ENTRY_FILLED\",\"instrument\":\"MGC\",\"stream\":\"GC2\",\"data\":{\"intent_id\":\"abc\",\"trading_date\":\"2026-01-01\",\"fill_quantity\":1,\"fill_price\":2000.0,\"partial\":false}}",
            ["ENTRY_TERMINATED"] =
                "{\"ts_utc\":\"2026-01-01T00:00:00+00:00\",\"event\":\"ENTRY_TERMINATED\",\"instrument\":\"MGC\",\"stream\":\"GC2\",\"data\":{\"intent_id\":\"abc\",\"trading_date\":\"2026-01-01\",\"reason\":\"cancelled\"}}"
        };

    /// <returns>Null if OK; else human-readable error.</returns>
    public static string? ValidateCatalogAgainstReader()
    {
        foreach (var kv in MinimalSampleLinesByEvent)
        {
            var err = ValidateOneLine(kv.Key, kv.Value);
            if (err != null) return err;
        }

        foreach (var ev in KeyEventsContractCatalog.SetupPhaseEventTypes)
        {
            if (!MinimalSampleLinesByEvent.ContainsKey(ev))
                return $"contract catalog missing sample for {ev}";
        }

        return ValidateMinimumDataKeysPresent();
    }

    private static string? ValidateOneLine(string eventName, string jsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            if (!root.TryGetProperty("event", out var e) || !string.Equals(e.GetString(), eventName, StringComparison.OrdinalIgnoreCase))
                return $"sample {eventName}: event field mismatch";

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return $"sample {eventName}: missing data object";

            if (!KeyEventsContractCatalog.MinimumDataKeysByEvent.TryGetValue(eventName, out var keys))
                return null;

            foreach (var k in keys)
            {
                if (!data.TryGetProperty(k, out _))
                    return $"sample {eventName}: missing data.{k}";
            }
        }
        catch (Exception ex)
        {
            return $"sample {eventName}: {ex.Message}";
        }

        return null;
    }

    private static string? ValidateMinimumDataKeysPresent()
    {
        foreach (var kv in KeyEventsContractCatalog.MinimumDataKeysByEvent)
        {
            if (!MinimalSampleLinesByEvent.ContainsKey(kv.Key))
                return $"minimum keys defined for {kv.Key} but no sample line";
        }
        return null;
    }

    /// <summary>Ensures narrative reader records every narrative-tracked event type in order.</summary>
    public static string? ValidateReaderCoversTrackedEvents()
    {
        var combined = string.Join("\n", MinimalSampleLinesByEvent.Values)
            + "\n{\"event\":\"EXECUTION_BLOCKED\",\"reason\":\"x\",\"data\":{\"layer\":\"t\"}}\n{\"event\":\"ENTRY_REJECTED\",\"reason\":\"y\"}";
        var n = KeyEventsNarrativeReader.FromLines(combined.Split('\n'));
        foreach (var t in KeyEventsContractCatalog.NarrativeTrackedEventTypes)
        {
            if (!n.OrderedHighlights.Any(h => string.Equals(h.Event, t, StringComparison.OrdinalIgnoreCase)))
                return $"narrative highlights missing {t}";
        }
        return null;
    }
}
