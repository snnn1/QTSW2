"""
Watchdog Aggregator Configuration
"""
from pathlib import Path
from typing import Optional

# Calculate project root: modules/watchdog/config.py -> QTSW2 root (go up 2 levels)
QTSW2_ROOT = Path(__file__).parent.parent.parent

# Robot log directories
ROBOT_LOGS_DIR = QTSW2_ROOT / "logs" / "robot"
ROBOT_JOURNAL_DIR = ROBOT_LOGS_DIR / "journal"
EXECUTION_JOURNALS_DIR = QTSW2_ROOT / "data" / "execution_journals"
EXECUTION_SUMMARIES_DIR = QTSW2_ROOT / "data" / "execution_summaries"
FRONTEND_FEED_FILE = ROBOT_LOGS_DIR / "frontend_feed.jsonl"
FRONTEND_CURSOR_FILE = QTSW2_ROOT / "data" / "frontend_cursor.json"
# Persisted read positions for robot log files (survives watchdog restarts)
ROBOT_LOG_READ_POSITIONS_FILE = QTSW2_ROOT / "data" / "robot_log_read_positions.json"

# Phase 1: Alert ledger and notification config
ALERT_LEDGER_PATH = QTSW2_ROOT / "data" / "watchdog" / "alert_ledger.jsonl"
# Incident recorder: structured start/end events (CONNECTION_LOST, ENGINE_STALLED, etc.)
INCIDENT_LOG_DIR = QTSW2_ROOT / "data" / "watchdog"
INCIDENTS_FILE = INCIDENT_LOG_DIR / "incidents.jsonl"
# Phase 9: Active incidents persistence (restart safety)
ACTIVE_INCIDENTS_FILE = INCIDENT_LOG_DIR / "active_incidents.json"
# Phase 8: Rolling metrics history (week/month aggregates)
METRICS_HISTORY_FILE = INCIDENT_LOG_DIR / "metrics_history.jsonl"
# Phase 9: Incident replay - read last N bytes from feed (avoids full-file scan)
REPLAY_TAIL_BYTES = 20 * 1024 * 1024  # 20 MB
# Status snapshot persistence for post-incident analysis (last 500 critical snapshots)
STATUS_SNAPSHOTS_FILE = QTSW2_ROOT / "data" / "watchdog" / "status_snapshots.jsonl"
STATUS_SNAPSHOTS_MAX_ENTRIES = 500
NOTIFICATIONS_CONFIG_PATH = QTSW2_ROOT / "configs" / "watchdog" / "notifications.json"
NOTIFICATIONS_SECRETS_PATH = QTSW2_ROOT / "configs" / "watchdog" / "notifications.secrets.json"
# Phase 4: Incident-based alert rules, thresholds, cooldowns
ALERTS_CONFIG_PATH = QTSW2_ROOT / "configs" / "watchdog" / "alerts.json"

# Process monitor
PROCESS_MONITOR_INTERVAL_SECONDS = 30
PROCESS_MONITOR_PROCESS_NAME = "NinjaTrader.exe"

# Supervision and alert safeguards
SUPERVISION_WINDOW_RECENT_ACTIVITY_SECONDS = 7200  # 2 hours
ALERT_STARTUP_GRACE_SECONDS = 90  # No alerts in first 90s after watchdog start (avoids false alerts during deploys)
PROCESS_MONITOR_GRACE_SECONDS = 60  # No process-down alert in first 60s

# Log-growth monitoring: detect feed file not growing (logging deadlock, file handle lost, NT not flushing)
LOG_GROWTH_MONITOR_FILE = FRONTEND_FEED_FILE  # Monitor our ingest source
LOG_GROWTH_STALL_THRESHOLD_SECONDS = 60  # File size unchanged for 60s → alert candidate

# Orphan detection: confirmed orphan requires heartbeat lost for this long with no recovery
CONFIRMED_ORPHAN_HEARTBEAT_LOST_SECONDS = 120  # 2 minutes

