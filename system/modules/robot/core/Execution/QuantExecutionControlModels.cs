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

    /// <summary>Last time a robot working order submit was handed to the broker.</summary>
    public DateTimeOffset? LastWorkingOrderSubmitUtc { get; set; }

    /// <summary>Expected robot working-order count during the submit/register convergence window.</summary>
    public int? PendingWorkingOrderSubmitCount { get; set; }

    public DateTimeOffset? LastMappedFillUtc { get; set; }

    /// <summary>
    /// Set when the broker order lifecycle has observed a fill before the execution/journal callback
    /// may have finished mapping it. This is a bounded callback-ordering suppressor.
    /// </summary>
    public DateTimeOffset? LastBrokerExecutionCallbackUtc { get; set; }

    /// <summary>End of the broker-callback ahead-of-journal window.</summary>
    public DateTimeOffset? BrokerExecutionCallbackExpiresUtc { get; set; }

    /// <summary>Diagnostic role for the last broker callback that armed callback alignment.</summary>
    public string? LastBrokerExecutionCallbackRole { get; set; }

    /// <summary>Diagnostic lifecycle state for the last broker callback that armed callback alignment.</summary>
    public string? LastBrokerExecutionCallbackState { get; set; }

    /// <summary>End of bounded PENDING_ALIGNMENT window (wall clock).</summary>
    public DateTimeOffset? PendingAlignmentExpiresUtc { get; set; }

    /// <summary>
    /// Set by <see cref="QuantExecutionControlStore.NotifyProtectiveStopSubmitted"/> after a protective stop submit succeeds.
    /// <see cref="QuantExecutionInstrumentPhase.PendingAlignment"/> can begin at mapped fill time before submit; protective coverage
    /// convergence requires this timestamp &gt;= <see cref="LastMappedFillUtc"/> for the current episode.
    /// </summary>
    public DateTimeOffset? LastProtectiveStopSubmitUtc { get; set; }

    /// <summary>
    /// Set when an already-working protective set must be resized after another mapped fill.
    /// The protective audit uses this as a bounded suppressor while the strategy-thread resize
    /// command cancels/recreates the broker orders.
    /// </summary>
    public DateTimeOffset? LastProtectiveResizePendingUtc { get; set; }

    /// <summary>Absolute protective quantity expected after the pending resize completes.</summary>
    public int? PendingProtectiveResizeQty { get; set; }

    /// <summary>
    /// Set when an existing protective OCO pair is intentionally cancel/replaced, for example a BE move
    /// where NT rejects/reverts in-place OCO stop changes.
    /// </summary>
    public DateTimeOffset? LastProtectiveCancelReplacePendingUtc { get; set; }

    /// <summary>Expected broker working-order count while the protective cancel/replace converges.</summary>
    public int? PendingProtectiveCancelReplaceWorkingCount { get; set; }

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
