// SINGLE SOURCE OF TRUTH
// This file is the authoritative registry of all robot event types.
// It is compiled into Robot.Core.dll and should be referenced from that DLL.
// Do not duplicate this file elsewhere - if source is needed, reference Robot.Core.dll instead.
//
// Linked into: Robot.Core.csproj (modules/robot/core/)
// Referenced by: RobotCore_For_NinjaTrader (via Robot.Core.dll)

using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core;

/// <summary>
/// Centralized registry of all robot event types with level assignments.
/// This provides compile-time validation and consistent level assignment.
/// </summary>
public static class RobotEventTypes
{
    /// <summary>
    /// Get the log level for an event type.
    /// Returns "INFO" if event type is unknown (fail-safe).
    /// </summary>
    public static string GetLevel(string eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            return "INFO";
        
        if (_levelMap.TryGetValue(eventType, out var level))
            return level;
        
        // Fallback: use heuristics for unknown events
        var upper = eventType.ToUpperInvariant();
        if (upper.Contains("ERROR") || upper.Contains("FAIL") || upper.Contains("INVALID") || upper.Contains("VIOLATION"))
            return "ERROR";
        if (upper.Contains("WARN") || upper.Contains("BLOCKED"))
            return "WARN";
        if (upper.Contains("DEBUG") || upper.Contains("DIAGNOSTIC") || upper.Contains("TRACE") || upper.Contains("HEARTBEAT"))
            return "DEBUG";
        
        return "INFO";
    }
    
    /// <summary>
    /// Check if an event type is valid (exists in registry).
    /// </summary>
    public static bool IsValid(string eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            return false;
        
        return _levelMap.ContainsKey(eventType) || _allEvents.Contains(eventType);
    }
    
    /// <summary>
    /// Get all registered event types.
    /// </summary>
    public static IEnumerable<string> GetAllEventTypes()
    {
        return _allEvents;
    }
    
    // Level mapping: event type -> log level
    private static readonly Dictionary<string, string> _levelMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Engine lifecycle
        ["ENGINE_START"] = "INFO",
        ["ENGINE_STOP"] = "INFO",
        ["ENGINE_STAND_DOWN"] = "WARN",
        ["ENGINE_TICK_INVALID_STATE"] = "ERROR",
        ["ENGINE_TICK_STALL_DETECTED"] = "ERROR",
        ["ENGINE_TICK_STALL_RECOVERED"] = "INFO",
        ["ENGINE_TICK_HEARTBEAT"] = "DEBUG",
        ["ENGINE_BAR_HEARTBEAT"] = "DEBUG",
        
        // Configuration
        ["PROJECT_ROOT_RESOLVED"] = "INFO",
        ["LOG_DIR_RESOLVED"] = "INFO",
        ["LOGGING_CONFIG_LOADED"] = "INFO",
        ["SPEC_LOADED"] = "INFO",
        ["SPEC_INVALID"] = "ERROR",
        ["SPEC_NAME_LOADED"] = "INFO",
        ["HEALTH_MONITOR_CONFIG_LOADED"] = "INFO",
        ["HEALTH_MONITOR_CONFIG_ERROR"] = "ERROR",
        ["HEALTH_MONITOR_CONFIG_MISSING"] = "WARN",
        ["HEALTH_MONITOR_CONFIG_NULL"] = "WARN",
        ["HEALTH_MONITOR_DISABLED"] = "INFO",
        ["HEALTH_MONITOR_STARTED"] = "INFO",
        ["HEALTH_MONITOR_EVALUATION_ERROR"] = "ERROR",
        ["PUSHOVER_CONFIG_MISSING"] = "WARN",
        
