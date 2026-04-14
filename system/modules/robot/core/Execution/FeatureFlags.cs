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
    /// Phase A / Phase 3: when false (default), <see cref="MismatchEscalationCoordinator"/> does not publish mismatch
    /// execution block authority and <see cref="MismatchEscalationCoordinator.IsInstrumentBlockedByMismatch"/> is always false —
    /// mismatch stays observational (logs, metrics, gate events). Do not re-enable submit-time denial here unless proven necessary;
    /// Tier-1 quant escalation + structural Phase 3 demotions own submit policy evolution.
    /// </summary>
    public static bool ControlPlaneMismatchExecutionBlockAuthority { get; set; } = false;

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
}
