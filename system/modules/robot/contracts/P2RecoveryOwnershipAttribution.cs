// P2 Phase 1: Stream-scoped recovery vs instrument-scoped destructive action (policy + attribution).
// Namespace matches RecoveryPhase3Types (Robot.Contracts assembly, consumed by net48 IEA).

using System;
using System.Collections.Generic;
using System.Linq;

namespace QTSW2.Robot.Core.Execution;

/* ═══════════════════════════════════════════════════════════════════════════
 * P2.1-E — Action classification (reference for operators / future policy UI)
 *
 * Stream-scoped (default when single-stream attributable):
 *   - Stand down implicated stream(s) only
 *   - Block new submissions for that stream
 *   - Mark intents blocked / recovery-needed
 *   - Ownership repair for that stream’s orders only
 *   - Cancel only orders positively attributable to that stream (existing policy)
 *   - Stream-scoped recovery events
 *
 * Instrument-scoped (requires stronger proof — see CanEscalate…):
 *   - Flatten account position for symbol
 *   - Broad cancel all working for symbol
 *   - Symbol-level stand-down / fail-closed recovery mode
 *
 * Emergency instrument-scoped:
 *   - Existing fail-closed emergencies (orphan fill, protective timeout, etc.)
 *   - Narrow triggers only; bypasses stream containment
 *
 * P2.1-I — Invariants (Phase 1)
 *   - Single-stream ownership ambiguity must not trigger symbol-wide flatten by default.
 *   - Sibling exposure must not be canceled unless attributable to acting stream OR instrument-wide ambiguity proven.
 *   - Instrument-wide destructive recovery requires stronger proof than stream stand-down.
 *   - Unattributed broker risk may justify instrument scope only after attribution evaluation marks unattributable/contradictory.
 *   - Aggregated/shared broker orders must not be treated as solely owned by one stream during recovery ambiguity.
 *   - Stream stand-down precedes sibling-affecting recovery when issue is stream-attributable.
 * ═══════════════════════════════════════════════════════════════════════════ */

/// <summary>Target recovery scope for policy decisions (P2.1-A).</summary>
public enum RecoveryScope
{
    StreamScoped,
    InstrumentScoped,
    EmergencyInstrumentScoped
}

/// <summary>Outcome of ownership attribution (P2.1-A).</summary>
public enum AttributableScopeKind
{
    SingleStream,
    MultiStreamShared,
    Unattributable,
    Contradictory
}

/// <summary>Structured attribution result (P2.1-A). Not a boolean.</summary>
public sealed class StateOwnershipAttributionResult
{
    public string ExecutionInstrumentKey { get; set; } = "";

    public List<string> ImplicatedStreams { get; set; } = new();

    public List<string> ImplicatedIntentIds { get; set; } = new();

    public List<string> UnattributedBrokerOrderIds { get; set; } = new();

    public AttributableScopeKind AttributableScope { get; set; }

    public bool IsSingleStreamAttributable { get; set; }

    public bool IsMultiStreamAttributable { get; set; }

    public bool IsUnattributable { get; set; }

    public bool IsContradictory { get; set; }

    public string Summary { get; set; } = "";

    public List<string> Contradictions { get; set; } = new();

    /// <summary>Recommended scope before emergency override.</summary>
    public RecoveryScope RecommendedRecoveryScope { get; set; } = RecoveryScope.StreamScoped;
}

/// <summary>Intent + stream line for attribution input.</summary>
public sealed class RecoveryAttributionIntentRow
{
    public string IntentId { get; set; } = "";
    public string? Stream { get; set; }
    public bool HasWorkingOrder { get; set; }
}

/// <summary>Registry working row for attribution.</summary>
public sealed class RecoveryAttributionRegistryRow
{
    public string BrokerOrderId { get; set; } = "";
    public string? IntentId { get; set; }
    public string? Stream { get; set; }
    public string OwnershipStatus { get; set; } = "";
    public bool IsEntry { get; set; }
}

