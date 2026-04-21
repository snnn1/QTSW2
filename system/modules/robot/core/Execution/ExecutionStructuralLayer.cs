using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Structural state only: measures broker/journal parity, authority inputs, repair latches, and exposure anomalies.
/// Returns minimal blocker reasons; does not enqueue work, mutate execution policy, or perform overlays.
/// </summary>
/// <remarks>
/// Phase 3 demotion (feature flags): parity-not-OK and journal-repair latch may be demoted from submit denies to diagnostics-only.
/// Tier-1 <see cref="QuantExecutionInstrumentPhase.RecoveryRequired"/> blocks directional entries before parity/authority; non-directional
/// submits (e.g. protective) still flow through parity/repair/authority — demoted parity/repair avoids double jeopardy with store recovery mode.
/// Position-authority matrix (<see cref="TryAuthorizeOrderSubmitByAuthority"/>) remains; UNKNOWN still denies broadly; overlap with RecoveryRequired
/// is mainly directional (handled at quant gate first).
/// </remarks>
public static class ExecutionStructuralLayer
{
    /// <summary>Primary structural blocker vocabulary for order-submit path (Reason on <see cref="ExecutionSafetySnapshot"/>).</summary>
    public static class StructuralBlocker
    {
        public const string MissingInstrument = "missing_instrument";
        public const string ParityNotOk = "parity_not_ok";
        public const string RepairActive = "repair_active";
        public const string AuthorityUnknown = "authority_unknown";
        public const string AuthorityRecovery = "authority_recovery";
        public const string AuthorityNotReal = "authority_not_real";
        public const string NoActiveExposuresWithBrokerPosition = "no_active_exposures_with_broker_position";
        public const string QuantUnmappedExecution = "quant_unmapped_execution";
        public const string QuantExecutionLocked = "quant_execution_locked";
        public const string QuantRecoveryRequired = "quant_recovery_required";
    }

    private readonly struct ParityRegistryView : IJournalParityRegistryView
    {
        public ParityRegistryView(bool useIea, int ieaOwnedPlus)
        {
            UseInstrumentExecutionAuthority = useIea;
            IeaOwnedPlusAdoptedWorking = ieaOwnedPlus;
        }

        public bool UseInstrumentExecutionAuthority { get; }
        public int IeaOwnedPlusAdoptedWorking { get; }
    }

    private static string NormalizeInstrument(string? instrument) =>
        string.IsNullOrWhiteSpace(instrument) ? "" : instrument.Trim();

    private static bool IsBookkeepingLagProtected(string inst, DateTimeOffset utc) =>
        PendingAlignmentAuthority.IsPendingAlignment(inst, utc)
        || (FeatureFlags.QuantExecutionControlStoreEnabled
            && QuantExecutionControlStore.OffersBookkeepingLagProtection(inst, utc));

    private static bool TryBypassQuantRecoveryRequiredForMarketReentry(
        string? orderSubmitBlockedWhat,
        QuantExecutionControlSnapshot qSnap,
        out string detail)
    {
        detail = "";
        if (!string.Equals(orderSubmitBlockedWhat, "SUBMIT_MARKET_REENTRY", StringComparison.Ordinal))
            return false;
        if (qSnap.Phase != QuantExecutionInstrumentPhase.RecoveryRequired)
            return false;
        if (!string.Equals(qSnap.RecoveryRequiredReason, "recovery_stabilization_window_expired_broker_gross_mismatch", StringComparison.Ordinal))
            return false;

        detail = "market_reentry_quant_recovery_gross_mismatch_bypass";
        return true;
    }

    private static bool IsJournalAuthorityFallbackSubmit(string? orderSubmitBlockedWhat) =>
        orderSubmitBlockedWhat is "SUBMIT_MARKET_REENTRY" or "SUBMIT_PROTECTIVE_STOP" or "SUBMIT_TARGET";