        // Timetable
        ["TIMETABLE_LOADED"] = "INFO",
        ["TIMETABLE_UPDATED"] = "INFO",
        ["TIMETABLE_VALIDATED"] = "INFO",
        ["TIMETABLE_INVALID"] = "ERROR",
        ["TIMETABLE_INVALID_TRADING_DATE"] = "ERROR",
        ["TIMETABLE_MISSING_TRADING_DATE"] = "ERROR",
        ["TIMETABLE_TRADING_DATE_MISMATCH"] = "ERROR",
        ["TIMETABLE_APPLY_SKIPPED"] = "INFO",
        ["TIMETABLE_PARSING_COMPLETE"] = "INFO",
        ["TIMETABLE_POLL_STALL_DETECTED"] = "ERROR",
        ["TIMETABLE_POLL_STALL_RECOVERED"] = "INFO",
        
        // Trading date
        ["TRADING_DATE_LOCKED"] = "INFO",
        
        // Streams
        ["STREAMS_CREATED"] = "INFO",
        ["STREAMS_CREATION_FAILED"] = "ERROR",
        ["STREAMS_CREATION_SKIPPED"] = "WARN",
        ["DUPLICATE_STREAM_ID"] = "ERROR",
        ["STREAM_STAND_DOWN"] = "WARN",
        ["STREAM_SKIPPED"] = "INFO",
        
        // Bars
        ["BAR_ACCEPTED"] = "INFO",
        ["BAR_DATE_MISMATCH"] = "WARN",
        ["BAR_PARTIAL_REJECTED"] = "WARN",
        ["BAR_RECEIVED_BEFORE_DATE_LOCKED"] = "WARN",
        ["BAR_RECEIVED_NO_STREAMS"] = "WARN",
        ["BAR_REJECTION_CONTINUOUS_FUTURE"] = "WARN",
        ["BAR_REJECTION_CONTINUOUS_NO_DATE_LOCK"] = "WARN",
        ["BAR_REJECTION_RATE_HIGH"] = "WARN",
        ["BAR_REJECTION_SUMMARY"] = "INFO",
        ["BAR_DELIVERY_SUMMARY"] = "INFO",
        ["BAR_DELIVERY_TO_STREAM"] = "DEBUG",
        ["BAR_TIME_INTERPRETATION_MISMATCH"] = "WARN",
        ["BAR_TIME_INTERPRETATION_LOCKED"] = "INFO",
        ["BAR_TIME_DETECTION_STARTING"] = "DEBUG",
        
        // BarsRequest
        ["BARSREQUEST_REQUESTED"] = "INFO",
        ["BARSREQUEST_EXECUTED"] = "INFO",
        ["BARSREQUEST_FAILED"] = "ERROR",
        ["BARSREQUEST_SKIPPED"] = "INFO",
        ["BARSREQUEST_INITIALIZATION"] = "INFO",
        ["BARSREQUEST_RANGE_CHECK"] = "DEBUG",
        ["BARSREQUEST_RANGE_DETERMINED"] = "INFO",
        ["BARSREQUEST_STREAM_STATUS"] = "DEBUG",
        ["BARSREQUEST_FILTER_SUMMARY"] = "DEBUG",
        ["BARSREQUEST_UNEXPECTED_COUNT"] = "WARN",
        ["BARSREQUEST_ZERO_BARS_DIAGNOSTIC"] = "DEBUG",
        
        // Pre-hydration
        ["PRE_HYDRATION_BARS_LOADED"] = "INFO",
        ["PRE_HYDRATION_BARS_FILTERED"] = "DEBUG",
        ["PRE_HYDRATION_BARS_SKIPPED"] = "DEBUG",
        ["PRE_HYDRATION_BARS_SKIPPED_STREAM_STATE"] = "DEBUG",
        ["PRE_HYDRATION_BARS_SKIPPED_ACTIVE_STREAM"] = "DEBUG",
        ["PRE_HYDRATION_NO_BARS_AFTER_FILTER"] = "WARN",
        ["PRE_HYDRATION_COMPLETE_SET"] = "DEBUG",
        ["PRE_HYDRATION_COMPLETE_BLOCK_ENTERED"] = "DEBUG",
        ["PRE_HYDRATION_AFTER_COMPLETE_BLOCK"] = "DEBUG",
        ["PRE_HYDRATION_AFTER_VARIABLES"] = "DEBUG",
        ["PRE_HYDRATION_BEFORE_RANGE_DIAGNOSTIC"] = "DEBUG",
        ["PRE_HYDRATION_RANGE_START_DIAGNOSTIC"] = "DEBUG",
        ["PRE_HYDRATION_HANDLER_TRACE"] = "DEBUG",
        ["PRE_HYDRATION_FAILED"] = "ERROR",
        