/// <summary>Broker working order as seen for attribution.</summary>
public sealed class RecoveryAttributionBrokerOrderRow
{
    public string BrokerOrderId { get; set; } = "";
    public string? TagIntentId { get; set; }
    public bool IsAggregatedTag { get; set; }
    public List<string> AggregatedIntentIds { get; set; } = new();
    /// <summary>Intent id from IEA registry if resolved for this broker id.</summary>
    public string? RegistryIntentId { get; set; }
}

/// <summary>Snapshot for <see cref="RecoveryOwnershipAttributionEvaluator.EvaluateOwnershipAttributionForRecovery"/>.</summary>
public sealed class RecoveryAttributionSnapshot
{
    public string ExecutionInstrumentKey { get; set; } = "";
    public string? TriggerReason { get; set; }
    public string? TriggerIntentId { get; set; }
    public string? TriggerBrokerOrderId { get; set; }
    public int BrokerPositionQty { get; set; }
    public int BrokerWorkingCount { get; set; }
    public int JournalOpenQtySum { get; set; }
    /// <summary>True when registry has UNOWNED working, IEA in recovery, or gate engaged (caller sets as needed).</summary>
    public bool DegradedOwnershipOrAmbiguity { get; set; }
    public bool GateEngagedForSymbol { get; set; }
    public List<RecoveryAttributionIntentRow> Intents { get; set; } = new();
    public List<RecoveryAttributionRegistryRow> RegistryWorking { get; set; } = new();
    public List<RecoveryAttributionBrokerOrderRow> BrokerWorking { get; set; } = new();
}

/// <summary>Optional directive when P2 blocks instrument flatten (stream containment).</summary>
public sealed class P2PostReconstructionDirective
{
    public bool UseStreamContainmentInsteadOfInstrumentFlatten { get; set; }
    public StateOwnershipAttributionResult? Attribution { get; set; }
}

/// <summary>P2.1-B: central ownership attribution for recovery (P2.1-D escalation guard).</summary>
public static class RecoveryOwnershipAttributionEvaluator
{
    /// <summary>P2.6: Emergency recovery reasons — strict prefix / explicit triggers only (no substring Contains).</summary>
    public static bool IsEmergencyInstrumentRecoveryTrigger(string? reason)
    {
        var t = DestructiveTriggerParser.TryParseStrictPrefix(reason);
        return t.HasValue && DestructiveTriggerClassifier.IsEmergencyInstrumentTrigger(t.Value);
    }

    public static StateOwnershipAttributionResult EvaluateOwnershipAttributionForRecovery(RecoveryAttributionSnapshot snap)
    {
        var r = new StateOwnershipAttributionResult
        {
            ExecutionInstrumentKey = snap.ExecutionInstrumentKey ?? ""
        };
        var contradictions = new List<string>();
        var unattributed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var implicatedStreams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var implicatedIntents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var intentStream = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in snap.Intents)
        {
            if (string.IsNullOrEmpty(row.IntentId)) continue;
            if (!string.IsNullOrEmpty(row.Stream))
                intentStream[row.IntentId] = row.Stream!;
        }

        void AddImplicatedIntent(string? intentId)
        {
            if (string.IsNullOrEmpty(intentId)) return;
            implicatedIntents.Add(intentId);
            if (intentStream.TryGetValue(intentId, out var st) && !string.IsNullOrEmpty(st))
                implicatedStreams.Add(st);
        }

        if (!string.IsNullOrEmpty(snap.TriggerIntentId))
            AddImplicatedIntent(snap.TriggerIntentId);

        foreach (var reg in snap.RegistryWorking)
        {
            if (!reg.OwnershipStatus.Equals("UNOWNED", StringComparison.OrdinalIgnoreCase))
                continue;
            AddImplicatedIntent(reg.IntentId);
            var hasStream = !string.IsNullOrEmpty(reg.IntentId) && intentStream.ContainsKey(reg.IntentId);
            if (!hasStream && !string.IsNullOrEmpty(reg.BrokerOrderId))
                unattributed.Add(reg.BrokerOrderId);
        }