# Thresholds (from specification)
# ENGINE_TICK_STALL_THRESHOLD_SECONDS: Threshold for detecting engine tick stalls
# ENGINE_TICK_CALLSITE is rate-limited in feed to every 5 seconds
# Use 60s - we now use processing time for liveness, so only stall when we truly stop receiving ticks
ENGINE_TICK_STALL_THRESHOLD_SECONDS = 60
# Hysteresis: require extra seconds before declaring dead (prevents flickering)
ENGINE_TICK_STALL_HYSTERESIS_SECONDS = 15
STUCK_STREAM_THRESHOLD_SECONDS = 300
UNPROTECTED_TIMEOUT_SECONDS = 10
# DATA_STALL_THRESHOLD_SECONDS must be > rate limit of bar tracking events
# ONBARUPDATE_CALLED and ENGINE_TICK_HEARTBEAT are rate-limited to 60 seconds
# Set threshold to 120 seconds (2x rate limit) to avoid false positives from rate limiting
# Increased from 90s to 120s to prevent flickering from temporary gaps
DATA_STALL_THRESHOLD_SECONDS = 120  # Default, can be configurable per instrument
# DATA_EVENT_MAX_AGE_SECONDS: Reject bar events older than this (prevents stale bars from tail causing false DATA FLOWING)
DATA_EVENT_MAX_AGE_SECONDS = 120  # Align with DATA_STALL_THRESHOLD
# RECOVERY_TIMEOUT_SECONDS: Maximum time recovery can be in RECOVERY_RUNNING state
# If DISCONNECT_RECOVERY_COMPLETE event is not received within this time, auto-clear recovery state
# Set to 10 minutes - recovery should complete quickly, but allow time for slow reconnections
RECOVERY_TIMEOUT_SECONDS = 600  # 10 minutes

# ENGINE_TICK_MAX_AGE_FOR_INIT_SECONDS: Reject ENGINE_TICK_CALLSITE events older than this when
# initializing from tail (startup or after invalidate). Prevents false ENGINE ALIVE at login screen.
# Strategy emits ticks every ~5s when running; 15s ensures we only trust ticks from active strategy.
ENGINE_TICK_MAX_AGE_FOR_INIT_SECONDS = 15

# SESSION_CONNECTION_EVENT_MAX_AGE_SECONDS: When processing CONNECTION_LOST/RECOVERED from tail,
# skip session metrics (disconnect count, downtime) if event is older than this. Prevents tail replay
# from inflating "42 disconnects" when watchdog restarts. Status (_connection_status) still updated.
SESSION_CONNECTION_EVENT_MAX_AGE_SECONDS = 60

# DISCONNECT_DEDUPE_WINDOW_SECONDS: Rapid connection flaps within this window count as one disconnect.
# Prevents multiple CONNECTION_LOST events in quick succession from inflating session disconnect count.
DISCONNECT_DEDUPE_WINDOW_SECONDS = 30

# IDENTITY_EXPIRY_SECONDS: If no IDENTITY_INVARIANTS_STATUS event in this many seconds, treat identity as Unknown.
IDENTITY_EXPIRY_SECONDS = 600  # 10 minutes

# INGESTION: Single tail read per cycle - line count for frontend_feed.jsonl tail.
# Reduced from 5000 to 3000 to lower I/O cost (WATCHDOG_INGESTION_HARDENING).
TAIL_LINE_COUNT = 3000

# INGESTION: Degradation mode - enter when loop_duration_ms > DEGRADATION_LOOP_THRESHOLD_MS
# for DEGRADATION_CONSECUTIVE_CYCLES consecutive cycles.
DEGRADATION_LOOP_THRESHOLD_MS = 900
DEGRADATION_CONSECUTIVE_CYCLES = 10

# INGESTION: Telemetry emit interval (seconds).
INGESTION_STATS_INTERVAL_SECONDS = 10

# INGESTION: /events cache TTL (seconds). Multiple clients within TTL share one disk read.
EVENTS_CACHE_TTL_SECONDS = 1

# STREAM_MAX_AGE_DAYS: Maximum age for streams to be shown (today + N days back).
# Streams older than this are filtered out. Must cover weekend carry-over (Fri -> Mon = 3 days).
STREAM_MAX_AGE_DAYS = 3  # Allow today + Fri->Mon weekend carry-over

# Feed/Ingestion health alert thresholds
FEED_INGESTION_DELAY_THRESHOLD_SECONDS = 10  # Alert when feed lag > 10s
WATCHDOG_LOOP_SLOW_THRESHOLD_MS = 500  # Alert when loop duration > 500ms (degradation is 900ms)
ANOMALY_RATE_THRESHOLD = 10  # Anomalies in window
ANOMALY_RATE_WINDOW_SECONDS = 300  # 5 minutes