        // Range computation
        ["RANGE_LOCKED"] = "INFO",
        ["RANGE_COMPUTE_FAILED"] = "ERROR",
        ["RANGE_COMPUTE_START"] = "DEBUG",
        ["RANGE_COMPUTE_NO_BARS_DIAGNOSTIC"] = "DEBUG",
        ["RANGE_COMPUTE_BAR_FILTERING"] = "DEBUG",
        ["RANGE_FIRST_BAR_ACCEPTED"] = "DEBUG",
        ["RANGE_INTENT_ASSERT"] = "DEBUG",
        ["RANGE_LOCK_ASSERT"] = "DEBUG",
        ["RANGE_INVALIDATED"] = "WARN",
        
        // Execution
        ["EXECUTION_MODE_SET"] = "INFO",
        ["EXECUTION_BLOCKED"] = "WARN",
        ["EXECUTION_ERROR"] = "ERROR",
        ["EXECUTION_FILLED"] = "INFO",
        ["EXECUTION_PARTIAL_FILL"] = "INFO",
        ["EXECUTION_SKIPPED_DUPLICATE"] = "WARN",
        ["EXECUTION_UPDATE_UNKNOWN_ORDER"] = "WARN",
        ["EXECUTION_UPDATE_MOCK"] = "DEBUG",
        ["EXECUTION_SUMMARY_WRITTEN"] = "INFO",
        ["EXECUTION_GATE_INVARIANT_VIOLATION"] = "ERROR",
        
        // Orders
        ["ORDER_SUBMITTED"] = "INFO",
        ["ORDER_SUBMIT_SUCCESS"] = "INFO",
        ["ORDER_SUBMIT_FAIL"] = "ERROR",
        ["ORDER_SUBMIT_ATTEMPT"] = "DEBUG",
        ["ORDER_ACKNOWLEDGED"] = "INFO",
        ["ORDER_REJECTED"] = "ERROR",
        ["ORDER_CANCELLED"] = "INFO",
        ["ENTRY_SUBMITTED"] = "INFO",
        ["PROTECTIVES_PLACED"] = "INFO",
        ["PROTECTIVE_ORDERS_SUBMITTED"] = "INFO",
        ["PROTECTIVE_ORDERS_FAILED_FLATTENED"] = "WARN",
        ["STOP_BRACKETS_SUBMITTED"] = "INFO",
        ["STOP_BRACKETS_SUBMIT_ATTEMPT"] = "DEBUG",
        ["STOP_BRACKETS_SUBMIT_FAILED"] = "ERROR",
        ["STOP_MODIFY_ATTEMPT"] = "DEBUG",
        ["STOP_MODIFY_SUCCESS"] = "INFO",
        ["STOP_MODIFY_FAIL"] = "ERROR",
        ["STOP_MODIFY_SKIPPED"] = "INFO",
        
        // Execution adapters
        ["ADAPTER_SELECTED"] = "INFO",
        ["SIM_ACCOUNT_VERIFIED"] = "INFO",
        ["LIVE_MODE_BLOCKED"] = "ERROR",
        
        // Kill switch
        ["KILL_SWITCH_INITIALIZED"] = "INFO",
        ["KILL_SWITCH_ACTIVE"] = "ERROR",
        ["KILL_SWITCH_ERROR_FAIL_CLOSED"] = "ERROR",
        
        // Flatten
        ["FLATTEN_ATTEMPT"] = "INFO",
        ["FLATTEN_SUCCESS"] = "INFO",
        ["FLATTEN_FAIL"] = "ERROR",
        ["FLATTEN_DRYRUN"] = "DEBUG",
        
