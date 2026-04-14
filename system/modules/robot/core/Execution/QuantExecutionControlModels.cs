using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Per-instrument execution phase for broker-first quant control (single state machine per instrument).
/// Diagnostics (journal, parity, reconciliation) do not add phases — they feed Tier 2/3 only.
/// </summary>
public enum QuantExecutionInstrumentPhase
{
    /// <summary>Broker and expected state agree; trading per business rules.</summary>
    Normal,

    /// <summary>Mapped fill just applied; broker moved first; bounded window for internal lag.</summary>
    PendingAlignment,

    /// <summary>Reconnect/adoption: broker has risk; history may be incomplete.</summary>
    Recovered,

    /// <summary>
    /// Tier-1 escalation durable state: alignment/recovery tolerance elapsed but broker vs store expected still incoherent.
    /// Blocks directional entries; protective stops and flatten remain allowed. Not owned by parity/journal mismatch coordinator.
    /// </summary>
    RecoveryRequired,

    /// <summary>Unmapped fill — hard stop path until operator unlock.</summary>
    UnmappedExecution,

    /// <summary>Manual kill, supervisory halt, catastrophic lock, or hard-flatten aftermath.</summary>
    ExecutionLocked
}

/// <summary>Minimal expected-state view (Tier 1) compared to broker truth for escalation timing.</summary>
public sealed class QuantExpectedInstrumentState
{
    /// <summary>Signed net position the system expects after mapped fills / recognized flattens (single canonical bucket).</summary>
    public int ExpectedSignedNetPosition { get; set; }

    /// <summary>Absolute position magnitude expected (single-leg view).</summary>
    public int ExpectedGrossPositionAbs => System.Math.Abs(ExpectedSignedNetPosition);

    /// <summary>Robot-tagged working orders expected at broker for parity-style checks (optional; 0 if unknown).</summary>
    public int ExpectedWorkingOrderCount { get; set; }

    public DateTimeOffset? LastMappedFillUtc { get; set; }

    /// <summary>End of bounded PENDING_ALIGNMENT window (wall clock).</summary>
    public DateTimeOffset? PendingAlignmentExpiresUtc { get; set; }

    /// <summary>True after reconnect adoption without full history.</summary>
    public bool RecoveryMode { get; set; }

    /// <summary>Wall time when <see cref="QuantExecutionInstrumentPhase.Recovered"/> was first entered (adoption/reconnect).</summary>
    public DateTimeOffset? RecoveryEnteredUtc { get; set; }
}

/// <summary>Immutable snapshot for logs / tests.</summary>
public readonly struct QuantExecutionControlSnapshot
{
    public QuantExecutionInstrumentPhase Phase { get; init; }
    public QuantExpectedInstrumentState Expected { get; init; }
    public string? LockOrUnmappedReason { get; init; }

    /// <summary>Set when <see cref="QuantExecutionInstrumentPhase.RecoveryRequired"/> — last escalation reason from evaluation.</summary>
    public string? RecoveryRequiredReason { get; init; }
}

/// <summary>Outcome of <see cref="QuantExecutionControlStore.EvaluateEscalation"/> — single evaluation, no side effects.</summary>
public enum QuantEscalationKind
{
    /// <summary>No Tier-1 escalation (normal, disabled, terminal phases handled elsewhere, or already coherent).</summary>
    NoAction,

    /// <summary>Mapped-fill alignment window still open; mismatch may still resolve.</summary>
    StillPendingAlignment,

    /// <summary>Recovered/adoption lag window still open.</summary>
    RecoveredLagTolerated,

    /// <summary>Bounded tolerance elapsed and broker vs expected still incoherent — caller should escalate (log, repair, policy flatten, etc.).</summary>
    EscalationRequired
}

/// <summary>Result of escalation evaluation with optional diagnostic reason.</summary>
public readonly struct QuantEscalationResult
{
    public QuantEscalationKind Kind { get; init; }
    public string? Reason { get; init; }
}
