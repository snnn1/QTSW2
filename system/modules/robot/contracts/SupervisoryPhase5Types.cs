// Phase 5: Operational Control and Supervisory Policy.
// Used by Robot.Core (IEA) and RobotCore_For_NinjaTrader.

using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>Instrument-scoped supervisory state.</summary>
public enum SupervisoryState
{
    ACTIVE,
    COOLDOWN,
    SUSPENDED,
    HALTED,
    AWAITING_OPERATOR_ACK,
    DISABLED
}

/// <summary>Severity for supervisory action.</summary>
public enum SupervisorySeverity
{
    LOW,
    MEDIUM,
    HIGH,
    CRITICAL
}

/// <summary>Reasons that trigger supervisory action.</summary>
public enum SupervisoryTriggerReason
{
    REPEATED_RECOVERY_TRIGGERS,
    REPEATED_BOOTSTRAP_HALTS,
    REPEATED_UNOWNED_EXECUTIONS,
    REPEATED_REGISTRY_DIVERGENCE,
    REPEATED_RECONCILIATION_MISMATCH,
    REPEATED_FLATTEN_ACTIONS,
    REPEATED_RECOVERY_HALT,
    REPEATED_CONNECTION_RECOVERY,
    MANUAL_OPERATOR_SUSPEND,
    MANUAL_OPERATOR_HALT,
    MANUAL_OPERATOR_DISABLE,
    GLOBAL_KILL_SWITCH,
    INSTRUMENT_KILL_SWITCH,
    THRESHOLD_EXCEEDED,
    COOLDOWN_ESCALATED,
    HALT_ESCALATED,
    IEA_ENQUEUE_FAILURE
}
