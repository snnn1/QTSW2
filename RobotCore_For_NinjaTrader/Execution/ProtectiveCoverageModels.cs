// Gap 3: Protective Coverage Audit — models and policy thresholds.
// Broker-truth based. Do not rely on journal assumptions.

using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>Audit result classification. Explicit categories, not collapsed.</summary>
public enum ProtectiveAuditStatus
{
    /// <summary>No issues. Full stop coverage verified.</summary>
    PROTECTIVE_OK,

    /// <summary>Non-flat broker position exists but no valid stop found.</summary>
    PROTECTIVE_MISSING_STOP,

    /// <summary>Stop exists but quantity does not fully cover current broker exposure.</summary>
    PROTECTIVE_STOP_QTY_MISMATCH,

    /// <summary>Stop exists but price is invalid, nonsensical, or not protective.</summary>
    PROTECTIVE_STOP_PRICE_INVALID,

    /// <summary>Position has stop but target missing. Lower severity than missing stop.</summary>
    PROTECTIVE_MISSING_TARGET,

    /// <summary>Target exists but does not match open exposure.</summary>
    PROTECTIVE_TARGET_QTY_MISMATCH,

    /// <summary>Multiple protective orders exist in contradictory or ambiguous configuration.</summary>
    PROTECTIVE_CONFLICTING_ORDERS,

    /// <summary>Broker position exists but audit cannot confidently associate coverage or ownership.</summary>
    PROTECTIVE_UNRESOLVED_POSITION,

    /// <summary>A known recovery workflow is running; suppress duplicate remediation.</summary>
    PROTECTIVE_RECOVERY_IN_PROGRESS,

    /// <summary>Emergency risk action already underway.</summary>
    PROTECTIVE_FLATTEN_IN_PROGRESS
}

/// <summary>Per-instrument protective recovery state machine.</summary>
public enum ProtectiveRecoveryState
{
    NONE,
    DETECTED,
    CORRECTIVE_SUBMITTING,
    AWAITING_CONFIRMATION,
    ESCALATE_TO_FLATTEN,
    FLATTEN_IN_PROGRESS,
    LOCKED_FAIL_CLOSED
}

/// <summary>Result of a single protective coverage audit for one instrument.</summary>
public sealed class ProtectiveAuditResult
{
    public string Instrument { get; set; } = "";
    public ProtectiveAuditStatus Status { get; set; }
    public int BrokerPositionQty { get; set; }
    public string BrokerDirection { get; set; } = ""; // "Long" or "Short"
    public int StopQty { get; set; }
    public int TargetQty { get; set; }
    public decimal? StopPrice { get; set; }
    public decimal? TargetPrice { get; set; }
    public string? IntentId { get; set; }
    public ProtectiveRecoveryState RecoveryState { get; set; }
    public int AttemptCount { get; set; }
    public bool InstrumentBlocked { get; set; }
    public bool FlattenInProgress { get; set; }
    public bool RecoveryInProgress { get; set; }
    public DateTimeOffset AuditUtc { get; set; }
    public string? Detail { get; set; }
}

/// <summary>Per-instrument protective recovery state (coordinator-owned).</summary>
public sealed class ProtectiveInstrumentState
{
    public ProtectiveRecoveryState RecoveryState { get; set; }
    public DateTimeOffset FirstDetectedUtc { get; set; }
    public DateTimeOffset LastDetectedUtc { get; set; }
    public ProtectiveAuditStatus LastAuditStatus { get; set; }
    public int AttemptCount { get; set; }
    public bool Blocked { get; set; }
    public string BlockReason { get; set; } = "";
    public int ConsecutiveCleanPassCount { get; set; }
    /// <summary>Phase 4: When in AWAITING_CONFIRMATION, timeout for escalation if still critical.</summary>
    public DateTimeOffset AwaitingConfirmationUntilUtc { get; set; }
    /// <summary>Phase 5: True once emergency flatten has been triggered (prevents retrigger).</summary>
    public bool EmergencyFlattenTriggered { get; set; }
}

/// <summary>Phase 4: Request for corrective stop submission. Engine derives stop price from journal.</summary>
public sealed class ProtectiveCorrectiveRequest
{
    public string Instrument { get; set; } = "";
    public int BrokerPositionQty { get; set; }
    public string BrokerDirection { get; set; } = "";
    public ProtectiveAuditStatus Status { get; set; }
    public DateTimeOffset AuditUtc { get; set; }
}

/// <summary>Phase 4: Result of corrective submission attempt.</summary>
public sealed class ProtectiveCorrectiveResult
{
    public bool Submitted { get; set; }
    public string? IntentId { get; set; }
    public string? FailureReason { get; set; }
}

/// <summary>Policy thresholds for protective audit. Config-backed constants.</summary>
public static class ProtectiveAuditPolicy
{
    public const int PROTECTIVE_AUDIT_INTERVAL_ACTIVE_MS = 1000;
    public const int PROTECTIVE_AUDIT_INTERVAL_INACTIVE_MS = 5000;
    public const int PROTECTIVE_RECOVERY_CONFIRM_MS = 2500;
    /// <summary>Phase 4: Timeout before escalating to ESCALATE_TO_FLATTEN when corrective submitted but still critical.</summary>
    public const int PROTECTIVE_AWAITING_CONFIRMATION_MS = 2500;
    public const int PROTECTIVE_MAX_CORRECTIVE_ATTEMPTS = 2;
    public const int PROTECTIVE_MISSING_STOP_FLATTEN_TIMEOUT_MS = 5000;
    public const int PROTECTIVE_MISSING_TARGET_WARN_TIMEOUT_MS = 10000;
    public const bool PROTECTIVE_BLOCK_ON_FIRST_CRITICAL_FAILURE = true;

    /// <summary>Minimum stop price sanity: stop must be protective (below entry for long, above for short).</summary>
    public const decimal MIN_STOP_PRICE = 0.01m;

    /// <summary>Consecutive clean passes required before clearing protective block.</summary>
    public const int PROTECTIVE_CLEAR_CONSECUTIVE_CLEAN_PASSES = 2;

    /// <summary>Block reason for protective failure. Distinguishable from queue poison, IEA timeout, etc.</summary>
    public const string BLOCK_REASON_PROTECTIVE_FAILURE = "PROTECTIVE_COVERAGE_BROKEN";
}