        // Recovery
        ["DISCONNECT_FAIL_CLOSED_ENTERED"] = "ERROR",
        ["DISCONNECT_RECOVERY_STARTED"] = "INFO",
        ["DISCONNECT_RECOVERY_COMPLETE"] = "INFO",
        ["DISCONNECT_RECOVERY_ABORTED"] = "WARN",
        ["DISCONNECT_RECOVERY_SKIPPED"] = "INFO",
        ["DISCONNECT_RECOVERY_WAITING_FOR_SYNC"] = "INFO",
        ["RECOVERY_ACCOUNT_SNAPSHOT"] = "INFO",
        ["RECOVERY_ACCOUNT_SNAPSHOT_FAILED"] = "ERROR",
        ["RECOVERY_ACCOUNT_SNAPSHOT_NULL"] = "WARN",
        ["RECOVERY_CANCELLED_ROBOT_ORDERS"] = "INFO",
        ["RECOVERY_CANCELLED_ROBOT_ORDERS_FAILED"] = "ERROR",
        ["RECOVERY_POSITION_RECONCILED"] = "INFO",
        ["RECOVERY_POSITION_UNMATCHED"] = "WARN",
        ["RECOVERY_PROTECTIVE_ORDERS_PLACED"] = "INFO",
        ["RECOVERY_STREAM_ORDERS_RECONCILED"] = "INFO",
        
        // Health monitoring
        ["CONNECTION_LOST"] = "ERROR",
        ["CONNECTION_LOST_SUSTAINED"] = "ERROR",
        ["CONNECTION_RECOVERED"] = "INFO",
        ["DATA_LOSS_DETECTED"] = "ERROR",
        ["DATA_STALL_RECOVERED"] = "INFO",
        
        // Notifications
        ["PUSHOVER_NOTIFY_ENQUEUED"] = "DEBUG",
        ["PUSHOVER_ENDPOINT"] = "DEBUG",
        ["CRITICAL_EVENT_REPORTED"] = "INFO",
        ["CRITICAL_NOTIFICATION_REJECTED"] = "WARN",
        ["CRITICAL_DEDUPE_MISSING_RUN_ID"] = "WARN",
        ["TEST_NOTIFICATION_SENT"] = "INFO",
        ["TEST_NOTIFICATION_SKIPPED"] = "WARN",
        ["TEST_NOTIFICATION_TRIGGER_ERROR"] = "ERROR",
        
        // Journal
        ["JOURNAL_WRITTEN"] = "INFO",
        ["EXECUTION_JOURNAL_ERROR"] = "ERROR",
        ["EXECUTION_JOURNAL_READ_ERROR"] = "ERROR",
        
        // Logging service
        ["LOG_BACKPRESSURE_DROP"] = "ERROR",
        ["LOG_WORKER_LOOP_ERROR"] = "ERROR",
        ["LOG_WRITE_FAILURE"] = "ERROR",
        ["LOG_HEALTH_ERROR"] = "ERROR",
        ["LOG_CONVERSION_ERROR"] = "ERROR",
        ["LOGGER_CONVERSION_ERROR"] = "ERROR",
        ["LOGGING_INVARIANT_VIOLATION"] = "ERROR",
        
        // Account snapshots
        ["ACCOUNT_SNAPSHOT_DRYRUN"] = "DEBUG",
        ["ACCOUNT_SNAPSHOT_ERROR"] = "ERROR",
        ["ACCOUNT_SNAPSHOT_LIVE_ERROR"] = "ERROR",
        ["ACCOUNT_SNAPSHOT_LIVE_STUB"] = "DEBUG",
        
        // Order cancellation
        ["CANCEL_ROBOT_ORDERS_DRYRUN"] = "DEBUG",
        ["CANCEL_ROBOT_ORDERS_ERROR"] = "ERROR",
        ["CANCEL_ROBOT_ORDERS_LIVE_ERROR"] = "ERROR",
        ["CANCEL_ROBOT_ORDERS_LIVE_STUB"] = "DEBUG",
        ["CANCEL_ROBOT_ORDERS_MOCK"] = "DEBUG",
        ["ROBOT_ORDERS_CANCELLED"] = "INFO",
        
