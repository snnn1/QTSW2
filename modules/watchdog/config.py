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

# Thresholds (from specification)
# ENGINE_TICK_STALL_THRESHOLD_SECONDS: Threshold for detecting engine tick stalls
# ENGINE_TICK_CALLSITE is rate-limited in feed to every 5 seconds
# Set to 15 seconds (3x rate limit) to detect stalls quickly while avoiding false positives
ENGINE_TICK_STALL_THRESHOLD_SECONDS = 15
STUCK_STREAM_THRESHOLD_SECONDS = 300
UNPROTECTED_TIMEOUT_SECONDS = 10
# DATA_STALL_THRESHOLD_SECONDS must be > rate limit of bar tracking events
# ONBARUPDATE_CALLED and ENGINE_TICK_HEARTBEAT are rate-limited to 60 seconds
# Set threshold to 90 seconds (1.5x rate limit) to avoid false positives
DATA_STALL_THRESHOLD_SECONDS = 90  # Default, can be configurable per instrument

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
    # Protective Orders
    "PROTECTIVE_ORDERS_SUBMITTED",
    "PROTECTIVE_ORDERS_FAILED_FLATTENED",
    # Intent Exposure
    "INTENT_EXPOSURE_REGISTERED",
    "INTENT_EXPOSURE_CLOSED",
    "INTENT_EXIT_FILL",
    # Data Health
    "DATA_LOSS_DETECTED",
    "DATA_STALL_RECOVERED",
    # Critical Events
    "CRITICAL_EVENT_REPORTED",
    "EXECUTION_GATE_INVARIANT_VIOLATION",
    # Additional events needed for state tracking
    "RANGE_LOCKED",
    "RANGE_LOCK_SNAPSHOT",  # Contains range data for RANGE_LOCKED streams
    "TIMETABLE_VALIDATED",
    # Stream State Machine transitions (plan requirement #2)
    "STREAM_STATE_TRANSITION",
    # Diagnostic events (for troubleshooting OnBarUpdate and bar routing)
    "ONBARUPDATE_CALLED",
    "ONBARUPDATE_DIAGNOSTIC",
    "BAR_ROUTING_DIAGNOSTIC",
}
