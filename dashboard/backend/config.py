"""
Configuration constants for the dashboard backend
"""
import json
from pathlib import Path
from .models import ScheduleConfig

# Root directory
QTSW2_ROOT = Path(__file__).parent.parent.parent

# Script paths
SCHEDULER_SCRIPT = QTSW2_ROOT / "automation" / "daily_data_pipeline_scheduler.py"
DATA_MERGER_SCRIPT = QTSW2_ROOT / "tools" / "data_merger.py"
EVENT_LOGS_DIR = QTSW2_ROOT / "automation" / "logs" / "events"
EVENT_LOGS_DIR.mkdir(parents=True, exist_ok=True)

# Streamlit app scripts
TRANSLATOR_APP = QTSW2_ROOT / "scripts" / "translate_raw_app.py"
ANALYZER_APP = QTSW2_ROOT / "scripts" / "breakout_analyzer" / "analyzer_app" / "app.py"
SEQUENTIAL_APP = QTSW2_ROOT / "sequential_processor" / "sequential_processor_app.py"

# Master Matrix and Timetable Engine scripts
MASTER_MATRIX_SCRIPT = QTSW2_ROOT / "run_matrix_and_timetable.py"
MASTER_MATRIX_MODULE = QTSW2_ROOT / "master_matrix" / "master_matrix.py"
TIMETABLE_MODULE = QTSW2_ROOT / "timetable_engine" / "timetable_engine.py"

# Schedule config file
SCHEDULE_CONFIG_FILE = QTSW2_ROOT / "automation" / "schedule_config.json"

# Logging
LOG_FILE_PATH = QTSW2_ROOT / "logs" / "backend_debug.log"
LOG_FILE_PATH.parent.mkdir(parents=True, exist_ok=True)

# CORS origins
CORS_ORIGINS = [
    "http://localhost:5173",
    "http://localhost:5174",
    "http://localhost:3000",
    "http://192.168.1.171:5174"
]


def load_schedule_config() -> ScheduleConfig:
    """Load schedule configuration from file."""
    if SCHEDULE_CONFIG_FILE.exists():
        with open(SCHEDULE_CONFIG_FILE, "r") as f:
            data = json.load(f)
            return ScheduleConfig(**data)
    return ScheduleConfig(schedule_time="07:30")


def save_schedule_config(config: ScheduleConfig):
    """Save schedule configuration to file."""
    with open(SCHEDULE_CONFIG_FILE, "w") as f:
        json.dump(config.dict(), f, indent=2)


