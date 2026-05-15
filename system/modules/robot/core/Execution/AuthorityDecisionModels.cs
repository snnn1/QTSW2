using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Classifies the intent behind a broker order submission for the <see cref="UnifiedExecutionAuthority"/>.
/// Replaces ad-hoc boolean bypass parameters (e.g. <c>skipMismatchExecutionBlock</c>).
/// </summary>
public enum SubmitIntent
{
    RiskIncreasing,
    OpeningEntry,
    RiskCoverage,
    RiskReducing,
    Emergency
}

/// <summary>
/// Non-submit execution decisions that are being consolidated behind the same authority surface.
/// Phase 1 uses this as a read-only/shadow contract for legacy lifecycle paths.
/// </summary>
public enum ExecutionAuthorityAction
{
    EntrySubmit,
    TerminalCommit,
    SessionCloseGlobalSweep,
    Flatten,
    CancelSubmit,
    MarketReentry,
    ProtectiveSubmit,
    ProtectiveBlockCreate,
    ProtectiveBlockClear,
    LatchCreate,
    MismatchRelease,
    JournalCompleteBrokerFlat,
    LatchClear,
    LatchClearExplicitOperator,
    ShutdownSafeVerdict
}

/// <summary>
/// Request for central lifecycle/session decisions that historically lived outside UEA.
/// Keep this facts-only: callers sample state once, then ask authority for the decision.
/// </summary>
public sealed class ExecutionAuthorityActionEvaluationRequest
{
    public ExecutionAuthorityAction Action { get; init; }
    public string Source { get; init; } = "";
    public string Instrument { get; init; } = "";
    public string IntentId { get; init; } = "";
    public string? Stream { get; init; }
    public DateTimeOffset UtcNow { get; init; }
    public string? CommitReason { get; init; }
    public string? EventType { get; init; }

    public bool HasOpenLifecycleExposureOrPendingReentry { get; init; }
    public bool HasCompletedTradeForCurrentStream { get; init; }
    public bool IsIntentionalOpenLifecycleTerminalCommit { get; init; }
    public bool HasActiveTrackedReentry { get; init; }
    public bool HasActiveLifecycleEvidence { get; init; }
    public bool HasRobotEvidence { get; init; }
    public DateTimeOffset? SessionCloseSweepAnchorUtc { get; init; }
    public long? SessionCloseSweepWindowAgeMs { get; init; }
    public bool SessionCloseSweepWindowFresh { get; init; } = true;

    public string DurableLatchReason { get; init; } = "";
    public int AccountQty { get; init; }
    public int BrokerAbsQty { get; init; }
    public int BrokerWorkingOrderCount { get; init; }
    public int JournalOpenQty { get; init; }
    public int OwnershipOpenQty { get; init; }
    public int OwnershipActiveSlotCount { get; init; }
    public int OwnershipOrphanSlotCount { get; init; }
    public int RealOpenQty { get; init; }
    public int RecoveryOpenQty { get; init; }
    public bool HasSupervisoryBlock { get; init; }
    public bool HasProtectiveBlock { get; init; }
    public bool HasMismatchBlock { get; init; }
    public bool SnapshotSufficient { get; init; } = true;
    public bool? ReleaseValidatorReady { get; init; }
    public int PendingExecutionWorkload { get; init; }
    public int ActiveIntentCount { get; init; }
    public int NonTerminalStreamCount { get; init; }

    public ExecutionAuthorityFrame? AuthorityFrame { get; init; }
}

/// <summary>
/// Decision for non-submit authority actions. This is intentionally small so legacy paths can
/// adopt it one by one before old decision owners are removed.
/// </summary>
public sealed class ExecutionAuthorityActionDecision
{
    public bool Allowed { get; init; }
    public string GateName { get; init; } = "";
    public string? DenyReason { get; init; }
    public string? Detail { get; init; }
    public ExecutionAuthorityFrame? AuthorityFrame { get; init; }
    public DateTimeOffset EvaluatedUtc { get; init; }

    public static ExecutionAuthorityActionDecision Allow(string gateName, ExecutionAuthorityFrame? frame, DateTimeOffset utc, string? detail = null) =>
        new() { Allowed = true, GateName = gateName, AuthorityFrame = frame, EvaluatedUtc = utc, Detail = detail };

    public static ExecutionAuthorityActionDecision Deny(string gateName, string reason, ExecutionAuthorityFrame? frame, DateTimeOffset utc, string? detail = null) =>
        new() { Allowed = false, GateName = gateName, DenyReason = reason, AuthorityFrame = frame, EvaluatedUtc = utc, Detail = detail };
}

