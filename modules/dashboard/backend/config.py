"""
Backend configuration constants
"""
import json
import os
from pathlib import Path

# Configuration
QTSW2_ROOT = Path(__file__).parent.parent.parent

# Schedule config file
SCHEDULE_CONFIG_FILE = QTSW2_ROOT / "configs" / "schedule.json"

# Dashboard configuration constants
# Port configuration
DASHBOARD_PORT = int(os.getenv("DASHBOARD_PORT", "8001"))

# Timeout constants (milliseconds)
API_TIMEOUT_SHORT = 2000   # 2 seconds - for fast-failing endpoints
API_TIMEOUT_DEFAULT = 8000  # 8 seconds - default timeout
API_TIMEOUT_LONG = 30000    # 30 seconds - for long-running operations

# Cache TTL constants (seconds)
FILE_COUNTS_CACHE_TTL = 30  # File counts cache TTL
SNAPSHOT_CACHE_TTL = 15     # Snapshot cache TTL

# Event limits
MAX_EVENTS_IN_UI = 100      # Maximum events displayed in UI
SNAPSHOT_MAX_EVENTS = 100   # Maximum events in snapshot
SNAPSHOT_WINDOW_HOURS = 4.0 # Snapshot time window

# WebSocket snapshot configuration
SNAPSHOT_CHUNK_SIZE = 25    # Events per chunk
SNAPSHOT_LOAD_TIMEOUT = 10  # Timeout for snapshot loading (seconds)

# Polling intervals (milliseconds)
POLL_INTERVAL_IDLE = 60000   # 60 seconds when idle and WebSocket connected
POLL_INTERVAL_RUNNING = 10000  # 10 seconds when running or WebSocket disconnected

# Schedule config loading/saving functions
def load_schedule_config():
    """Load schedule configuration from file."""
    try:
        from .models import ScheduleConfig
    except ImportError:
        from models import ScheduleConfig
    
    if SCHEDULE_CONFIG_FILE.exists():
        with open(SCHEDULE_CONFIG_FILE, "r") as f:
            data = json.load(f)
            return ScheduleConfig(**data)
    return ScheduleConfig(schedule_time="07:30")


def save_schedule_config(config):
    """Save schedule configuration to file."""
    with open(SCHEDULE_CONFIG_FILE, "w") as f:
        json.dump(config.dict(), f, indent=2)
