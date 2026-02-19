using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using QTSW2.Robot.Contracts;

namespace QTSW2.Robot.ReplayHost;

/// <summary>
/// Runs invariants against IEA snapshot at each replay step.
/// </summary>
public static class InvariantRunner
{
    public sealed class InvariantResult
    {
        public bool Passed { get; set; }
        public string? InvariantId { get; set; }
        public string? InvariantType { get; set; }
        public int Step { get; set; }
        public string? Detail { get; set; }
        /// <summary>Machine-parseable reason classification.</summary>
        public string? Reason { get; set; }
    }

    public static List<InvariantExpectation> LoadExpected(string path)
    {
        var json = File.ReadAllText(path);
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var spec = JsonSerializer.Deserialize<InvariantSpec>(json, opts);
        return spec?.Invariants ?? new List<InvariantExpectation>();
    }

    public static List<InvariantResult> RunAll(
        IReadOnlyList<InvariantExpectation> invariants,
        IEASnapshot snapshot,
        int stepIndex,
        IReadOnlyList<ReplayEventEnvelope> eventsProcessedSoFar)
    {
        var results = new List<InvariantResult>();
        foreach (var inv in invariants)
        {
            var r = RunOne(inv, snapshot, stepIndex, eventsProcessedSoFar);
            if (r != null)
                results.Add(r);
        }
        return results;
    }

    private static InvariantResult? RunOne(
        InvariantExpectation inv,
        IEASnapshot snapshot,
        int stepIndex,
        IReadOnlyList<ReplayEventEnvelope> eventsProcessedSoFar)
    {
        switch (inv.Type.ToUpperInvariant())
        {
            case "NO_DUPLICATE_EXECUTION_PROCESSED":
                return CheckNoDuplicateExecutionProcessed(inv, snapshot, stepIndex, eventsProcessedSoFar);
            case "INTENT_REQUIRES_POLICY_BEFORE_SUBMISSION":
                return CheckIntentRequiresPolicyBeforeSubmission(inv, snapshot, stepIndex);
            case "NO_UNPROTECTED_POSITION":
                return CheckNoUnprotectedPosition(inv, snapshot, stepIndex, eventsProcessedSoFar);
            case "BE_PRICE_CROSSED_BY_STEP":
                return CheckBEPriceCrossedByStep(inv, snapshot, stepIndex);
            case "BE_TRIGGERED_BY_STEP":
                return CheckBETriggeredByStep(inv, snapshot, stepIndex);
            default:
                return null;
        }
    }

