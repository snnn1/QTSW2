"""
Pipeline Configuration

Centralized configuration for the automation pipeline.
All paths and settings should be defined here.
"""

import os
from pathlib import Path
from dataclasses import dataclass
from typing import Optional


@dataclass
class PipelineConfig:
    """
    Configuration for the pipeline system.
    
    All paths are relative to qtsw2_root unless specified as absolute.
    """
    
    # Base paths
    qtsw2_root: Path
    
    # Data directories
    data_raw: Path
    data_translated: Path  # MANDATORY: Translator output location
    analyzer_runs: Path
    
    # Script paths
    merger_script: Path
    parallel_analyzer_script: Path
    
    # Logging
    logs_dir: Path
    event_logs_dir: Path
    
    # Timeouts (in seconds)
    translator_timeout: int = 3600  # 1 hour
    analyzer_timeout: int = 21600  # 6 hours
    merger_timeout: int = 1800  # 30 minutes
    
    @classmethod
    def from_environment(cls, qtsw2_root: Optional[Path] = None) -> 'PipelineConfig':
        """
        Create PipelineConfig from environment variables and defaults.
        
        Args:
            qtsw2_root: Optional root path. If None, uses QTSW2_ROOT env var or default.
        
        Returns:
            PipelineConfig instance
            
        Raises:
            ValueError: If data_translated cannot be determined (MANDATORY)
        """
        # Determine QTSW2 root
        if qtsw2_root is None:
            qtsw2_root_str = os.getenv("QTSW2_ROOT", r"C:\Users\jakej\QTSW2")
            qtsw2_root = Path(qtsw2_root_str)
        else:
            qtsw2_root = Path(qtsw2_root)
        
        # Base paths
        data_raw = qtsw2_root / "data" / "raw"
        data_translated = qtsw2_root / "data" / "translated"
        analyzer_runs = qtsw2_root / "data" / "analyzed"
        
        # Script paths
        merger_script = qtsw2_root / "modules" / "merger" / "merger.py"
        parallel_analyzer_script = qtsw2_root / "ops" / "maintenance" / "run_analyzer_parallel.py"
        
        # Logging directories
        logs_dir = qtsw2_root / "automation" / "logs"
        event_logs_dir = logs_dir / "events"
        
        # Validate mandatory paths
        if data_translated is None or str(data_translated).strip() == "":
            raise ValueError(
                "data_translated is MANDATORY but could not be determined. "
                "This is a configuration error that must be fixed."
            )
        
        return cls(
            qtsw2_root=qtsw2_root,
            data_raw=data_raw,
            data_translated=data_translated,  # MANDATORY
            analyzer_runs=analyzer_runs,
            merger_script=merger_script,
            parallel_analyzer_script=parallel_analyzer_script,
            logs_dir=logs_dir,
            event_logs_dir=event_logs_dir,
            translator_timeout=int(os.getenv("TRANSLATOR_TIMEOUT", "3600")),
            analyzer_timeout=int(os.getenv("ANALYZER_TIMEOUT", "21600")),
            merger_timeout=int(os.getenv("MERGER_TIMEOUT", "1800")),
        )


# Legacy constants for backward compatibility
# These are kept for code that hasn't been migrated to PipelineConfig yet
QTSW_ROOT = Path(os.getenv("QTSW_ROOT", r"C:\Users\jakej\QTSW"))
QTSW2_ROOT = Path(os.getenv("QTSW2_ROOT", r"C:\Users\jakej\QTSW2"))

DATA_RAW = QTSW2_ROOT / "data" / "raw"
DATA_RAW_LOGS = DATA_RAW / "logs"
DATA_TRANSLATED = QTSW2_ROOT / "data" / "translated"  # Canonical location for translated data

ANALYZER_RUNS = QTSW2_ROOT / "data" / "analyzed"
SEQUENCER_RUNS = QTSW2_ROOT / "data" / "sequencer_runs"

LOGS_DIR = QTSW2_ROOT / "automation" / "logs"
EVENT_LOGS_DIR = LOGS_DIR / "events"

TRANSLATOR_SCRIPT = QTSW2_ROOT / "tools" / "translate_raw.py"  # Deprecated
PARALLEL_ANALYZER_SCRIPT = QTSW2_ROOT / "ops" / "maintenance" / "run_analyzer_parallel.py"
DATA_MERGER_SCRIPT = QTSW2_ROOT / "modules" / "merger" / "merger.py"

TRANSLATOR_TIMEOUT = 3600
ANALYZER_TIMEOUT = 21600
MERGER_TIMEOUT = 1800

DEFAULT_INSTRUMENTS = ["ES", "NQ", "YM", "CL", "NG", "GC"]
SCHEDULE_INTERVAL_MINUTES = 15

RAW_FILE_PATTERN = "*.csv"
PROCESSED_FILE_PATTERNS = ["*.parquet", "*.csv"]
