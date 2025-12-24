"""
Backend configuration constants
"""
import json
from pathlib import Path

# Configuration
QTSW2_ROOT = Path(__file__).parent.parent.parent

# Schedule config file
SCHEDULE_CONFIG_FILE = QTSW2_ROOT / "configs" / "schedule.json"

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