    private static InvariantResult? CheckNoDuplicateExecutionProcessed(InvariantExpectation inv, IEASnapshot snapshot, int stepIndex, IReadOnlyList<ReplayEventEnvelope> eventsProcessedSoFar)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in eventsProcessedSoFar)
        {
            if (e.Type != ReplayEventType.ExecutionUpdate || e.Payload is not ReplayExecutionUpdate eu)
                continue;
            var execId = eu.ExecutionId;
            if (string.IsNullOrEmpty(execId)) continue;
            if (seen.TryGetValue(execId, out var count))
            {
                return new InvariantResult
                {
                    Passed = false,
                    InvariantId = inv.Id,
                    InvariantType = inv.Type,
                    Step = stepIndex,
                    Detail = $"Execution {execId} processed {count + 1} times",
                    Reason = "DUPLICATE_EXECUTION"
                };
            }
            seen[execId] = 1;
        }
        return null;
    }

    private static InvariantResult? CheckIntentRequiresPolicyBeforeSubmission(InvariantExpectation inv, IEASnapshot snapshot, int stepIndex)
    {
        foreach (var kvp in snapshot.OrderMap)
        {
            var oi = kvp.Value;
            if (!oi.IsEntryOrder) continue;
            if (oi.EntryFillTime == null) continue;
            var intentId = oi.IntentId;
            if (string.IsNullOrEmpty(intentId)) continue;
            if (!snapshot.IntentPolicy.ContainsKey(intentId))
            {
                return new InvariantResult
                {
                    Passed = false,
                    InvariantId = inv.Id,
                    InvariantType = inv.Type,
                    Step = stepIndex,
                    Detail = $"Intent {intentId} has entry fill but no policy registered",
                    Reason = "INTENT_WITHOUT_POLICY"
                };
            }
        }
        return null;
    }

    private static InvariantResult? CheckNoUnprotectedPosition(InvariantExpectation inv, IEASnapshot snapshot, int stepIndex, IReadOnlyList<ReplayEventEnvelope> eventsProcessedSoFar)
    {
        var maxEvents = 10;
        if (inv.Params.TryGetValue("max_events_unprotected", out var v))
        {
            if (v is JsonElement je)
                maxEvents = je.GetInt32();
            else if (v is int i)
                maxEvents = i;
        }

        foreach (var kvp in snapshot.OrderMap)
        {
            var oi = kvp.Value;
            if (!oi.IsEntryOrder || oi.EntryFillTime == null) continue;
            var intentId = oi.IntentId;
            if (string.IsNullOrEmpty(intentId)) continue;
            var bothAcked = oi.ProtectiveStopAcknowledged && oi.ProtectiveTargetAcknowledged;
            if (bothAcked) continue;

            var fillIndex = -1;
            for (var i = 0; i < eventsProcessedSoFar.Count; i++)
            {
                var e = eventsProcessedSoFar[i];
                if (e.Type != ReplayEventType.ExecutionUpdate || e.Payload is not ReplayExecutionUpdate eu)
                    continue;
                var eIntentId = eu.IntentId ?? DecodeIntentIdFromTag(eu.Tag ?? "");
                if (string.Equals(eIntentId, intentId, StringComparison.OrdinalIgnoreCase))
                {
                    fillIndex = i;
                    break;
                }
            }
            if (fillIndex < 0) continue;
            var eventsSinceFill = eventsProcessedSoFar.Count - fillIndex - 1;
            if (eventsSinceFill > maxEvents)
            {
                return new InvariantResult
                {
                    Passed = false,
                    InvariantId = inv.Id,
                    InvariantType = inv.Type,
                    Step = stepIndex,
                    Detail = $"Intent {intentId} unprotected for {eventsSinceFill} events (max {maxEvents})",
                    Reason = "UNPROTECTED_TIMEOUT"
                };
            }
        }
        return null;
    }

    private static InvariantResult? CheckBEPriceCrossedByStep(InvariantExpectation inv, IEASnapshot snapshot, int stepIndex)
    {
        var intentId = GetParamString(inv, "intent_id");
        var latestStep = GetParamInt(inv, "latest_step_index", -1);
        if (string.IsNullOrEmpty(intentId) || latestStep < 0)
            return new InvariantResult { Passed = false, InvariantId = inv.Id, InvariantType = inv.Type, Step = stepIndex, Detail = "Missing intent_id or latest_step_index", Reason = "INVALID_PARAMS" };

        if (stepIndex < latestStep) return null;
        if (stepIndex > latestStep) return null;

        var crossed = snapshot.BeState.ContainsKey($"triggerReached:{intentId}") || snapshot.BeState.ContainsKey($"triggered:{intentId}");
        if (!crossed)
        {
            return new InvariantResult
            {
                Passed = false,
                InvariantId = inv.Id,
                InvariantType = inv.Type,
                Step = stepIndex,
                Detail = $"BE price not crossed for intent {intentId} by step {latestStep}",
                Reason = "BE_PRICE_NOT_CROSSED"
            };
        }
        return null;
    }

    private static InvariantResult? CheckBETriggeredByStep(InvariantExpectation inv, IEASnapshot snapshot, int stepIndex)
    {
        var intentId = GetParamString(inv, "intent_id");
        var latestStep = GetParamInt(inv, "latest_step_index", -1);
        if (string.IsNullOrEmpty(intentId) || latestStep < 0)
            return new InvariantResult { Passed = false, InvariantId = inv.Id, InvariantType = inv.Type, Step = stepIndex, Detail = "Missing intent_id or latest_step_index", Reason = "INVALID_PARAMS" };

        if (stepIndex < latestStep) return null;
        if (stepIndex > latestStep) return null;

        var triggered = snapshot.BeState.ContainsKey($"triggered:{intentId}");
        if (!triggered)
        {
            return new InvariantResult
            {
                Passed = false,
                InvariantId = inv.Id,
                InvariantType = inv.Type,
                Step = stepIndex,
                Detail = $"BE not triggered for intent {intentId} by step {latestStep}",
                Reason = "BE_NOT_TRIGGERED"
            };
        }
        return null;
    }

    private static string? GetParamString(InvariantExpectation inv, string key)
    {
        if (!inv.Params.TryGetValue(key, out var v)) return null;
        if (v is JsonElement je) return je.GetString();
        return v?.ToString();
    }

    private static int GetParamInt(InvariantExpectation inv, string key, int defaultValue)
    {
        if (!inv.Params.TryGetValue(key, out var v)) return defaultValue;
        if (v is JsonElement je) return je.GetInt32();
        if (v is int i) return i;
        return defaultValue;
    }

    private static string? DecodeIntentIdFromTag(string tag)
    {
        if (string.IsNullOrEmpty(tag) || !tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase))
            return null;
        var remainder = tag.Substring(6);
        var idx = remainder.IndexOf(':');
        return idx < 0 ? remainder : remainder.Substring(0, idx);
    }
}
