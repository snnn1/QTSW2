"""
Configuration - All paths, timeouts, and environment settings

Single Responsibility: Hold all configuration constants
No hardcoded paths in other modules
"""

from pathlib import Path
from typing import List
import os


# Base paths
QTSW_ROOT = Path(r"C:\Users\jakej\QTSW")
QTSW2_ROOT = Path(r"C:\Users\jakej\QTSW2")

# Data directories
DATA_RAW = QTSW2_ROOT / "data" / "raw"
DATA_RAW_LOGS = DATA_RAW / "logs"
DATA_PROCESSED = QTSW2_ROOT / "data" / "processed"
ANALYZER_RUNS = QTSW2_ROOT / "data" / "analyzer_runs"
SEQUENCER_RUNS = QTSW2_ROOT / "data" / "sequencer_runs"

# Log directories
LOGS_DIR = QTSW2_ROOT / "automation" / "logs"
EVENT_LOGS_DIR = LOGS_DIR / "events"

# Pipeline scripts
TRANSLATOR_SCRIPT = QTSW2_ROOT / "tools" / "translate_raw.py"
PARALLEL_ANALYZER_SCRIPT = QTSW2_ROOT / "tools" / "run_analyzer_parallel.py"
DATA_MERGER_SCRIPT = QTSW2_ROOT / "tools" / "data_merger.py"

# Timeouts (in seconds)
TRANSLATOR_TIMEOUT = 3600  # 1 hour
ANALYZER_TIMEOUT = 21600   # 6 hours per instrument
MERGER_TIMEOUT = 1800      # 30 minutes
PROCESS_NO_OUTPUT_TIMEOUT = 300  # 5 minutes
PROCESS_COMPLETION_TIMEOUT = 30  # 30 seconds after completion detected

# Progress intervals
PROGRESS_UPDATE_INTERVAL = 60  # 1 minute

# Default instruments
DEFAULT_INSTRUMENTS: List[str] = ["ES", "NQ", "YM", "CL", "NG", "GC"]

# Scheduling
SCHEDULE_INTERVAL_MINUTES = 15
SCHEDULE_TIMES = [0, 15, 30, 45]  # Minutes past the hour

# File patterns
RAW_FILE_PATTERN = "*.csv"
PROCESSED_FILE_PATTERNS = ["*.parquet", "*.csv"]

# Lock file settings
LOCK_TIMEOUT_SECONDS = 300  # 5 minutes
LOCK_FILE_NAME = ".pipeline.lock"

# Ensure directories exist
LOGS_DIR.mkdir(parents=True, exist_ok=True)
EVENT_LOGS_DIR.mkdir(parents=True, exist_ok=True)


class PipelineConfig:
    """
    Configuration container - can be extended for environment-based config
    """
    
    def __init__(
        self,
        qtsw2_root: Path = QTSW2_ROOT,
        data_raw: Path = DATA_RAW,
        data_processed: Path = DATA_PROCESSED,
        analyzer_runs: Path = ANALYZER_RUNS,
        logs_dir: Path = LOGS_DIR,
        event_logs_dir: Path = EVENT_LOGS_DIR,
        translator_script: Path = TRANSLATOR_SCRIPT,
        parallel_analyzer_script: Path = PARALLEL_ANALYZER_SCRIPT,
        merger_script: Path = DATA_MERGER_SCRIPT,
        translator_timeout: int = TRANSLATOR_TIMEOUT,
        analyzer_timeout: int = ANALYZER_TIMEOUT,
        merger_timeout: int = MERGER_TIMEOUT,
        default_instruments: List[str] = None,
        schedule_interval_minutes: int = SCHEDULE_INTERVAL_MINUTES
    ):
        self.qtsw2_root = qtsw2_root
        self.data_raw = data_raw
        self.data_processed = data_processed
        self.analyzer_runs = analyzer_runs
        self.logs_dir = logs_dir
        self.event_logs_dir = event_logs_dir
        self.translator_script = translator_script
        self.parallel_analyzer_script = parallel_analyzer_script
        self.merger_script = merger_script
        self.translator_timeout = translator_timeout
        self.analyzer_timeout = analyzer_timeout
        self.merger_timeout = merger_timeout
        self.default_instruments = default_instruments or DEFAULT_INSTRUMENTS.copy()
        self.schedule_interval_minutes = schedule_interval_minutes
        
        # Ensure directories exist
        self.logs_dir.mkdir(parents=True, exist_ok=True)
        self.event_logs_dir.mkdir(parents=True, exist_ok=True)
    
    @classmethod
    def from_environment(cls) -> 'PipelineConfig':
        """Create config from environment variables (for future use)"""
        # For now, return default config
        # Can be extended to read from env vars
        return cls()
    
    def get_lock_file(self, run_id: str) -> Path:
        """Get lock file path for a run"""
        return self.logs_dir / f"{LOCK_FILE_NAME}.{run_id}"



