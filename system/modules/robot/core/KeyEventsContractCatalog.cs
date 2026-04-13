using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core;

/// <summary>
/// Canonical KEY_EVENTS names + minimum payload fields for contract drift checks (see docs/authority/key_events_contract.md).
/// </summary>
public static class KeyEventsContractCatalog
{
    public static readonly string[] SetupPhaseEventTypes =
    {
        "STREAM_SKIPPED",
        "TIMETABLE_APPLY_PARTIAL_REFUSAL",
        "STREAMS_CONSTRUCTION_OUTCOME"
    };

    public static readonly string[] ExecutionSignalEventTypes =
    {
        "EXECUTION_BLOCKED",
        "ENTRY_REJECTED",
        "ENTRY_SUBMITTED",
        "ENTRY_FILLED",
        "ENTRY_TERMINATED"
    };

    /// <summary>Events the narrative reader aggregates (ordering + counts).</summary>
    public static readonly string[] NarrativeTrackedEventTypes =
    {
        "STREAM_SKIPPED",
        "TIMETABLE_APPLY_PARTIAL_REFUSAL",
        "STREAMS_CONSTRUCTION_OUTCOME",
        "EXECUTION_BLOCKED",
        "ENTRY_REJECTED"
    };

    /// <summary>Minimum data.* keys readers should accept for setup events (subset; extra keys allowed).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> MinimumDataKeysByEvent =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["STREAM_SKIPPED"] = new[] { "reason", "trading_date" },
            ["TIMETABLE_APPLY_PARTIAL_REFUSAL"] = new[] { "trading_date", "decision_type" },
            ["STREAMS_CONSTRUCTION_OUTCOME"] = new[] { "trading_date" },
            ["ENTRY_SUBMITTED"] = new[] { "intent_id", "trading_date" },
            ["ENTRY_FILLED"] = new[] { "intent_id", "trading_date", "fill_quantity" },
            ["ENTRY_TERMINATED"] = new[] { "intent_id", "trading_date", "reason" }
        };
}
