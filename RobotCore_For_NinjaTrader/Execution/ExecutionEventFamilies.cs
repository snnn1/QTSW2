// Gap 5: Canonical event families and event type constants.
// Maps event types to families for validation and replay.

using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Canonical event type constants. Use these for event_type field.
/// </summary>
public static class ExecutionEventTypes
{
    // COMMAND family
    public const string COMMAND_RECEIVED = "COMMAND_RECEIVED";
    public const string COMMAND_DISPATCHED = "COMMAND_DISPATCHED";
    public const string COMMAND_COMPLETED = "COMMAND_COMPLETED";
    public const string COMMAND_REJECTED = "COMMAND_REJECTED";
    public const string COMMAND_ERROR = "COMMAND_ERROR";
    public const string COMMAND_SKIPPED = "COMMAND_SKIPPED";

    // ORDER family
    public const string ORDER_REGISTERED = "ORDER_REGISTERED";
    public const string ORDER_UPDATE_OBSERVED = "ORDER_UPDATE_OBSERVED";
    public const string ORDER_CANCEL_REQUESTED = "ORDER_CANCEL_REQUESTED";
    public const string ORDER_CANCELLED = "ORDER_CANCELLED";
    public const string ORDER_REJECTED = "ORDER_REJECTED";

    // EXECUTION family
    public const string EXECUTION_OBSERVED = "EXECUTION_OBSERVED";
    public const string EXECUTION_DEFERRED = "EXECUTION_DEFERRED";
    public const string EXECUTION_RESOLVED = "EXECUTION_RESOLVED";
    public const string EXECUTION_DEDUPLICATED = "EXECUTION_DEDUPLICATED";
    public const string EXECUTION_LATE_FILL = "EXECUTION_LATE_FILL";
    public const string EXECUTION_UNRESOLVED_TIMEOUT = "EXECUTION_UNRESOLVED_TIMEOUT";

    // LIFECYCLE family
    public const string LIFECYCLE_TRANSITIONED = "LIFECYCLE_TRANSITIONED";

    // PROTECTIVE family
    public const string PROTECTIVE_AUDIT_RESULT = "PROTECTIVE_AUDIT_RESULT";
    public const string PROTECTIVE_RECOVERY_STARTED = "PROTECTIVE_RECOVERY_STARTED";
    public const string PROTECTIVE_RECOVERY_SUBMITTED = "PROTECTIVE_RECOVERY_SUBMITTED";
    public const string PROTECTIVE_RECOVERY_CONFIRMED = "PROTECTIVE_RECOVERY_CONFIRMED";
    public const string PROTECTIVE_RECOVERY_FAILED = "PROTECTIVE_RECOVERY_FAILED";
    public const string PROTECTIVE_EMERGENCY_FLATTEN_TRIGGERED = "PROTECTIVE_EMERGENCY_FLATTEN_TRIGGERED";
    public const string PROTECTIVE_FLATTEN_COMPLETED = "PROTECTIVE_FLATTEN_COMPLETED";
    public const string PROTECTIVE_INSTRUMENT_BLOCKED = "PROTECTIVE_INSTRUMENT_BLOCKED";

    // MISMATCH family
    public const string MISMATCH_DETECTED = "MISMATCH_DETECTED";
    public const string MISMATCH_PERSISTENT = "MISMATCH_PERSISTENT";
    public const string MISMATCH_FAIL_CLOSED = "MISMATCH_FAIL_CLOSED";
    public const string MISMATCH_CLEARED = "MISMATCH_CLEARED";
    public const string MISMATCH_BLOCKED = "MISMATCH_BLOCKED";

    // SUPERVISORY family
    public const string QUEUE_POISON_DETECTED = "QUEUE_POISON_DETECTED";
    public const string COMMAND_STALLED = "COMMAND_STALLED";
    public const string INSTRUMENT_FROZEN = "INSTRUMENT_FROZEN";
    public const string SUPERVISOR_STANDDOWN = "SUPERVISOR_STANDDOWN";

    // RECONCILIATION family
    public const string RECONCILIATION_CLASSIFIED = "RECONCILIATION_CLASSIFIED";
    public const string RECONCILIATION_BROKER_AHEAD = "RECONCILIATION_BROKER_AHEAD";
    public const string RECONCILIATION_JOURNAL_AHEAD = "RECONCILIATION_JOURNAL_AHEAD";
    public const string RECONCILIATION_POSITION_QTY_MISMATCH = "RECONCILIATION_POSITION_QTY_MISMATCH";
    public const string RECONCILIATION_REGISTRY_MISSING = "RECONCILIATION_REGISTRY_MISSING";

    // TERMINAL family
    public const string INTENT_TERMINALIZED = "INTENT_TERMINALIZED";
    public const string POSITION_FLATTENED = "POSITION_FLATTENED";
    public const string SESSION_FORCED_FLATTENED = "SESSION_FORCED_FLATTENED";
}

