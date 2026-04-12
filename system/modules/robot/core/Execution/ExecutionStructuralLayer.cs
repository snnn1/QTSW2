using System.Collections.Generic;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Structural state only: measures broker/journal parity, authority inputs, repair latches, and exposure anomalies.
/// Returns minimal blocker reasons; does not enqueue work, mutate execution policy, or perform overlays.
/// </summary>
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

        var (realOpen, recoveredOpen, _) = req.Journal.GetPositionAuthorityOpenQuantitiesForInstrument(inst, canonical);
        var derived = PositionAuthorityDerivation.DerivePositionAuthority(
            parity.BrokerPositionQty,
            realOpen,
            recoveredOpen);
        var authorityState = derived.ToString();

        var repairActive = req.RecoveryExecutionDisallowed || req.JournalIntegrityOrReconciliationRepairActive;
        ApplyFacts(snapshot, parity, realOpen, recoveredOpen, authorityState, repairActive, noExposureBrokerOpen: false);

        if (repairActive)
        {
            snapshot.Reason = StructuralBlocker.RepairActive;
            snapshot.Detail = req.RecoveryExecutionDisallowed ? "recovery_execution_disallowed" : "journal_repair_active";
            return false;
        }

        if (!parity.IsOkOrPendingAlignment)
        {
            snapshot.Reason = StructuralBlocker.ParityNotOk;
            snapshot.Detail = parity.Status.ToString();
            return false;
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
}