        foreach (var b in snap.BrokerWorking)
        {
            if (b.IsAggregatedTag && b.AggregatedIntentIds is { Count: > 1 })
            {
                foreach (var id in b.AggregatedIntentIds)
                    AddImplicatedIntent(id);
                continue;
            }

            var tagIntent = b.TagIntentId;
            var regIntent = b.RegistryIntentId;
            if (!string.IsNullOrEmpty(tagIntent) && !string.IsNullOrEmpty(regIntent) &&
                !string.Equals(tagIntent, regIntent, StringComparison.OrdinalIgnoreCase))
            {
                contradictions.Add($"TAG_REGISTRY_MISMATCH:{b.BrokerOrderId}:tag={tagIntent}:registry={regIntent}");
            }

            if (string.IsNullOrEmpty(tagIntent) && string.IsNullOrEmpty(regIntent))
            {
                if (!string.IsNullOrEmpty(b.BrokerOrderId))
                    unattributed.Add(b.BrokerOrderId);
                continue;
            }

            var effectiveIntent = !string.IsNullOrEmpty(regIntent) ? regIntent : tagIntent;
            AddImplicatedIntent(effectiveIntent);
            if (!string.IsNullOrEmpty(b.BrokerOrderId) &&
                !string.IsNullOrEmpty(effectiveIntent) && intentStream.ContainsKey(effectiveIntent))
                unattributed.Remove(b.BrokerOrderId);
        }

        if (!string.IsNullOrEmpty(snap.TriggerBrokerOrderId))
            unattributed.Remove(snap.TriggerBrokerOrderId);

        var stillUnattributed = new List<string>();
        foreach (var oid in unattributed)
        {
            var reg = snap.RegistryWorking.FirstOrDefault(x => string.Equals(x.BrokerOrderId, oid, StringComparison.OrdinalIgnoreCase));
            if (reg != null && !string.IsNullOrEmpty(reg.IntentId) && intentStream.ContainsKey(reg.IntentId))
                continue;
            stillUnattributed.Add(oid);
        }

        r.UnattributedBrokerOrderIds.AddRange(stillUnattributed.OrderBy(x => x));
        r.ImplicatedIntentIds.AddRange(implicatedIntents.OrderBy(x => x));
        r.ImplicatedStreams.AddRange(implicatedStreams.OrderBy(x => x));
        r.Contradictions.AddRange(contradictions);
        r.IsContradictory = contradictions.Count > 0;
        r.IsUnattributable = stillUnattributed.Count > 0;
        var distinctStreams = implicatedStreams.Count;

        if (r.IsContradictory)
            r.AttributableScope = AttributableScopeKind.Contradictory;
        else if (r.IsUnattributable)
            r.AttributableScope = AttributableScopeKind.Unattributable;
        else if (distinctStreams > 1)
            r.AttributableScope = AttributableScopeKind.MultiStreamShared;
        else if (distinctStreams == 1)
            r.AttributableScope = AttributableScopeKind.SingleStream;
        else
        {
            r.AttributableScope = AttributableScopeKind.Unattributable;
            r.IsUnattributable = true;
            if (string.IsNullOrEmpty(r.Summary))
                r.Summary = "No stream line resolved for implicated state; treat as unattributable.";
        }

        r.IsSingleStreamAttributable = r.AttributableScope == AttributableScopeKind.SingleStream && !r.IsContradictory;
        r.IsMultiStreamAttributable = r.AttributableScope == AttributableScopeKind.MultiStreamShared
            || (snap.DegradedOwnershipOrAmbiguity && snap.BrokerWorking.Any(x => x.IsAggregatedTag && x.AggregatedIntentIds.Count > 1));

        r.RecommendedRecoveryScope = r.IsContradictory || r.IsUnattributable || r.IsMultiStreamAttributable
            ? RecoveryScope.InstrumentScoped
            : RecoveryScope.StreamScoped;

