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
ENGINE_TICK_STALL_THRESHOLD_SECONDS = 120
STUCK_STREAM_THRESHOLD_SECONDS = 300
UNPROTECTED_TIMEOUT_SECONDS = 10
DATA_STALL_THRESHOLD_SECONDS = 60  # Default, can be configurable per instrument

# Update frequencies (seconds)
ENGINE_ALIVE_UPDATE_FREQUENCY = 5
STREAM_STUCK_UPDATE_FREQUENCY = 10
RISK_GATE_UPDATE_FREQUENCY = 5
WATCHDOG_STATUS_UPDATE_FREQUENCY = 5
UNPROTECTED_POSITION_UPDATE_FREQUENCY = 2

# Live-critical event types (from specification section 5.1)
LIVE_CRITICAL_EVENT_TYPES = {
    # Engine Health
    "ENGINE_START",
    "ENGINE_STOP",
    "ENGINE_TICK_HEARTBEAT",
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
    "TIMETABLE_VALIDATED",
    # Stream State Machine transitions (plan requirement #2)
    "STREAM_STATE_TRANSITION",
}