/// <summary>
/// Immutable audit frame for one execution-authority decision. Gates may still call existing
/// evaluators, but every log for the decision can now point back to the same sampled facts.
/// </summary>
public sealed class ExecutionAuthorityFrame
{
    public string FrameId { get; init; } = "";
    public string Source { get; init; } = "";
    public string Account { get; init; } = "";
    public string Instrument { get; init; } = "";
    public string? CanonicalInstrument { get; init; }
    public string? ExecutionInstrumentKey { get; init; }
    public string TradingDate { get; init; } = "";
    public string StreamId { get; init; } = "";
    public string IntentId { get; init; } = "";
    public string OrderRole { get; init; } = "";
    public string SubmitPath { get; init; } = "";
    public string ExecutionMode { get; init; } = "";
    public DateTimeOffset DecisionUtc { get; init; }
    public DateTimeOffset FrameCreatedUtc { get; init; }
    public DateTimeOffset? BrokerSnapshotCapturedUtc { get; init; }
    public long? AccountSnapshotAgeMs { get; init; }
    public string? SnapshotError { get; init; }

    public int BrokerPositionQty { get; init; }
    public int BrokerWorkingOrderCount { get; init; }
    public int BrokerWorkingOrdersCount { get; init; }
    public int BrokerStopQty { get; init; }
    public int BrokerTargetQty { get; init; }
    public IReadOnlyList<string> BrokerOrderIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BrokerOrderTags { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BrokerEntryOrderIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BrokerStopOrderIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BrokerTargetOrderIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BrokerFlattenOrderIds { get; init; } = Array.Empty<string>();
    public int JournalOpenQty { get; init; }
    public long JournalOpenIntentSetHash { get; init; }
    public int RealOpenQty { get; init; }
    public int RecoveryOpenQty { get; init; }
    public string AuthorityState { get; init; } = "";

    public bool UseInstrumentExecutionAuthority { get; init; }
    public int IeaOwnedPlusAdoptedWorking { get; init; }
    public int IeaRegistryWorkingCount { get; init; }
    public int IeaMismatchTrustedWorkingCount { get; init; }
    public bool IeaSupervisoryBlock { get; init; }
    public bool RecoveryExecutionDisallowed { get; init; }
    public bool JournalIntegrityOrReconciliationRepairActive { get; init; }
    public bool PreflightAuthoritySampled { get; init; }
    public bool PreflightGlobalKillSwitchActive { get; init; }
    public bool PreflightMismatchExecutionBlocked { get; init; }
    public bool? PreflightMismatchExecutionBlockedForSubmit { get; init; }
    public bool PreflightInstrumentFrozenOrEpaBlocked { get; init; }
    public string? PreflightInstrumentFrozenOrEpaBlockReason { get; init; }

    public string? LedgerAccountName { get; init; }
    public long? LedgerOwnershipVersion { get; init; }
    public int? LedgerSignedNetQty { get; init; }
    public int? LedgerActiveSlotCount { get; init; }
    public int? LedgerOrphanSlotCount { get; init; }
    public int OwnershipOpenQty { get; init; }
    public int OwnershipSignedQty { get; init; }
    public int OwnershipActiveSlots { get; init; }
    public int OwnershipOrphanSlots { get; init; }
    public InstrumentOwnershipSnapshot? OwnershipSnapshot { get; init; }

    public string StreamLifecycleState { get; init; } = "";
    public bool StreamCommitted { get; init; }
    public int NonTerminalStreamsCount { get; init; }
    public int ActiveIntentsCount { get; init; }
    public IReadOnlyList<string> ActiveIntentIds { get; init; } = Array.Empty<string>();
    public string ActiveReentryState { get; init; } = "";
    public DateTimeOffset? ScheduledExitTimeUtc { get; init; }

    public string ProtectiveCoverageState { get; init; } = "";
    public int ProtectiveMissingQty { get; init; }
    public bool ProtectivePending { get; init; }
    public string QecPhase { get; init; } = "";
    public bool QecPendingAlignment { get; init; }
    public int QecRecoveryOpenQty { get; init; }

    public bool DurableLatchActive { get; init; }
    public string DurableLatchReason { get; init; } = "";
    public bool MismatchBlockActive { get; init; }
    public string MismatchBlockReason { get; init; } = "";
    public string StructuralDeny { get; init; } = "";
    public bool OverlayLock { get; init; }
    public bool KillSwitchActive { get; init; }
    public bool TimetableAllowed { get; init; }
    public string SessionCloseState { get; init; } = "";
    public DateTimeOffset? SessionCloseSweepAnchorUtc { get; init; }
    public long? SessionCloseSweepWindowAgeMs { get; init; }
    public bool SessionCloseSweepWindowFresh { get; init; } = true;

    public bool IsPlayback { get; init; }
    public bool IsMultiDayScenario { get; init; }
    public string PlaybackScenarioId { get; init; } = "";
    public string ProofLevel { get; init; } = "";
    public string RuntimeSignatureHash { get; init; } = "";

    public bool IsCleanFlat { get; init; }
    public bool HasTrackedExposure { get; init; }
    public bool HasUntrackedExposure { get; init; }
    public bool HasProtectedExposure { get; init; }
    public bool HasContradiction { get; init; }
    public IReadOnlyList<string> FailedPredicates { get; init; } = Array.Empty<string>();

    public static string CreateFrameId(DateTimeOffset utcNow)
    {
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
        return $"af-{utcNow.ToUnixTimeMilliseconds()}-{suffix}";
    }
}

/// <summary>
/// Result from <see cref="UnifiedExecutionAuthority.Evaluate"/>. Contains the allow/deny decision,
/// the gate that denied (if any), the exact ownership snapshot used, and per-gate audit trail.
/// </summary>
public sealed class AuthorityDecision
{
    public bool Allowed { get; init; }
    public string? DenyGate { get; init; }
    public string? DenyReason { get; init; }
    public InstrumentOwnershipSnapshot? OwnershipSnapshot { get; init; }
    public ExecutionAuthorityFrame? AuthorityFrame { get; init; }
    public IReadOnlyList<GateEvaluation> AuditTrail { get; init; } = Array.Empty<GateEvaluation>();
    public DateTimeOffset EvaluatedUtc { get; init; }