    private static bool TryFallbackToJournalAuthorityForSubmit(
        string? orderSubmitBlockedWhat,
        DerivedPositionAuthority derivedFromLedger,
        int brokerPositionQty,
        int journalRealOpenQty,
        int journalRecoveredOpenQty,
        out DerivedPositionAuthority fallbackAuthority,
        out string detail)
    {
        detail = "";
        fallbackAuthority = derivedFromLedger;
        if (!IsJournalAuthorityFallbackSubmit(orderSubmitBlockedWhat))
            return false;
        if (derivedFromLedger != DerivedPositionAuthority.UNKNOWN)
            return false;

        var journalAuthority = PositionAuthorityDerivation.DerivePositionAuthority(
            brokerPositionQty, journalRealOpenQty, journalRecoveredOpenQty);
        if (journalAuthority != DerivedPositionAuthority.REAL)
            return false;

        fallbackAuthority = journalAuthority;
        detail = orderSubmitBlockedWhat switch
        {
            "SUBMIT_MARKET_REENTRY" => "market_reentry_authority_fallback_journal_real",
            "SUBMIT_PROTECTIVE_STOP" or "SUBMIT_TARGET" => "risk_coverage_authority_fallback_journal_real",
            _ => ""
        };
        return true;
    }

    private static bool TryBypassParityNotOkForProtectedSubmit(
        string? orderSubmitBlockedWhat,
        JournalParityResult parity,
        DerivedPositionAuthority derived,
        out string detail)
    {
        detail = "";
        if (orderSubmitBlockedWhat is not "SUBMIT_MARKET_REENTRY" and not "SUBMIT_PROTECTIVE_STOP" and not "SUBMIT_TARGET")
            return false;
        if (parity.Status != JournalParityStatus.POSITION_MISMATCH)
            return false;
        if (derived != DerivedPositionAuthority.REAL)
            return false;

        detail = orderSubmitBlockedWhat switch
        {
            "SUBMIT_MARKET_REENTRY" => "market_reentry_parity_position_mismatch_bypass_real_authority",
            "SUBMIT_PROTECTIVE_STOP" or "SUBMIT_TARGET" => "risk_coverage_parity_position_mismatch_bypass_real_authority",
            _ => ""
        };
        return true;
    }

    private static void ApplyFacts(
        ExecutionSafetySnapshot snap,
        JournalParityResult parity,
        int realOpen,
        int recoveryOpen,
        string authorityState,
        bool repairActive,
        bool noExposureBrokerOpen)
    {
        snap.BrokerQty = parity.BrokerPositionQty;
        snap.JournalQty = parity.JournalOpenQty;
        snap.RealOpenQty = realOpen;
        snap.RecoveredOpenQty = recoveryOpen;
        snap.JournalOpenQty = realOpen + recoveryOpen;
        snap.ParityStatus = parity.Status.ToString();
        snap.StructuralRepairActive = repairActive;
        snap.NoActiveExposuresWithBrokerPosition = noExposureBrokerOpen;
        snap.AuthorityState = authorityState;
    }

    /// <summary>
    /// Bookkeeping-lag tolerance: broker snapshot + pending-alignment window + explainable parity — not foreign/insufficient-data faults.
    /// Does not apply to <see cref="ExecutionSafetyEvaluationRequest.RecoveryExecutionDisallowed"/> (handled separately).
    /// </summary>
    private static bool TryBookkeepingLagStructuralBypass(
        ExecutionSafetyEvaluationRequest req,
        JournalParityResult parity,
        string inst,
        out string detail)
    {
        detail = "";
        if (!FeatureFlags.StructuralLayerAllowSubmitDuringPendingAlignmentLag)
            return false;
        if (string.IsNullOrEmpty(inst))
            return false;
        if (!IsBookkeepingLagProtected(inst, req.UtcNow))
            return false;
        if (parity.BrokerPositionQty == 0)
            return false;
        if (parity.Status == JournalParityStatus.INSUFFICIENT_DATA)
            return false;
        if (parity.IeaUnavailableWhenRequired)
            return false;
        // Untagged / foreign broker orders — structural risk, not journal lag.
        if (parity.Status == JournalParityStatus.UNKNOWN_ORDER_PRESENT)
            return false;

        detail = "structural_bookkeeping_lag_bypass_pending_alignment:parity=" + parity.Status;
        return true;
    }

