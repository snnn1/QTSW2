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
    RiskCoverage,
    RiskReducing,
    Emergency
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
    public IReadOnlyList<GateEvaluation> AuditTrail { get; init; } = Array.Empty<GateEvaluation>();
    public DateTimeOffset EvaluatedUtc { get; init; }

    public static AuthorityDecision Allow(InstrumentOwnershipSnapshot? snapshot, IReadOnlyList<GateEvaluation> trail, DateTimeOffset utc) =>
        new() { Allowed = true, OwnershipSnapshot = snapshot, AuditTrail = trail, EvaluatedUtc = utc };

    public static AuthorityDecision Deny(string gate, string reason, InstrumentOwnershipSnapshot? snapshot,
        IReadOnlyList<GateEvaluation> trail, DateTimeOffset utc) =>
        new() { Allowed = false, DenyGate = gate, DenyReason = reason, OwnershipSnapshot = snapshot, AuditTrail = trail, EvaluatedUtc = utc };
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
    public Func<string, string?, bool>? InstrumentFrozenOrEpaBlocked { get; init; }
    public Func<string, string?, DateTimeOffset, ExecutionSafetyEvaluationRequest>? BuildSafetyRequest { get; init; }
    public InstrumentOwnershipLedger? OwnershipLedger { get; init; }
    public string? AccountName { get; init; }
}
