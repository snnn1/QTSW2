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
# RECOVERY_TIMEOUT_SECONDS: Maximum time recovery can be in RECOVERY_RUNNING state
# If DISCONNECT_RECOVERY_COMPLETE event is not received within this time, auto-clear recovery state
# Set to 10 minutes - recovery should complete quickly, but allow time for slow reconnections
RECOVERY_TIMEOUT_SECONDS = 600  # 10 minutes

# ENGINE_TICK_MAX_AGE_FOR_INIT_SECONDS: Reject ENGINE_TICK_CALLSITE events older than this when
# initializing from end-of-file. Prevents false ENGINE ALIVE when feed has stale ticks from previous run.
ENGINE_TICK_MAX_AGE_FOR_INIT_SECONDS = 90

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
    # Kill Switch
    "KILL_SWITCH_ACTIVE",
    # Stream State
    "STREAM_STAND_DOWN",
    "MARKET_CLOSE_NO_TRADE",  # Stream commits at market close (treated same as STREAM_STAND_DOWN)
    "RANGE_INVALIDATED",
    # Execution Blocking
    "EXECUTION_BLOCKED",
    "EXECUTION_ALLOWED",
    # Order Lifecycle
    "ORDER_SUBMITTED",
    "ORDER_ACKNOWLEDGED",
    "ORDER_REJECTED",
    "ORDER_CANCELLED",
    "EXECUTION_FILLED",
    "EXECUTION_PARTIAL_FILL",
    "EXECUTION_EXIT_FILL",  # Migration: ledger converts to synthetic EXECUTION_FILLED; robot now emits EXECUTION_FILLED for exits
    "EXECUTION_FILL_BLOCKED_TRADING_DATE_NULL",
    "EXECUTION_FILL_UNMAPPED",
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
    # Diagnostic events (for troubleshooting OnBarUpdate and bar routing)
    "ONBARUPDATE_CALLED",
    "ONBARUPDATE_DIAGNOSTIC",
    "BAR_ROUTING_DIAGNOSTIC",
    # Bar acceptance tracking
    "BAR_ACCEPTED",  # Bar acceptance events (rate-limited in engine)
    "BAR_RECEIVED_NO_STREAMS",  # Bar received before streams created (for data stall detection)
}