    /// <summary>
    /// Parity, repair latches, authority matrix for order submits — no snapshot-age overlay checks.
    /// </summary>
    public static bool TryEvaluateOrderSubmitStructure(
        ExecutionSafetyEvaluationRequest req,
        string? orderSubmitBlockedWhat,
        bool flattenAuthorityBypass,
        out ExecutionSafetySnapshot snapshot)
    {
        snapshot = new ExecutionSafetySnapshot { Instrument = NormalizeInstrument(req.Instrument) };
        var inst = snapshot.Instrument;
        if (string.IsNullOrEmpty(inst))
        {
            snapshot.Reason = StructuralBlocker.MissingInstrument;
            return false;
        }

        if (req.AccountSnapshot == null)
        {
            snapshot.Reason = StructuralBlocker.ParityNotOk;
            snapshot.Detail = "account_snapshot_null";
            return false;
        }

        if (FeatureFlags.QuantExecutionControlStoreEnabled && !flattenAuthorityBypass)
        {
            var qSnap = QuantExecutionControlStore.GetSnapshot(inst);
            var qPhase = qSnap.Phase;
            if (qPhase == QuantExecutionInstrumentPhase.UnmappedExecution)
            {
                snapshot.Reason = StructuralBlocker.QuantUnmappedExecution;
                snapshot.Detail = "tier1_quant_phase_unmapped";
                return false;
            }

            if (qPhase == QuantExecutionInstrumentPhase.ExecutionLocked)
            {
                snapshot.Reason = StructuralBlocker.QuantExecutionLocked;
                snapshot.Detail = "tier1_quant_phase_locked";
                return false;
            }

            if (qPhase == QuantExecutionInstrumentPhase.RecoveryRequired)
            {
                var blockDirectional = string.IsNullOrEmpty(orderSubmitBlockedWhat) || IsDirectionalOrderSubmit(orderSubmitBlockedWhat);
                if (blockDirectional)
                {
                    if (TryBypassQuantRecoveryRequiredForMarketReentry(orderSubmitBlockedWhat, qSnap, out var quantBypassDetail))
                    {
                        snapshot.Detail = string.IsNullOrEmpty(snapshot.Detail)
                            ? quantBypassDetail
                            : snapshot.Detail + ";" + quantBypassDetail;
                    }
                    else
                    {
                        snapshot.Reason = StructuralBlocker.QuantRecoveryRequired;
                        snapshot.Detail = "tier1_quant_phase_recovery_required";
                        return false;
                    }
                }
            }
        }

        var canonical = string.IsNullOrWhiteSpace(req.CanonicalInstrument) ? inst : req.CanonicalInstrument.Trim();
        var registry = new ParityRegistryView(req.UseInstrumentExecutionAuthority, req.IeaOwnedPlusAdoptedWorking);
        var parity = JournalParityChecker.CheckJournalParity(
            inst,
            req.AccountSnapshot,
            req.Journal,
            registry,
            canonical,
            req.UtcNow,
            req.SnapshotTakenUtc);

        // Phase 8: when ledger ownership is available and the flag is on, derive authority from ledger
        DerivedPositionAuthority derived;
        var (journalRealOpen, journalRecoveredOpen, _) =
            req.Journal.GetPositionAuthorityOpenQuantitiesForInstrument(inst, canonical);
        int realOpen, recoveredOpen;
        if (FeatureFlags.StructuralLayerUseLedgerOwnership && req.LedgerOwnershipSnapshot != null)
        {
            var snap = req.LedgerOwnershipSnapshot;
            realOpen = Math.Abs(snap.LedgerSignedNetQty);
            recoveredOpen = 0;
            derived = PositionAuthorityDerivation.DerivePositionAuthority(
                parity.BrokerPositionQty, realOpen, recoveredOpen);
            if (TryFallbackToJournalAuthorityForSubmit(
                    orderSubmitBlockedWhat,
                    derived,
                    parity.BrokerPositionQty,
                    journalRealOpen,
                    journalRecoveredOpen,
                    out var fallbackAuthority,
                    out var authorityFallbackDetail))
            {
                derived = fallbackAuthority;
                realOpen = journalRealOpen;
                recoveredOpen = journalRecoveredOpen;
                snapshot.Detail = string.IsNullOrEmpty(snapshot.Detail)
                    ? authorityFallbackDetail
                    : snapshot.Detail + ";" + authorityFallbackDetail;
            }
        }
        else
        {
            realOpen = journalRealOpen;
            recoveredOpen = journalRecoveredOpen;
            derived = PositionAuthorityDerivation.DerivePositionAuthority(
                parity.BrokerPositionQty, realOpen, recoveredOpen);
        }
        var authorityState = derived.ToString();

        var recoveryExecutionBlocked = req.RecoveryExecutionDisallowed;
        var journalRepairLatch = req.JournalIntegrityOrReconciliationRepairActive;
        var repairActiveForSnapshot = recoveryExecutionBlocked || journalRepairLatch;
        ApplyFacts(snapshot, parity, realOpen, recoveredOpen, authorityState, repairActiveForSnapshot, noExposureBrokerOpen: false);

        // Recovery guard (disconnect / stand-down): never bypass — not bookkeeping lag.
        if (recoveryExecutionBlocked)
        {
            snapshot.Reason = StructuralBlocker.RepairActive;
            snapshot.Detail = "recovery_execution_disallowed";
            return false;
        }

        if (journalRepairLatch)
        {
            if (TryBookkeepingLagStructuralBypass(req, parity, inst, out var repairLagDetail))
            {
                snapshot.Detail = string.IsNullOrEmpty(snapshot.Detail)
                    ? repairLagDetail
                    : snapshot.Detail + ";" + repairLagDetail;
            }
            else if (FeatureFlags.StructuralLayerPhase3DemoteRepairActiveSubmitDeny)
            {
                const string tag = "phase3_repair_active_demoted_to_diagnostic";
                snapshot.Detail = string.IsNullOrEmpty(snapshot.Detail)
                    ? tag
                    : snapshot.Detail + ";" + tag;
            }
            else
            {
                snapshot.Reason = StructuralBlocker.RepairActive;
                snapshot.Detail = "journal_repair_active";
                return false;
            }
        }

        if (!parity.IsOkOrPendingAlignment)
        {
            if (TryBookkeepingLagStructuralBypass(req, parity, inst, out var parityLagDetail))
            {
                snapshot.Detail = string.IsNullOrEmpty(snapshot.Detail)
                    ? parityLagDetail
                    : snapshot.Detail + ";" + parityLagDetail;
            }
            else if (TryBypassParityNotOkForProtectedSubmit(orderSubmitBlockedWhat, parity, derived, out var parityProtectedDetail))
            {
                snapshot.Detail = string.IsNullOrEmpty(snapshot.Detail)
                    ? parityProtectedDetail
                    : snapshot.Detail + ";" + parityProtectedDetail;
            }
            else if (FeatureFlags.StructuralLayerPhase3DemoteParityNotOkSubmitDeny)
            {
                var tag = "phase3_parity_not_ok_demoted_to_diagnostic:parity=" + parity.Status;
                snapshot.Detail = string.IsNullOrEmpty(snapshot.Detail)
                    ? tag
                    : snapshot.Detail + ";" + tag;
            }
            else
            {
                snapshot.Reason = StructuralBlocker.ParityNotOk;
                snapshot.Detail = parity.Status.ToString();
                return false;
            }
        }

        if (flattenAuthorityBypass)
        {
            snapshot.Reason = "";
            return true;
        }

        if (orderSubmitBlockedWhat != null)
        {
            if (!TryAuthorizeOrderSubmitByAuthority(derived, orderSubmitBlockedWhat, out var deny))
            {
                snapshot.Reason = deny;
                return false;
            }
        }
        else
        {
            if (derived != DerivedPositionAuthority.REAL)
            {
                snapshot.Reason = MapAuthorityToBlocker(derived);
                return false;
            }
        }

        var coord = req.Coordinator;
        var exposuresA = coord?.GetActiveExposuresForInstrument(inst) ?? new List<IntentExposure>();
        var exposuresB = string.IsNullOrEmpty(req.ExecutionInstrumentKey)
            ? new List<IntentExposure>()
            : coord?.GetActiveExposuresForInstrument(req.ExecutionInstrumentKey) ?? new List<IntentExposure>();
        var noActiveExposures = exposuresA.Count == 0 && exposuresB.Count == 0;

        if (noActiveExposures && parity.BrokerPositionQty != 0)
        {
            snapshot.NoActiveExposuresWithBrokerPosition = true;
            // Coordinated market-open reentry can race coordinator exposure registration after the first sibling
            // has already reopened the broker position. Keep earlier parity/authority/quant gates authoritative,
            // but do not fail the second sibling solely because the coordinator view has not caught up yet.
            if (string.Equals(orderSubmitBlockedWhat, "SUBMIT_MARKET_REENTRY", StringComparison.Ordinal))
            {
                const string reentry = "no_active_exposures_bypass_market_reentry_with_broker_position";
                snapshot.Detail = string.IsNullOrEmpty(snapshot.Detail) ? reentry : snapshot.Detail + ";" + reentry;
                snapshot.Reason = "";
                return true;
            }

            // First protective stop after a mapped fill may race exposure registration; do not hard-deny on this alone.
            if (string.Equals(orderSubmitBlockedWhat, "SUBMIT_PROTECTIVE_STOP", StringComparison.Ordinal))
            {
                const string prot = "no_active_exposures_bypass_submittable_protective_with_broker_position";
                snapshot.Detail = string.IsNullOrEmpty(snapshot.Detail) ? prot : snapshot.Detail + ";" + prot;
                snapshot.Reason = "";
                return true;
            }

            // Stop-entry bracket setup can see an existing broker position before the stream exposure view has
            // caught up. Keep recovery/parity/mismatch gates authoritative above, but do not fail the bracket
            // solely on this lagging exposure registration snapshot.
            if (IsOpeningEntryBracketSubmit(orderSubmitBlockedWhat))
            {
                const string entry = "no_active_exposures_bypass_opening_entry_bracket_with_broker_position";
                snapshot.Detail = string.IsNullOrEmpty(snapshot.Detail) ? entry : snapshot.Detail + ";" + entry;
                snapshot.Reason = "";
                return true;
            }

            snapshot.Reason = StructuralBlocker.NoActiveExposuresWithBrokerPosition;
            snapshot.Detail = null;
            return false;
        }

        snapshot.Reason = "";
        return true;
    }

