using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>Runtime toggles for staged robot behaviors (defaults are production-safe).</summary>
public static class FeatureFlags
{
    /// <summary>
    /// When true, runs an extra <see cref="ExecutionJournal.CloseRecoveredRowsSupersededByRealExposure"/> pass
    /// when real open quantity already matches broker but recovered rows still carry open qty (overlap / illegal authority).
    /// </summary>
    public static bool EnablePositionAuthorityEnforcement { get; set; }

    /// <summary>
    /// When true: journal integrity does not align / upsert recovered intents / reconstruct when parity or authority is unsafe;
    /// engine may trigger broker <see cref="HardFailClosedExecutionModel"/> flatten once per instrument.
    /// </summary>
    public static bool EnableHardFailClosedJournalIntegrity { get; set; } = true;

    /// <summary>When true, engine requests broker hard flatten when parity pre-check fails (after execution activity).</summary>
    public static bool EnableHardFailClosedBrokerFlatten { get; set; } = true;

    /// <summary>
    /// When true, <see cref="PostFillAlignmentGate"/> arms on mapped trusted fills and can classify broker/journal lag as
    /// <see cref="JournalParityStatus.PARITY_PENDING_ALIGNMENT"/> after ledger entries are cleared but within the window.
    /// </summary>
    public static bool EnablePostFillAlignmentGate { get; set; } = true;

    /// <summary>Wall-clock window for post-fill alignment after <see cref="JournalParityPendingLedger.TryRecordTrustedFill"/>.</summary>
    public static int PostFillAlignmentWindowMs { get; set; } = 5000;

    /// <summary>
    /// Phase A execution control: when false (default), <see cref="RobotEngine.TryEnsureJournalIntegrityAfterExecutionActivity"/>
    /// never calls <see cref="IExecutionAdapter.TryTriggerHardFlatten"/> from parity/journal pre-check status — parity remains diagnostic only on this path.
    /// Unmapped-fill and adapter fail-closed paths are unchanged. Set true to restore automatic parity-driven hard flatten from try-ensure.
    /// </summary>
    public static bool ControlPlaneParityHardFlattenFromTryEnsureJournalIntegrity { get; set; } = false;

    /// <summary>
    /// When true (default), <see cref="MismatchEscalationCoordinator.TryExitFailClosedWhenSafe"/> requires multiple fresh
    /// account snapshots with full (non-cached) release readiness, stability across a bounded quiet window, and a mandatory
    /// post-release recheck before trusting <c>fail_closed_safe_recovery</c> release.
    /// </summary>
    public static bool FailClosedStrictReleaseConfirmationEnabled { get; set; } = true;

    /// <summary>
    /// [DEPRECATED — P6 refactor] Mismatch execution block is now a first-class authority input
    /// via <see cref="MismatchEscalationCoordinator.IsInstrumentBlockedByMismatch"/> feeding
    /// <see cref="UnifiedExecutionAuthority"/> Gate 2. This flag is retained only for backwards
    /// compatibility — it no longer gates the coordinator's block publication or query.
    /// </summary>
    [Obsolete("P6: Mismatch block is now unconditionally published. Remove after dual-run validation.")]
    public static bool ControlPlaneMismatchExecutionBlockAuthority { get; set; } = true;

    /// <summary>
    /// When true (default), <see cref="ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure"/> does not deny solely for
    /// journal-repair latch or non-OK parity during <see cref="PendingAlignmentAuthority"/> (mapped-fill / post-adoption lag),
    /// if broker shows open quantity and the case is not a foreign-order or insufficient-data structural fault.
    /// <see cref="ExecutionSafetyEvaluationRequest.RecoveryExecutionDisallowed"/> is never bypassed.
    /// </summary>
    public static bool StructuralLayerAllowSubmitDuringPendingAlignmentLag { get; set; } = true;

    /// <summary>
    /// Tier 1 quant control plane: <see cref="QuantExecutionControlStore"/> tracks per-instrument phase and expected state
    /// from mapped/unmapped fills and parity-OK transitions. When false, the store no-ops and structural checks ignore it (rollback).
    /// </summary>
    public static bool QuantExecutionControlStoreEnabled { get; set; } = true;