        // DRYRUN events
        ["ENTRY_ORDER_DRYRUN"] = "DEBUG",
        ["STOP_ENTRY_ORDER_DRYRUN"] = "DEBUG",
        ["PROTECTIVE_STOP_DRYRUN"] = "DEBUG",
        ["TARGET_ORDER_DRYRUN"] = "DEBUG",
        ["BE_MODIFY_DRYRUN"] = "DEBUG",
        
        // Tick/stream state
        ["TICK_METHOD_ENTERED"] = "DEBUG",
        ["TICK_METHOD_ENTERED_ERROR"] = "ERROR",
        ["TICK_CALLED"] = "DEBUG",
        ["TICK_TRACE"] = "DEBUG",
        ["UPDATE_APPLIED"] = "DEBUG",
        
        // Session
        ["SESSION_START_TIME_SET"] = "INFO",
        
        // Gaps
        ["GAP_VIOLATIONS_SUMMARY"] = "WARN",
        
        // Slot gate
        ["SLOT_GATE_DIAGNOSTIC"] = "DEBUG",
        
        // Operator
        ["OPERATOR_BANNER"] = "INFO",
        
        // Invariants
        ["INVARIANT_VIOLATION"] = "ERROR",
        
        // Incident persistence
        ["INCIDENT_PERSIST_ERROR"] = "ERROR",
    };
    
    // All registered event types (for validation)
    private static readonly HashSet<string> _allEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        // Engine lifecycle
        "ENGINE_START", "ENGINE_STOP", "ENGINE_STAND_DOWN", "ENGINE_TICK_INVALID_STATE",
        "ENGINE_TICK_STALL_DETECTED", "ENGINE_TICK_STALL_RECOVERED", "ENGINE_TICK_HEARTBEAT",
        "ENGINE_BAR_HEARTBEAT",
        
        // Configuration
        "PROJECT_ROOT_RESOLVED", "LOG_DIR_RESOLVED", "LOGGING_CONFIG_LOADED", "SPEC_LOADED",
        "SPEC_INVALID", "SPEC_NAME_LOADED", "HEALTH_MONITOR_CONFIG_LOADED",
        "HEALTH_MONITOR_CONFIG_ERROR", "HEALTH_MONITOR_CONFIG_MISSING", "HEALTH_MONITOR_CONFIG_NULL",
        "HEALTH_MONITOR_DISABLED", "HEALTH_MONITOR_STARTED", "HEALTH_MONITOR_EVALUATION_ERROR",
        "PUSHOVER_CONFIG_MISSING",
        
        // Timetable
        "TIMETABLE_LOADED", "TIMETABLE_UPDATED", "TIMETABLE_VALIDATED", "TIMETABLE_INVALID",
        "TIMETABLE_INVALID_TRADING_DATE", "TIMETABLE_MISSING_TRADING_DATE",
        "TIMETABLE_TRADING_DATE_MISMATCH", "TIMETABLE_APPLY_SKIPPED", "TIMETABLE_PARSING_COMPLETE",
        "TIMETABLE_POLL_STALL_DETECTED", "TIMETABLE_POLL_STALL_RECOVERED",
        
        // Trading date
        "TRADING_DATE_LOCKED",
        
        // Streams
        "STREAMS_CREATED", "STREAMS_CREATION_FAILED", "STREAMS_CREATION_SKIPPED",
        "DUPLICATE_STREAM_ID", "STREAM_STAND_DOWN", "STREAM_SKIPPED",
        
        // Bars
        "BAR_ACCEPTED", "BAR_DATE_MISMATCH", "BAR_PARTIAL_REJECTED",
        "BAR_RECEIVED_BEFORE_DATE_LOCKED", "BAR_RECEIVED_NO_STREAMS",
        "BAR_REJECTION_CONTINUOUS_FUTURE", "BAR_REJECTION_CONTINUOUS_NO_DATE_LOCK",
        "BAR_REJECTION_RATE_HIGH", "BAR_REJECTION_SUMMARY", "BAR_DELIVERY_SUMMARY",
        "BAR_DELIVERY_TO_STREAM", "BAR_TIME_INTERPRETATION_MISMATCH",
        "BAR_TIME_INTERPRETATION_LOCKED", "BAR_TIME_DETECTION_STARTING",
        
        // BarsRequest
        "BARSREQUEST_REQUESTED", "BARSREQUEST_EXECUTED", "BARSREQUEST_FAILED",
        "BARSREQUEST_SKIPPED", "BARSREQUEST_INITIALIZATION", "BARSREQUEST_RANGE_CHECK",
        "BARSREQUEST_RANGE_DETERMINED", "BARSREQUEST_STREAM_STATUS",
        "BARSREQUEST_FILTER_SUMMARY", "BARSREQUEST_UNEXPECTED_COUNT",
        "BARSREQUEST_ZERO_BARS_DIAGNOSTIC",
        
        // Pre-hydration
        "PRE_HYDRATION_BARS_LOADED", "PRE_HYDRATION_BARS_FILTERED",
        "PRE_HYDRATION_BARS_SKIPPED", "PRE_HYDRATION_BARS_SKIPPED_STREAM_STATE",
        "PRE_HYDRATION_BARS_SKIPPED_ACTIVE_STREAM", "PRE_HYDRATION_NO_BARS_AFTER_FILTER",
        "PRE_HYDRATION_COMPLETE_SET", "PRE_HYDRATION_COMPLETE_BLOCK_ENTERED",
        "PRE_HYDRATION_AFTER_COMPLETE_BLOCK", "PRE_HYDRATION_AFTER_VARIABLES",
        "PRE_HYDRATION_BEFORE_RANGE_DIAGNOSTIC", "PRE_HYDRATION_RANGE_START_DIAGNOSTIC",
        "PRE_HYDRATION_HANDLER_TRACE", "PRE_HYDRATION_FAILED",
        
        // Range computation
        "RANGE_LOCKED", "RANGE_COMPUTE_FAILED", "RANGE_COMPUTE_START",
        "RANGE_COMPUTE_NO_BARS_DIAGNOSTIC", "RANGE_COMPUTE_BAR_FILTERING",
        "RANGE_FIRST_BAR_ACCEPTED", "RANGE_INTENT_ASSERT", "RANGE_LOCK_ASSERT",
        "RANGE_INVALIDATED",
        
        // Execution
        "EXECUTION_MODE_SET", "EXECUTION_BLOCKED", "EXECUTION_ERROR", "EXECUTION_FILLED",
        "EXECUTION_PARTIAL_FILL", "EXECUTION_SKIPPED_DUPLICATE", "EXECUTION_UPDATE_UNKNOWN_ORDER",
        "EXECUTION_UPDATE_MOCK", "EXECUTION_SUMMARY_WRITTEN", "EXECUTION_GATE_INVARIANT_VIOLATION",
        
        // Orders
        "ORDER_SUBMITTED", "ORDER_SUBMIT_SUCCESS", "ORDER_SUBMIT_FAIL", "ORDER_SUBMIT_ATTEMPT",
        "ORDER_ACKNOWLEDGED", "ORDER_REJECTED", "ORDER_CANCELLED", "ENTRY_SUBMITTED",
        "PROTECTIVES_PLACED", "PROTECTIVE_ORDERS_SUBMITTED", "PROTECTIVE_ORDERS_FAILED_FLATTENED",
        "STOP_BRACKETS_SUBMITTED", "STOP_BRACKETS_SUBMIT_ATTEMPT", "STOP_BRACKETS_SUBMIT_FAILED",
        "STOP_MODIFY_ATTEMPT", "STOP_MODIFY_SUCCESS", "STOP_MODIFY_FAIL", "STOP_MODIFY_SKIPPED",
        
        // Execution adapters
        "ADAPTER_SELECTED", "SIM_ACCOUNT_VERIFIED", "LIVE_MODE_BLOCKED",
        
        // Kill switch
        "KILL_SWITCH_INITIALIZED", "KILL_SWITCH_ACTIVE", "KILL_SWITCH_ERROR_FAIL_CLOSED",
        
        // Flatten
        "FLATTEN_ATTEMPT", "FLATTEN_SUCCESS", "FLATTEN_FAIL", "FLATTEN_DRYRUN",
        
        // Recovery
        "DISCONNECT_FAIL_CLOSED_ENTERED", "DISCONNECT_RECOVERY_STARTED",
        "DISCONNECT_RECOVERY_COMPLETE", "DISCONNECT_RECOVERY_ABORTED",
        "DISCONNECT_RECOVERY_SKIPPED", "DISCONNECT_RECOVERY_WAITING_FOR_SYNC",
        "RECOVERY_ACCOUNT_SNAPSHOT", "RECOVERY_ACCOUNT_SNAPSHOT_FAILED",
        "RECOVERY_ACCOUNT_SNAPSHOT_NULL", "RECOVERY_CANCELLED_ROBOT_ORDERS",
        "RECOVERY_CANCELLED_ROBOT_ORDERS_FAILED", "RECOVERY_POSITION_RECONCILED",
        "RECOVERY_POSITION_UNMATCHED", "RECOVERY_PROTECTIVE_ORDERS_PLACED",
        "RECOVERY_STREAM_ORDERS_RECONCILED",
        
        // Health monitoring
        "CONNECTION_LOST", "CONNECTION_LOST_SUSTAINED", "CONNECTION_RECOVERED",
        "DATA_LOSS_DETECTED", "DATA_STALL_RECOVERED",
        
        // Notifications
        "PUSHOVER_NOTIFY_ENQUEUED", "PUSHOVER_ENDPOINT",
        "CRITICAL_EVENT_REPORTED", "CRITICAL_NOTIFICATION_REJECTED", "CRITICAL_DEDUPE_MISSING_RUN_ID",
        "TEST_NOTIFICATION_SENT", "TEST_NOTIFICATION_SKIPPED", "TEST_NOTIFICATION_TRIGGER_ERROR",
        
        // Journal
        "JOURNAL_WRITTEN", "EXECUTION_JOURNAL_ERROR", "EXECUTION_JOURNAL_READ_ERROR",
        
        // Logging service
        "LOG_BACKPRESSURE_DROP", "LOG_WORKER_LOOP_ERROR", "LOG_WRITE_FAILURE",
        "LOG_HEALTH_ERROR", "LOG_CONVERSION_ERROR", "LOGGER_CONVERSION_ERROR",
        "LOGGING_INVARIANT_VIOLATION",
        
        // Account snapshots
        "ACCOUNT_SNAPSHOT_DRYRUN", "ACCOUNT_SNAPSHOT_ERROR", "ACCOUNT_SNAPSHOT_LIVE_ERROR",
        "ACCOUNT_SNAPSHOT_LIVE_STUB",
        
        // Order cancellation
        "CANCEL_ROBOT_ORDERS_DRYRUN", "CANCEL_ROBOT_ORDERS_ERROR",
        "CANCEL_ROBOT_ORDERS_LIVE_ERROR", "CANCEL_ROBOT_ORDERS_LIVE_STUB",
        "CANCEL_ROBOT_ORDERS_MOCK", "ROBOT_ORDERS_CANCELLED",
        
        // DRYRUN events
        "ENTRY_ORDER_DRYRUN", "STOP_ENTRY_ORDER_DRYRUN", "PROTECTIVE_STOP_DRYRUN",
        "TARGET_ORDER_DRYRUN", "BE_MODIFY_DRYRUN",
        
        // Tick/stream state
        "TICK_METHOD_ENTERED", "TICK_METHOD_ENTERED_ERROR", "TICK_CALLED", "TICK_TRACE",
        "UPDATE_APPLIED",
        
        // Session
        "SESSION_START_TIME_SET",
        
        // Gaps
        "GAP_VIOLATIONS_SUMMARY",
        
        // Slot gate
        "SLOT_GATE_DIAGNOSTIC",
        
        // Operator
        "OPERATOR_BANNER",
        
        // Invariants
        "INVARIANT_VIOLATION",
        
        // Incident persistence
        "INCIDENT_PERSIST_ERROR",
    };
}