    /// <summary>Flatten structural path: parity + authority bypass + broker/exposure + duplicate flatten — no overlay locks.</summary>
    public static bool TryEvaluateFlattenStructure(
        ExecutionSafetyEvaluationRequest req,
        out ExecutionSafetySnapshot snapshot,
        out string flattenBlockReason)
    {
        flattenBlockReason = "";
        if (!TryEvaluateOrderSubmitStructure(req, orderSubmitBlockedWhat: null, flattenAuthorityBypass: true, out snapshot))
        {
            flattenBlockReason = snapshot.Reason ?? "";
            return false;
        }

        var inst = snapshot.Instrument;
        if (snapshot.BrokerQty <= 0)
        {
            flattenBlockReason = "broker_flat";
            snapshot.Reason = flattenBlockReason;
            return false;
        }

        var canonical = string.IsNullOrWhiteSpace(req.CanonicalInstrument) ? inst : req.CanonicalInstrument.Trim();
        var (realOpen, _, _) = req.Journal.GetPositionAuthorityOpenQuantitiesForInstrument(inst, canonical);
        var coord = req.Coordinator;
        var expA = coord?.GetActiveExposuresForInstrument(inst)?.Count ?? 0;
        var expB = string.IsNullOrEmpty(req.ExecutionInstrumentKey)
            ? 0
            : coord?.GetActiveExposuresForInstrument(req.ExecutionInstrumentKey)?.Count ?? 0;
        var hasTrackedExposure = realOpen > 0 || expA > 0 || expB > 0;
        if (!hasTrackedExposure)
        {
            flattenBlockReason = "no_mapped_exposure";
            snapshot.Reason = flattenBlockReason;
            return false;
        }

        if (ExecutionSafetyGate.ShouldBlockRepeatedFlatten(inst, snapshot.BrokerQty, req.UtcNow))
        {
            flattenBlockReason = "duplicate_flatten_idempotency";
            snapshot.Reason = flattenBlockReason;
            return false;
        }

        return true;
    }