    /// <summary>
    /// When true (default), aggregated entry fills on one broker order (e.g. CL1+CL2 → MCL) submit a single protective
    /// wave on the lexicographic lead intent with combined quantity; peer intents delegate idempotency to the lead.
    /// Disabling restores per-intent protective submission (legacy; may trip PROTECTIVE_CONFLICTING_ORDERS audit).
    /// </summary>
    public static bool AggregatedProtectiveSingleWaveEnabled { get; set; } = true;

    /// <summary>
    /// Max wall-clock time after first <see cref="QuantExecutionControlStore.NotifyRecoveredReconnect"/> before
    /// <see cref="QuantExecutionControlStore.EvaluateEscalation"/> may return <see cref="QuantEscalationKind.EscalationRequired"/>
    /// if broker gross still does not match store expected.
    /// </summary>
    public static int QuantEscalationRecoveryStabilizationWindowMs { get; set; } = 60000;

    /// <summary>
    /// Phase 3: when true, <see cref="ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure"/> does not deny solely because
    /// <see cref="JournalParityResult.IsOkOrPendingAlignment"/> is false — parity remains on the snapshot for diagnostics / Tier 2+3.
    /// Default false preserves legacy hard-deny until rollout.
    /// </summary>
    public static bool StructuralLayerPhase3DemoteParityNotOkSubmitDeny { get; set; } = false;

    /// <summary>
    /// Phase 3: when true, structural submit does not deny solely because <see cref="ExecutionSafetyEvaluationRequest.JournalIntegrityOrReconciliationRepairActive"/>
    /// is true (journal/reconciliation repair latch). <see cref="ExecutionSafetyEvaluationRequest.RecoveryExecutionDisallowed"/> is never bypassed.
    /// Default false preserves legacy hard-deny until rollout.
    /// </summary>
    public static bool StructuralLayerPhase3DemoteRepairActiveSubmitDeny { get; set; } = false;

    /// <summary>
    /// When true, <see cref="InstrumentOwnershipLedger"/> receives write calls alongside existing journal/coordinator paths.
    /// Comparison assertions verify ledger snapshot vs existing composite ownership query.
    /// Does not change any read paths — existing stores remain authoritative until proven.
    /// </summary>
    public static bool CanonicalOwnershipLedgerEnabled { get; set; } = false;

    /// <summary>
    /// Wall-clock window (ms) for transient mismatch classification. Broker/ledger qty mismatches younger than
    /// this window are classified TRANSIENT_MISMATCH and not escalated. Aligned with <see cref="PostFillAlignmentWindowMs"/>.
    /// </summary>
    public static int TransientMismatchWindowMs { get; set; } = 5000;

    /// <summary>
    /// Phase 4a shadow: when true, <see cref="UnifiedExecutionAuthority.Evaluate"/> runs in parallel with the
    /// existing gate chain on every order submit. The old path still decides; disagreements are logged as
    /// UEA_SHADOW_DISAGREEMENT. No behavior change.
    /// </summary>
    public static bool UnifiedExecutionAuthorityShadowEnabled { get; set; } = false;

    /// <summary>
    /// Phase 4a2 activation: when true (requires shadow parity proof), <see cref="UnifiedExecutionAuthority.Evaluate"/>
    /// replaces the old gate chain for order submit decisions. The old path is bypassed.
    /// </summary>
    public static bool UnifiedExecutionAuthorityEnabled { get; set; } = false;

    /// <summary>
    /// Phase 5b activation: when true, <see cref="ReconciliationRepairExecutor.ExecuteRepairs"/> is called for
    /// non-stale classifier verdicts. Requires classifier parity proof, orphan restore correctness, and
    /// transfer durability validation before enabling.
    /// </summary>
    public static bool ReconciliationRepairExecutorEnabled { get; set; } = false;

    /// <summary>
    /// Playback audit mode used to force-enable <see cref="ReconciliationRepairExecutorEnabled"/>.
    /// Keep false by default so playback can observe/classify authority drift without adding repair mutations.
    /// Set true only for explicit repair-executor validation runs.
    /// </summary>
    public static bool PlaybackAuditAutoEnableReconciliationRepairExecutor { get; set; } = false;

    /// <summary>
    /// Phase 8: when true, <see cref="ExecutionStructuralLayer"/> derives ownership from
    /// <see cref="InstrumentOwnershipLedger"/> snapshots instead of journal-based position authority.
    /// </summary>
    public static bool StructuralLayerUseLedgerOwnership { get; set; } = false;
}