# Execution anomaly thresholds (Phase 12)
DUPLICATE_ORDER_WINDOW_MS = 5000  # Window for duplicate submission detection
ORDER_STUCK_ENTRY_THRESHOLD_SECONDS = 120  # Entry working too long
ORDER_STUCK_PROTECTIVE_THRESHOLD_SECONDS = 90  # Stop/target working too long
ORDER_STUCK_REORDER_GRACE_SECONDS = 8  # Grace before flagging stuck (late cancel events from multi-file merge)
ORDER_STUCK_DETECTED_COOLDOWN_SECONDS = 60  # Min interval between ORDER_STUCK_DETECTED alerts (stops flood when recovery loops)
EXECUTION_LATENCY_SPIKE_THRESHOLD_MS = 5000  # Submit→fill latency
RECOVERY_LOOP_COUNT_THRESHOLD = 3  # Recovery entries in window
RECOVERY_LOOP_WINDOW_SECONDS = 600  # Rolling window for recovery loop

# Update frequencies (seconds)
ENGINE_ALIVE_UPDATE_FREQUENCY = 5
STREAM_STUCK_UPDATE_FREQUENCY = 10
RISK_GATE_UPDATE_FREQUENCY = 5
WATCHDOG_STATUS_UPDATE_FREQUENCY = 5
UNPROTECTED_POSITION_UPDATE_FREQUENCY = 2

# WebSocket configuration
WS_SEND_TIMEOUT_SECONDS = 5  # Timeout for send operations
WS_HEARTBEAT_INTERVAL_SECONDS = 30  # Heartbeat interval
WS_MAX_SEND_FAILURES = 10  # Max failures before closing connection
WS_MAX_BUFFER_SIZE = 100  # Max events in buffer (if buffer used)

