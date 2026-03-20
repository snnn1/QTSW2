// Gap 4: Persistent Mismatch Escalation — models and policy thresholds.
// Detects when broker truth, journals, registry, lifecycle, or recovery state fail to converge.
// Bounded detection and escalation only; no retry loops or auto-repair in first pass.

using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>Explicit mismatch categories. Do not collapse into one generic mismatch.</summary>
public enum MismatchType
{
    /// <summary>Broker position/order state indicates exposure or working orders not reflected in local journal/intent state.</summary>
    BROKER_AHEAD,

    /// <summary>Local journal/intent state claims exposure or progress that broker state does not support.</summary>
    JOURNAL_AHEAD,

    /// <summary>Broker quantity and reconstructed local quantity differ (both nonzero).</summary>
    POSITION_QTY_MISMATCH,

    /// <summary>Broker working order exists but local registry/order map does not reliably own or classify it.</summary>
    ORDER_REGISTRY_MISSING,

    /// <summary>Lifecycle or local state says protectives exist or are active, but broker-side protection is missing or contradictory.</summary>
    PROTECTIVE_STATE_DIVERGENCE,

    /// <summary>Execution could not be resolved and mismatch remains beyond acceptable window.</summary>
    UNKNOWN_EXECUTION_PERSISTENT,

    /// <summary>Startup or restart reconstruction could not classify the instrument into a clean stable state.</summary>
    RESTART_RECONCILIATION_UNRESOLVED,

    /// <summary>Lifecycle state and broker-observed state are materially inconsistent.</summary>
    LIFECYCLE_BROKER_DIVERGENCE,

    /// <summary>Fallback category for severe mismatch that cannot be classified more specifically.</summary>
    UNCLASSIFIED_CRITICAL_MISMATCH
}

/// <summary>Per-instrument mismatch escalation state machine.</summary>
public enum MismatchEscalationState
{
    NONE,
    DETECTED,
    PERSISTENT_MISMATCH,
    FAIL_CLOSED
}

/// <summary>Observation payload supplied by RobotEngine to the coordinator.</summary>
public sealed class MismatchObservation
{
    public string Instrument { get; set; } = "";
    public MismatchType MismatchType { get; set; }
    public bool Present { get; set; }
    public string? Summary { get; set; }
    public int BrokerQty { get; set; }
    public int LocalQty { get; set; }
    public int BrokerWorkingOrderCount { get; set; }
    public int LocalWorkingOrderCount { get; set; }
    public string? LifecycleState { get; set; }
    public string? JournalState { get; set; }
    public DateTimeOffset ObservedUtc { get; set; }
    public string Severity { get; set; } = "CRITICAL";
}

/// <summary>Per-instrument mismatch state (coordinator-owned).</summary>
public sealed class MismatchInstrumentState
{
    public MismatchType MismatchType { get; set; }
    public MismatchEscalationState EscalationState { get; set; }
    public DateTimeOffset FirstDetectedUtc { get; set; }
    public DateTimeOffset LastDetectedUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public int RetryCount { get; set; }
    public bool Blocked { get; set; }
    public string BlockReason { get; set; } = "";
    public int ConsecutiveCleanPassCount { get; set; }
    public DateTimeOffset LastResolutionAttemptUtc { get; set; }
    public long PersistenceMs { get; set; }
    public string? LastSummary { get; set; }
    public bool MismatchStillPresent { get; set; }
}

/// <summary>Policy thresholds for mismatch escalation.</summary>
public static class MismatchEscalationPolicy
{
    public const int MISMATCH_AUDIT_INTERVAL_MS = 5000;
    public const int MISMATCH_PERSISTENT_THRESHOLD_MS = 10000;
    public const int MISMATCH_FAIL_CLOSED_THRESHOLD_MS = 30000;
    public const int MISMATCH_CLEAR_CONSECUTIVE_CLEAN_PASSES = 2;
    public const int MISMATCH_MAX_RETRIES = 3;

    /// <summary>Block reason for persistent mismatch. Distinguishable from protective, queue poison, supervisory.</summary>
    public const string BLOCK_REASON_PERSISTENT_MISMATCH = "PERSISTENT_RECONCILIATION_MISMATCH";
}