    private static string MapAuthorityToBlocker(DerivedPositionAuthority derived) =>
        derived switch
        {
            DerivedPositionAuthority.RECOVERY => StructuralBlocker.AuthorityRecovery,
            DerivedPositionAuthority.UNKNOWN => StructuralBlocker.AuthorityUnknown,
            _ => StructuralBlocker.AuthorityNotReal
        };

    /// <summary>Phase 2: UNKNOWN blocks all order submits; RECOVERY blocks directional entries/stops only; REAL allows (subject to exposure).</summary>
    private static bool TryAuthorizeOrderSubmitByAuthority(
        DerivedPositionAuthority derived,
        string orderSubmitBlockedWhat,
        out string denyReason)
    {
        denyReason = "";
        if (derived == DerivedPositionAuthority.REAL)
            return true;

        if (derived == DerivedPositionAuthority.UNKNOWN)
        {
            denyReason = StructuralBlocker.AuthorityUnknown;
            return false;
        }

        if (IsDirectionalOrderSubmit(orderSubmitBlockedWhat))
        {
            denyReason = StructuralBlocker.AuthorityRecovery;
            return false;
        }

        return true;
    }

    private static bool IsDirectionalOrderSubmit(string blockedWhat) =>
        blockedWhat is "SUBMIT_ENTRY" or "SUBMIT_ENTRY_STOP";

    private static bool IsOpeningEntryBracketSubmit(string? blockedWhat) =>
        blockedWhat is "SUBMIT_ENTRY_STOP";
}