/// <summary>
/// Maps event types to event families for validation.
/// </summary>
public static class ExecutionEventFamilies
{
    private static readonly Dictionary<string, ExecutionEventFamily> _typeToFamily = new Dictionary<string, ExecutionEventFamily>(StringComparer.OrdinalIgnoreCase)
    {
        [ExecutionEventTypes.COMMAND_RECEIVED] = ExecutionEventFamily.COMMAND,
        [ExecutionEventTypes.COMMAND_DISPATCHED] = ExecutionEventFamily.COMMAND,
        [ExecutionEventTypes.COMMAND_COMPLETED] = ExecutionEventFamily.COMMAND,
        [ExecutionEventTypes.COMMAND_REJECTED] = ExecutionEventFamily.COMMAND,
        [ExecutionEventTypes.COMMAND_ERROR] = ExecutionEventFamily.COMMAND,
        [ExecutionEventTypes.COMMAND_SKIPPED] = ExecutionEventFamily.COMMAND,
        [ExecutionEventTypes.ORDER_REGISTERED] = ExecutionEventFamily.ORDER,
        [ExecutionEventTypes.ORDER_UPDATE_OBSERVED] = ExecutionEventFamily.ORDER,
        [ExecutionEventTypes.ORDER_CANCEL_REQUESTED] = ExecutionEventFamily.ORDER,
        [ExecutionEventTypes.ORDER_CANCELLED] = ExecutionEventFamily.ORDER,
        [ExecutionEventTypes.ORDER_REJECTED] = ExecutionEventFamily.ORDER,
        [ExecutionEventTypes.EXECUTION_OBSERVED] = ExecutionEventFamily.EXECUTION,
        [ExecutionEventTypes.EXECUTION_DEFERRED] = ExecutionEventFamily.EXECUTION,
        [ExecutionEventTypes.EXECUTION_RESOLVED] = ExecutionEventFamily.EXECUTION,
        [ExecutionEventTypes.EXECUTION_DEDUPLICATED] = ExecutionEventFamily.EXECUTION,
        [ExecutionEventTypes.EXECUTION_LATE_FILL] = ExecutionEventFamily.EXECUTION,
        [ExecutionEventTypes.EXECUTION_UNRESOLVED_TIMEOUT] = ExecutionEventFamily.EXECUTION,
        [ExecutionEventTypes.LIFECYCLE_TRANSITIONED] = ExecutionEventFamily.LIFECYCLE,
        [ExecutionEventTypes.PROTECTIVE_AUDIT_RESULT] = ExecutionEventFamily.PROTECTIVE,
        [ExecutionEventTypes.PROTECTIVE_RECOVERY_STARTED] = ExecutionEventFamily.PROTECTIVE,
        [ExecutionEventTypes.PROTECTIVE_RECOVERY_SUBMITTED] = ExecutionEventFamily.PROTECTIVE,
        [ExecutionEventTypes.PROTECTIVE_RECOVERY_CONFIRMED] = ExecutionEventFamily.PROTECTIVE,
        [ExecutionEventTypes.PROTECTIVE_RECOVERY_FAILED] = ExecutionEventFamily.PROTECTIVE,
        [ExecutionEventTypes.PROTECTIVE_EMERGENCY_FLATTEN_TRIGGERED] = ExecutionEventFamily.PROTECTIVE,
        [ExecutionEventTypes.PROTECTIVE_FLATTEN_COMPLETED] = ExecutionEventFamily.PROTECTIVE,
        [ExecutionEventTypes.PROTECTIVE_INSTRUMENT_BLOCKED] = ExecutionEventFamily.PROTECTIVE,
        [ExecutionEventTypes.MISMATCH_DETECTED] = ExecutionEventFamily.MISMATCH,
        [ExecutionEventTypes.MISMATCH_PERSISTENT] = ExecutionEventFamily.MISMATCH,
        [ExecutionEventTypes.MISMATCH_FAIL_CLOSED] = ExecutionEventFamily.MISMATCH,
        [ExecutionEventTypes.MISMATCH_CLEARED] = ExecutionEventFamily.MISMATCH,
        [ExecutionEventTypes.MISMATCH_BLOCKED] = ExecutionEventFamily.MISMATCH,
        [ExecutionEventTypes.QUEUE_POISON_DETECTED] = ExecutionEventFamily.SUPERVISORY,
        [ExecutionEventTypes.COMMAND_STALLED] = ExecutionEventFamily.SUPERVISORY,
        [ExecutionEventTypes.INSTRUMENT_FROZEN] = ExecutionEventFamily.SUPERVISORY,
        [ExecutionEventTypes.SUPERVISOR_STANDDOWN] = ExecutionEventFamily.SUPERVISORY,
        [ExecutionEventTypes.RECONCILIATION_CLASSIFIED] = ExecutionEventFamily.RECONCILIATION,
        [ExecutionEventTypes.RECONCILIATION_BROKER_AHEAD] = ExecutionEventFamily.RECONCILIATION,
        [ExecutionEventTypes.RECONCILIATION_JOURNAL_AHEAD] = ExecutionEventFamily.RECONCILIATION,
        [ExecutionEventTypes.RECONCILIATION_POSITION_QTY_MISMATCH] = ExecutionEventFamily.RECONCILIATION,
        [ExecutionEventTypes.RECONCILIATION_REGISTRY_MISSING] = ExecutionEventFamily.RECONCILIATION,
        [ExecutionEventTypes.INTENT_TERMINALIZED] = ExecutionEventFamily.TERMINAL,
        [ExecutionEventTypes.POSITION_FLATTENED] = ExecutionEventFamily.TERMINAL,
        [ExecutionEventTypes.SESSION_FORCED_FLATTENED] = ExecutionEventFamily.TERMINAL,
    };

    public static ExecutionEventFamily GetFamily(string eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            return ExecutionEventFamily.COMMAND;
        return _typeToFamily.TryGetValue(eventType, out var f) ? f : ExecutionEventFamily.COMMAND;
    }
}