    public static AuthorityDecision Allow(InstrumentOwnershipSnapshot? snapshot, ExecutionAuthorityFrame? frame,
        IReadOnlyList<GateEvaluation> trail, DateTimeOffset utc) =>
        new() { Allowed = true, OwnershipSnapshot = snapshot, AuthorityFrame = frame, AuditTrail = trail, EvaluatedUtc = utc };

    public static AuthorityDecision Deny(string gate, string reason, InstrumentOwnershipSnapshot? snapshot, ExecutionAuthorityFrame? frame,
        IReadOnlyList<GateEvaluation> trail, DateTimeOffset utc) =>
        new() { Allowed = false, DenyGate = gate, DenyReason = reason, OwnershipSnapshot = snapshot, AuthorityFrame = frame, AuditTrail = trail, EvaluatedUtc = utc };
}

/// <summary>
/// Per-gate pass/fail record for the <see cref="AuthorityDecision"/> audit trail.
/// </summary>
public sealed class GateEvaluation
{
    public string GateName { get; init; } = "";
    public bool Passed { get; init; }
    public string? DenyReason { get; init; }
    public string? Detail { get; init; }
}

/// <summary>
/// Request input for <see cref="UnifiedExecutionAuthority.Evaluate"/>.
/// </summary>
public sealed class AuthorityEvaluationRequest
{
    public string Instrument { get; init; } = "";
    public string? CanonicalInstrument { get; init; }
    public string IntentId { get; init; } = "";
    public SubmitIntent SubmitIntent { get; init; }
    public string SubmitPath { get; init; } = "";
    public DateTimeOffset UtcNow { get; init; }

    public Func<bool>? GlobalKillSwitchActive { get; init; }
    public Func<string, bool>? MismatchExecutionBlocked { get; init; }
    public Func<string, string?, bool>? MismatchExecutionBlockedForSubmit { get; init; }
    public Func<string, string?, bool>? InstrumentFrozenOrEpaBlocked { get; init; }
    public Func<string, string?, string?>? InstrumentFrozenOrEpaBlockReason { get; init; }
    public bool? PreflightGlobalKillSwitchActive { get; init; }
    public bool? PreflightMismatchExecutionBlocked { get; init; }
    public bool? PreflightMismatchExecutionBlockedForSubmit { get; init; }
    public bool? PreflightInstrumentFrozenOrEpaBlocked { get; init; }
    public string? PreflightInstrumentFrozenOrEpaBlockReason { get; init; }
    public ExecutionSafetyEvaluationRequest? PrebuiltSafetyRequest { get; init; }
    public ExecutionAuthorityFrame? AuthorityFrame { get; init; }
    public Func<string, string?, DateTimeOffset, ExecutionSafetyEvaluationRequest>? BuildSafetyRequest { get; init; }
    public InstrumentOwnershipLedger? OwnershipLedger { get; init; }
    public string? AccountName { get; init; }
}