        r.Summary = $"scope={r.AttributableScope}; streams={distinctStreams}; intents={implicatedIntents.Count}; unattributed_orders={unattributed.Count}; contradictions={contradictions.Count}";
        return r;
    }

    /// <summary>P2.1-D: Instrument-wide flatten/cancel/broad recovery allowed only with stronger proof.</summary>
    public static bool CanEscalateToInstrumentScopedRecovery(
        StateOwnershipAttributionResult attribution,
        RecoveryAttributionSnapshot snapshot,
        string? reason,
        bool emergencyInstrumentRequested,
        bool gateEngagedForSymbol)
    {
        if (emergencyInstrumentRequested)
            return true;

        if (attribution.IsContradictory)
            return true;

        if (attribution.IsUnattributable)
            return true;

        if (attribution.AttributableScope == AttributableScopeKind.MultiStreamShared)
            return true;

        if (attribution.IsSingleStreamAttributable)
        {
            if (gateEngagedForSymbol)
                return false;
            if (snapshot.BrokerPositionQty > 0 && snapshot.JournalOpenQtySum == 0)
                return true;
            return false;
        }

        return true;
    }

    /// <summary>Reconciliation qty mismatch: attribute to single stream if only one stream has open journal exposure.</summary>
    public static StateOwnershipAttributionResult EvaluateReconciliationQuantityMismatch(
        string executionInstrumentKey,
        int accountQty,
        int journalQty,
        IReadOnlyList<(string Stream, int OpenQty)> streamOpenQtys)
    {
        var snap = new RecoveryAttributionSnapshot
        {
            ExecutionInstrumentKey = executionInstrumentKey,
            TriggerReason = "RECONCILIATION_QTY_MISMATCH",
            BrokerPositionQty = accountQty,
            JournalOpenQtySum = journalQty,
            DegradedOwnershipOrAmbiguity = true
        };
        foreach (var (stream, qty) in streamOpenQtys)
        {
            if (qty <= 0 || string.IsNullOrEmpty(stream)) continue;
            snap.Intents.Add(new RecoveryAttributionIntentRow { IntentId = $"stream:{stream}", Stream = stream, HasWorkingOrder = true });
        }

        var r = new StateOwnershipAttributionResult { ExecutionInstrumentKey = executionInstrumentKey };
        var nonZero = streamOpenQtys.Where(s => s.OpenQty > 0 && !string.IsNullOrEmpty(s.Stream)).ToList();
        if (nonZero.Count == 1)
        {
            r.ImplicatedStreams.Add(nonZero[0].Stream);
            r.IsSingleStreamAttributable = true;
            r.AttributableScope = AttributableScopeKind.SingleStream;
            r.RecommendedRecoveryScope = RecoveryScope.StreamScoped;
            r.Summary = "Single stream with open journal exposure on instrument; mismatch attributable to that lineage.";
        }
        else if (nonZero.Count > 1)
        {
            foreach (var x in nonZero)
                r.ImplicatedStreams.Add(x.Stream);
            r.IsMultiStreamAttributable = true;
            r.AttributableScope = AttributableScopeKind.MultiStreamShared;
            r.RecommendedRecoveryScope = RecoveryScope.InstrumentScoped;
            r.Summary = "Multiple streams hold open journal exposure; treat as instrument-wide ambiguity.";
        }
        else
        {
            r.IsUnattributable = true;
            r.AttributableScope = AttributableScopeKind.Unattributable;
            r.RecommendedRecoveryScope = RecoveryScope.InstrumentScoped;
            r.Summary = "No stream-level journal exposure; mismatch not attributable to a single stream.";
        }

        if (accountQty > journalQty)
        {
            r.Contradictions.Add("broker_ahead:position_not_fully_explained_by_journal_sum");
            r.IsContradictory = true;
            r.AttributableScope = AttributableScopeKind.Contradictory;
            r.RecommendedRecoveryScope = RecoveryScope.InstrumentScoped;
        }

        return r;
    }

    public static bool CanEscalateReconciliationToInstrumentFreeze(
        StateOwnershipAttributionResult reconciliationAttribution,
        int accountQty,
        int journalQty)
    {
        if (reconciliationAttribution.IsContradictory)
            return true;
        if (reconciliationAttribution.AttributableScope == AttributableScopeKind.Unattributable)
            return true;
        if (reconciliationAttribution.AttributableScope == AttributableScopeKind.MultiStreamShared)
            return true;
        if (accountQty > journalQty)
            return true;
        return !reconciliationAttribution.IsSingleStreamAttributable;
    }
}