# Live-critical event types (from specification section 5.1)
LIVE_CRITICAL_EVENT_TYPES = {
    # PHASE 3.1: Identity invariants status for ongoing monitoring
    "IDENTITY_INVARIANTS_STATUS",
    # Engine Health
    "ENGINE_START",
    "ENGINE_STOP",
    # ENGINE_HEARTBEAT removed: Only works if HeartbeatAddOn/Strategy is installed, unreliable
    # Use ENGINE_TICK_CALLSITE (rate-limited) instead for reliable liveness monitoring
    "ENGINE_TICK_EXECUTED",  # Diagnostic: Tick() execution (Phase 1)
    "ENGINE_TICK_CALLSITE",  # Diagnostic: Tick() call site (rate-limited in event feed to every 5s)
    "ENGINE_TIMER_HEARTBEAT",  # Timer-based heartbeat when market closed (no ticks)
    "ENGINE_TICK_BEFORE_LOCK",  # Diagnostic: before lock acquisition
    "ENGINE_TICK_LOCK_ACQUIRED",  # Diagnostic: after lock acquired
    "ENGINE_TICK_AFTER_LOCK",  # Diagnostic: after lock released
    "ENGINE_BUILD_STAMP",  # Diagnostic: engine build and instance ID
    "ENGINE_TICK_HEARTBEAT",  # Bar-driven heartbeat (keep for bar tracking)
    "ENGINE_ALIVE",  # Strategy heartbeat (every N bars in Realtime) - fallback when ENGINE_TICK_CALLSITE not emitted
    "ENGINE_TICK_STALL_DETECTED",
    "ENGINE_TICK_STALL_RECOVERED",
    # Recovery State
    "DISCONNECT_FAIL_CLOSED_ENTERED",
    "DISCONNECT_RECOVERY_STARTED",
    "DISCONNECT_RECOVERY_COMPLETE",
    "DISCONNECT_RECOVERY_ABORTED",
    # Connection Status
    "CONNECTION_LOST",
    "CONNECTION_LOST_SUSTAINED",
    "CONNECTION_RECOVERED",
    "CONNECTION_RECOVERED_NOTIFICATION",  # Recovery notification after sustained disconnect
    "CONNECTION_CONFIRMED",  # Robot emits at startup and periodically when connected (Watchdog shows Connected vs Unknown)
    # Kill Switch
    "KILL_SWITCH_ACTIVE",
    # Stream State
    "STREAM_STAND_DOWN",
    "MARKET_CLOSE_NO_TRADE",  # Stream commits at market close (treated same as STREAM_STAND_DOWN)
    "RANGE_INVALIDATED",
    # Execution Blocking
    "EXECUTION_BLOCKED",
    "EXECUTION_ALLOWED",
    # Order Lifecycle (robot emits ORDER_SUBMIT_SUCCESS; ORDER_SUBMITTED deprecated)
    "ORDER_SUBMIT_SUCCESS",
    "ORDER_ACKNOWLEDGED",
    "ORDER_REJECTED",
    "ORDER_CANCELLED",
    "EXECUTION_FILLED",
    "EXECUTION_PARTIAL_FILL",
    "EXECUTION_EXIT_FILL",  # Migration: ledger converts to synthetic EXECUTION_FILLED; robot now emits EXECUTION_FILLED for exits
    "EXECUTION_FILL_BLOCKED_TRADING_DATE_NULL",
    "EXECUTION_FILL_UNMAPPED",
    # Critical fill anomalies (no EXECUTION_FILLED emitted; track counts for fill_health)
    "BROKER_FLATTEN_FILL_RECOGNIZED",
    "EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL",
    # Protective Orders
    "PROTECTIVE_ORDERS_SUBMITTED",
    "PROTECTIVE_ORDERS_FAILED_FLATTENED",
    # Intent Exposure
    "INTENT_EXPOSURE_REGISTERED",
    "INTENT_EXPOSURE_CLOSED",
    "INTENT_EXIT_FILL",
    "TRADE_RECONCILED",  # Orphaned journal closed; broker flat
    # Data Health
    "DATA_LOSS_DETECTED",
    "DATA_STALL_RECOVERED",
    # Critical Events
    "CRITICAL_EVENT_REPORTED",
    "EXECUTION_GATE_INVARIANT_VIOLATION",
    "LEDGER_INVARIANT_VIOLATION",  # Phase 2.3: execution_sequence, exit_side, exit_qty
    # Deployment and Configuration
    "DUPLICATE_INSTANCE_DETECTED",  # Critical for detecting invalid deployments
    "EXECUTION_POLICY_VALIDATION_FAILED",  # Important for execution monitoring
    # Additional events needed for state tracking
    "RANGE_LOCKED",
    "RANGE_LOCK_SNAPSHOT",  # Contains range data for RANGE_LOCKED streams
    "RANGE_LOCKED_RESTORED_FROM_HYDRATION",  # Restore events include range/session metadata
    "RANGE_LOCKED_RESTORED_FROM_RANGES",
    "TIMETABLE_VALIDATED",
    # Stream State Machine transitions (plan requirement #2)
    "STREAM_STATE_TRANSITION",
    "SLOT_END_SUMMARY",  # Slot summary with trade_executed, reason, range data
    # Diagnostic events (for troubleshooting OnBarUpdate and bar routing)
    "ONBARUPDATE_CALLED",
    "ONBARUPDATE_DIAGNOSTIC",
    "BAR_ROUTING_DIAGNOSTIC",
    # Bar acceptance tracking
    "BAR_ACCEPTED",  # Bar acceptance events (rate-limited in engine)
    "BAR_RECEIVED_NO_STREAMS",  # Bar received before streams created (for data stall detection)
    # Execution anomaly monitoring (Phase 2-9)
    "EXECUTION_GHOST_FILL_DETECTED",
    "PROTECTIVE_DRIFT_DETECTED",
    "ORPHAN_ORDER_DETECTED",
    "DUPLICATE_ORDER_SUBMISSION_DETECTED",
    "POSITION_DRIFT_DETECTED",
    "ORDER_STUCK_DETECTED",
    "EXECUTION_LATENCY_SPIKE_DETECTED",
    "RECOVERY_LOOP_DETECTED",
    "RECONCILIATION_QTY_MISMATCH",  # Position drift (broker vs journal)
    "EXPOSURE_INTEGRITY_VIOLATION",  # Intent vs broker mismatch (critical invariant)
    "ORDER_LIFECYCLE_TRANSITION_INVALID",  # Invalid order state transition
    # Forced flatten lifecycle (notification coverage fix)
    "FORCED_FLATTEN_TRIGGERED",
    "FORCED_FLATTEN_POSITION_CLOSED",
    "SESSION_FORCED_FLATTENED",
    "RECONCILIATION_PASS_SUMMARY",
    "FORCED_FLATTEN_FAILED",
    "FORCED_FLATTEN_EXPOSURE_REMAINING",
    "REENTRY_FAILED",
    "REENTRY_PROTECTION_FAILED",
    "EXECUTION_JOURNAL_CORRUPTION",
    "EXECUTION_JOURNAL_ERROR",
}
