// Gap 5: Rebuild execution state from canonical events for deterministic replay validation.

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Reconstructed state from replay. First-pass: lifecycle, protective block, mismatch block, terminal.
/// </summary>
public sealed class ReplayState
{
    public Dictionary<string, string> LifecycleByIntent { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ProtectiveBlockedInstruments { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> MismatchFailClosedInstruments { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> TerminalIntents { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> FlattenedInstruments { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, decimal> OpenQuantityByIntent { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> CommandHistory { get; } = new();
}

/// <summary>
/// Applies canonical events in order to rebuild execution state.
/// </summary>
public static class ExecutionReplayRebuilder
{
    /// <summary>
    /// Rebuild state from events in a stream.
    /// </summary>
    public static ReplayState Rebuild(IEnumerable<CanonicalExecutionEvent> events)
    {
        var state = new ReplayState();
        foreach (var evt in events)
        {
            Apply(state, evt);
        }
        return state;
    }

    private static void Apply(ReplayState state, CanonicalExecutionEvent evt)
    {
        var inst = evt.Instrument ?? "";
        var intentId = evt.IntentId ?? "";

        switch (evt.EventType)
        {
            case ExecutionEventTypes.COMMAND_RECEIVED:
            case ExecutionEventTypes.COMMAND_DISPATCHED:
            case ExecutionEventTypes.COMMAND_COMPLETED:
                if (!string.IsNullOrEmpty(evt.CommandId))
                    state.CommandHistory.Add($"{evt.EventType}:{evt.CommandId}");
                break;

            case ExecutionEventTypes.LIFECYCLE_TRANSITIONED:
                if (!string.IsNullOrEmpty(intentId) && !string.IsNullOrEmpty(evt.LifecycleStateAfter))
                    state.LifecycleByIntent[intentId] = evt.LifecycleStateAfter;
                break;

            case ExecutionEventTypes.PROTECTIVE_INSTRUMENT_BLOCKED:
            case ExecutionEventTypes.INSTRUMENT_FROZEN:
                if (!string.IsNullOrEmpty(inst))
                    state.ProtectiveBlockedInstruments.Add(inst);
                break;

            case ExecutionEventTypes.PROTECTIVE_RECOVERY_CONFIRMED:
            case ExecutionEventTypes.PROTECTIVE_FLATTEN_COMPLETED:
                if (!string.IsNullOrEmpty(inst))
                    state.ProtectiveBlockedInstruments.Remove(inst);
                break;

            case ExecutionEventTypes.MISMATCH_FAIL_CLOSED:
            case ExecutionEventTypes.MISMATCH_BLOCKED:
                if (!string.IsNullOrEmpty(inst))
                    state.MismatchFailClosedInstruments.Add(inst);
                break;

            case ExecutionEventTypes.MISMATCH_CLEARED:
                if (!string.IsNullOrEmpty(inst))
                    state.MismatchFailClosedInstruments.Remove(inst);
                break;

            case ExecutionEventTypes.INTENT_TERMINALIZED:
                if (!string.IsNullOrEmpty(intentId))
                    state.TerminalIntents.Add(intentId);
                break;

            case ExecutionEventTypes.POSITION_FLATTENED:
            case ExecutionEventTypes.SESSION_FORCED_FLATTENED:
                if (!string.IsNullOrEmpty(inst))
                    state.FlattenedInstruments.Add(inst);
                break;

            case ExecutionEventTypes.EXECUTION_OBSERVED:
            case ExecutionEventTypes.EXECUTION_RESOLVED:
                UpdateOpenQuantityFromPayload(state, evt);
                break;

            case ExecutionEventTypes.EXECUTION_DEDUPLICATED:
                break;
        }
    }

    private static void UpdateOpenQuantityFromPayload(ReplayState state, CanonicalExecutionEvent evt)
    {
        if (evt.Payload == null) return;
        var intentId = evt.IntentId ?? "";
        if (string.IsNullOrEmpty(intentId)) return;

        decimal filledQty = GetPayloadDecimal(evt.Payload, "filled_qty") ?? GetPayloadDecimal(evt.Payload, "fill_quantity") ?? 0;
        var side = GetPayloadString(evt.Payload, "side");
        var sign = string.Equals(side, "Short", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
        var delta = filledQty * sign;

        state.OpenQuantityByIntent.TryGetValue(intentId, out var current);
        state.OpenQuantityByIntent[intentId] = current + delta;
    }

    private static decimal? GetPayloadDecimal(object payload, string key)
    {
        if (payload is JsonElement je)
        {
            if (je.TryGetProperty(key, out var p))
            {
                if (p.TryGetDecimal(out var d)) return d;
                if (p.TryGetInt64(out var l)) return l;
            }
            return null;
        }
        if (payload is Dictionary<string, object?> dict && dict.TryGetValue(key, out var v) && v != null)
        {
            if (v is decimal d) return d;
            if (v is double dbl) return (decimal)dbl;
            if (v is int i) return i;
            if (v is long l) return l;
        }
        return null;
    }

    private static string GetPayloadString(object payload, string key)
    {
        if (payload is JsonElement je && je.TryGetProperty(key, out var p))
            return p.GetString() ?? "";
        if (payload is Dictionary<string, object?> dict && dict.TryGetValue(key, out var v))
            return v?.ToString() ?? "";
        return "";
    }
}
