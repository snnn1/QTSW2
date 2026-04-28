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
/// Immutable audit frame for one execution-authority decision. Gates may still call existing
/// evaluators, but every log for the decision can now point back to the same sampled facts.
/// </summary>
public sealed class ExecutionAuthorityFrame
{
    public string FrameId { get; init; } = "";
    public string Source { get; init; } = "";
    public string Instrument { get; init; } = "";
    public string? CanonicalInstrument { get; init; }
    public string? ExecutionInstrumentKey { get; init; }
    public string IntentId { get; init; } = "";
    public string SubmitPath { get; init; } = "";
    public DateTimeOffset DecisionUtc { get; init; }
    public DateTimeOffset FrameCreatedUtc { get; init; }
    public DateTimeOffset? BrokerSnapshotCapturedUtc { get; init; }
    public string? SnapshotError { get; init; }

    public int BrokerPositionQty { get; init; }
    public int BrokerWorkingOrderCount { get; init; }
    public int JournalOpenQty { get; init; }
    public long JournalOpenIntentSetHash { get; init; }
    public int RealOpenQty { get; init; }
    public int RecoveryOpenQty { get; init; }
    public string AuthorityState { get; init; } = "";

    public bool UseInstrumentExecutionAuthority { get; init; }
    public int IeaOwnedPlusAdoptedWorking { get; init; }
    public bool RecoveryExecutionDisallowed { get; init; }
    public bool JournalIntegrityOrReconciliationRepairActive { get; init; }
    public bool PreflightAuthoritySampled { get; init; }
    public bool PreflightGlobalKillSwitchActive { get; init; }
    public bool PreflightMismatchExecutionBlocked { get; init; }
    public bool? PreflightMismatchExecutionBlockedForSubmit { get; init; }
    public bool PreflightInstrumentFrozenOrEpaBlocked { get; init; }

    public string? LedgerAccountName { get; init; }
    public long? LedgerOwnershipVersion { get; init; }
    public int? LedgerSignedNetQty { get; init; }
    public int? LedgerActiveSlotCount { get; init; }
    public int? LedgerOrphanSlotCount { get; init; }
    public InstrumentOwnershipSnapshot? OwnershipSnapshot { get; init; }

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
    public bool? PreflightGlobalKillSwitchActive { get; init; }
    public bool? PreflightMismatchExecutionBlocked { get; init; }
    public bool? PreflightMismatchExecutionBlockedForSubmit { get; init; }
    public bool? PreflightInstrumentFrozenOrEpaBlocked { get; init; }
    public ExecutionSafetyEvaluationRequest? PrebuiltSafetyRequest { get; init; }
    public ExecutionAuthorityFrame? AuthorityFrame { get; init; }
    public Func<string, string?, DateTimeOffset, ExecutionSafetyEvaluationRequest>? BuildSafetyRequest { get; init; }
    public InstrumentOwnershipLedger? OwnershipLedger { get; init; }
    public string? AccountName { get; init; }
}
