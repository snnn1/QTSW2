"""
Quantitative Trading System - Daily Data Pipeline Scheduler

Automated scheduler that orchestrates the complete data pipeline:
1. Launches NinjaTrader at scheduled time (07:30 CT)
2. Monitors for data export completion
3. Triggers Translator → Analyzer → Sequential Processor → Stream Matrix

Implements quant-style automation with:
- Deterministic execution
- Comprehensive logging and audit trails
- Health checks and error recovery
- Timezone-aware scheduling (Chicago Time)

Author: Quant Development Environment
Date: 2025
"""

import os
import sys
import time
import logging
import subprocess
import argparse
import re
from pathlib import Path
from datetime import datetime, timedelta
from typing import Optional, List, Dict
import json
import uuid

# Third-party imports
try:
    import pytz
except ImportError:
    print("ERROR: pytz not installed. Install with: pip install pytz")
    sys.exit(1)

try:
    import pandas as pd
except ImportError:
    print("ERROR: pandas not installed. Install with: pip install pandas")
    sys.exit(1)


# ============================================================
# Configuration & Paths
# ============================================================

# Base paths - adjust these to match your system
QTSW_ROOT = Path(r"C:\Users\jakej\QTSW")  # Main workspace
QTSW2_ROOT = Path(r"C:\Users\jakej\QTSW2")  # Data translator workspace

# NinjaTrader configuration
NINJATRADER_EXE = Path(r"C:\Program Files\NinjaTrader 8\bin\NinjaTrader.exe")
NINJATRADER_WORKSPACE = Path(r"C:\Users\jakej\Documents\NinjaTrader 8\workspaces\DataExport.ntworkspace")  # Adjust to your workspace name

# Data directories
# Note: DATA_RAW matches DataExporter output path (QTSW2/data/raw)
DATA_RAW = QTSW2_ROOT / "data" / "raw"
DATA_RAW_LOGS = DATA_RAW / "logs"  # Signal files are stored in logs subfolder
DATA_PROCESSED = QTSW2_ROOT / "data" / "processed"  # QTSW2/data/processed (where files should go)
ANALYZER_RUNS = QTSW2_ROOT / "data" / "analyzer_runs"  # QTSW2/data/analyzer_runs
SEQUENCER_RUNS = QTSW2_ROOT / "data" / "sequencer_runs"  # QTSW2/data/sequencer_runs
LOGS_DIR = QTSW2_ROOT / "automation" / "logs"

# Pipeline scripts (adjust paths as needed)
TRANSLATOR_SCRIPT = QTSW2_ROOT / "scripts" / "translate_raw_app.py"
ANALYZER_SCRIPT = QTSW2_ROOT / "scripts" / "breakout_analyzer" / "scripts" / "run_data_processed.py"
DATA_MERGER_SCRIPT = QTSW2_ROOT / "tools" / "data_merger.py"
SEQUENTIAL_PROCESSOR_SCRIPT = QTSW2_ROOT / "sequential_processor" / "sequential_processor.py"

# Logging configuration
LOGS_DIR.mkdir(parents=True, exist_ok=True)
LOG_FILE = LOGS_DIR / f"pipeline_{datetime.now().strftime('%Y%m%d_%H%M%S')}.log"

# Timezone - Chicago (America/Chicago)
CHICAGO_TZ = pytz.timezone("America/Chicago")

# Structured event logging
EVENT_LOG_PATH = os.getenv("PIPELINE_EVENT_LOG", None)


# ============================================================
# Structured Event Logging
# ============================================================

def emit_event(
    run_id: str,
    stage: str,
    event: str,
    msg: Optional[str] = None,
    data: Optional[Dict] = None
):
    """
    Emit a structured event to the event log file (if configured).
    
    Args:
        run_id: Unique identifier for this pipeline run
        stage: Current pipeline stage (translator, analyzer, sequential, audit, etc.)
        event: Event type (start, metric, success, failure, log)
        msg: Optional message
        data: Optional data dictionary (for metrics, file counts, etc.)
    """
    if EVENT_LOG_PATH is None:
        return
    
    event_obj = {
        "run_id": run_id,
        "stage": stage,
        "event": event,
        "timestamp": datetime.now(CHICAGO_TZ).isoformat()
    }
    
    if msg is not None:
        event_obj["msg"] = msg
    
    if data is not None:
        event_obj["data"] = data
    
    try:
        with open(EVENT_LOG_PATH, "a", encoding="utf-8") as f:
            f.write(json.dumps(event_obj) + "\n")
            f.flush()
    except Exception as e:
        # Don't fail pipeline if event logging fails
        print(f"Warning: Failed to write event log: {e}")


# ============================================================
# Logging Setup
# ============================================================

def setup_logging(log_file: Path, debug_window=None) -> logging.Logger:
    """
    Configure structured logging with file, console, and optional debug window output.
    
    Args:
        log_file: Path to log file
        debug_window: Optional DebugLogWindow instance for GUI logging
    """
    logger = logging.getLogger("PipelineScheduler")
    logger.setLevel(logging.INFO)
    
    # Remove existing handlers
    logger.handlers.clear()
    
    # File handler
    file_handler = logging.FileHandler(log_file)
    file_handler.setLevel(logging.INFO)
    file_formatter = logging.Formatter(
        '%(asctime)s | %(levelname)s | %(message)s',
        datefmt='%Y-%m-%d %H:%M:%S'
    )
    file_handler.setFormatter(file_formatter)
    
    # Console handler
    console_handler = logging.StreamHandler()
    console_handler.setLevel(logging.INFO)
    console_formatter = logging.Formatter(
        '%(levelname)s | %(message)s'
    )
    console_handler.setFormatter(console_formatter)
    
    logger.addHandler(file_handler)
    logger.addHandler(console_handler)
    
    # Debug window handler (if provided)
    if debug_window:
        try:
            import sys
            from pathlib import Path
            # Add automation directory to path for import
            automation_dir = Path(__file__).parent
            if str(automation_dir) not in sys.path:
                sys.path.insert(0, str(automation_dir))
            from debug_log_window import DebugLogHandler
            debug_handler = DebugLogHandler(debug_window)
            debug_handler.setLevel(logging.INFO)
            debug_formatter = logging.Formatter(
                '%(message)s'
            )
            debug_handler.setFormatter(debug_formatter)
            logger.addHandler(debug_handler)
        except Exception as e:
            print(f"Warning: Could not add debug window handler: {e}")
    
    return logger


# ============================================================
# NinjaTrader Control
# ============================================================

class NinjaTraderController:
    """Manages NinjaTrader process lifecycle and monitoring."""
    
    def __init__(self, logger: logging.Logger):
        self.logger = logger
        self.process: Optional[subprocess.Popen] = None
        self.workspace_path = NINJATRADER_WORKSPACE
        
    def launch(self) -> bool:
        """Launch NinjaTrader with saved workspace."""
        try:
            if not NINJATRADER_EXE.exists():
                self.logger.error(f"NinjaTrader not found at: {NINJATRADER_EXE}")
                return False
                
            if not self.workspace_path.exists():
                self.logger.warning(f"Workspace not found: {self.workspace_path}")
                self.logger.info("Launching NinjaTrader without workspace (you'll need to open workspace manually)")
                command = [str(NINJATRADER_EXE)]
            else:
                self.logger.info(f"Launching NinjaTrader with workspace: {self.workspace_path.name}")
                command = [str(NINJATRADER_EXE), str(self.workspace_path)]
            
            self.logger.info(f"Executing: {' '.join(command)}")
            self.process = subprocess.Popen(
                command,
                cwd=str(NINJATRADER_EXE.parent),
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE
            )
            
            self.logger.info(f"NinjaTrader launched (PID: {self.process.pid})")
            time.sleep(5)  # Give it time to start
            return True
            
        except Exception as e:
            self.logger.error(f"Failed to launch NinjaTrader: {e}")
            return False
    
    def is_running(self) -> bool:
        """Check if NinjaTrader process is still running."""
        if self.process is None:
            return False
        return self.process.poll() is None
    
    def wait_for_export(self, timeout_minutes: int = 60, run_id: Optional[str] = None) -> bool:
        """
        Wait for data export to complete by monitoring data_raw directory.
        
        Enhanced detection:
        - Checks for completion signal files (export_complete_*.json)
        - Monitors progress files (export_progress_*.json)
        - Falls back to file modification time detection
        - Detects stalled exports (file not growing)
        
        Args:
            timeout_minutes: Maximum time to wait (default 60 minutes)
            
        Returns:
            True if export detected and completed, False if timeout
        """
        self.logger.info(f"Monitoring {DATA_RAW} for exports (timeout: {timeout_minutes} min)")
        
        # Get initial state
        initial_files = self._get_csv_files()
        initial_count = len(initial_files)
        initial_times = {f: f.stat().st_mtime for f in initial_files}
        initial_sizes = {f: f.stat().st_size for f in initial_files}
        
        # Get initial completion signals (from logs subfolder)
        initial_completion_signals = set(DATA_RAW_LOGS.glob("export_complete_*.json")) if DATA_RAW_LOGS.exists() else set()
        
        # Get initial start signals to detect new exports (from logs subfolder)
        initial_start_signals = set(DATA_RAW_LOGS.glob("export_start_*.json")) if DATA_RAW_LOGS.exists() else set()
        
        self.logger.info(f"Initial file count: {initial_count}")
        if initial_count > 0:
            self.logger.info(f"Most recent file: {max(initial_times.items(), key=lambda x: x[1])[0].name}")
        
        start_time = time.time()
        timeout_seconds = timeout_minutes * 60
        check_interval = 10  # Check every 10 seconds (more frequent for better detection)
        last_size_check = {}  # Track file sizes to detect stalled exports
        stalled_check_interval = 300  # Check for stalled exports every 5 minutes
        last_progress_emission = {}  # Track last progress emission to avoid spam
        export_started_emitted = False
        
        while (time.time() - start_time) < timeout_seconds:
            # Check for start signals (export beginning) - from logs subfolder
            current_start_signals = set(DATA_RAW_LOGS.glob("export_start_*.json")) if DATA_RAW_LOGS.exists() else set()
            new_start_signals = current_start_signals - initial_start_signals
            
            if new_start_signals and not export_started_emitted and run_id:
                export_started_emitted = True
                for signal_file in new_start_signals:
                    try:
                        with open(signal_file, "r") as f:
                            start_data = json.load(f)
                            instrument = start_data.get('instrument', 'unknown')
                            data_type = start_data.get('dataType', 'unknown')
                            emit_event(run_id, "export", "start", f"Export started for {instrument} ({data_type})", {
                                "instrument": instrument,
                                "dataType": data_type
                            })
                            self.logger.info(f"Export start signal detected: {instrument} ({data_type})")
                    except Exception as e:
                        self.logger.warning(f"Could not read start signal {signal_file.name}: {e}")
            
            # Check for completion signals first (most reliable) - from logs subfolder
            current_completion_signals = set(DATA_RAW_LOGS.glob("export_complete_*.json")) if DATA_RAW_LOGS.exists() else set()
            new_completion_signals = current_completion_signals - initial_completion_signals
            
            if new_completion_signals:
                for signal_file in new_completion_signals:
                    try:
                        with open(signal_file, "r") as f:
                            signal_data = json.load(f)
                            self.logger.info(f"Export completion signal detected: {signal_data.get('fileName', 'unknown')}")
                            self.logger.info(f"  Instrument: {signal_data.get('instrument', 'unknown')}")
                            self.logger.info(f"  Records: {signal_data.get('totalBarsProcessed', 0):,}")
                            self.logger.info(f"  File size: {signal_data.get('fileSizeMB', 0):.2f} MB")
                            
                            # Emit completion event
                            if run_id:
                                emit_event(run_id, "export", "success", 
                                    f"Export completed: {signal_data.get('totalBarsProcessed', 0):,} records, {signal_data.get('fileSizeMB', 0):.2f} MB",
                                    {
                                        "instrument": signal_data.get('instrument', 'unknown'),
                                        "dataType": signal_data.get('dataType', 'unknown'),
                                        "totalBarsProcessed": signal_data.get('totalBarsProcessed', 0),
                                        "fileSizeMB": signal_data.get('fileSizeMB', 0),
                                        "gapsDetected": signal_data.get('gapsDetected', 0),
                                        "invalidDataSkipped": signal_data.get('invalidDataSkipped', 0),
                                        "fileName": signal_data.get('fileName', 'unknown')
                                    })
                    except Exception as e:
                        self.logger.warning(f"Could not read completion signal {signal_file.name}: {e}")
                
                return True
            
            # Check for progress files (export in progress) - from logs subfolder
            progress_files = list(DATA_RAW_LOGS.glob("export_progress_*.json")) if DATA_RAW_LOGS.exists() else []
            if progress_files:
                # Read most recent progress file
                latest_progress = max(progress_files, key=lambda p: p.stat().st_mtime)
                try:
                    with open(latest_progress, "r") as f:
                        progress_data = json.load(f)
                        records = progress_data.get("totalBarsProcessed", 0)
                        file_size_mb = progress_data.get("fileSizeMB", 0)
                        instrument = progress_data.get("instrument", "unknown")
                        
                        # Emit progress event (throttle to avoid spam - only every 30 seconds per file)
                        if run_id:
                            progress_key = str(latest_progress)
                            last_emit_time = last_progress_emission.get(progress_key, 0)
                            current_time = time.time()
                            
                            if current_time - last_emit_time >= 30:  # Emit at most every 30 seconds
                                last_progress_emission[progress_key] = current_time
                                
                                # Count files
                                csv_files = self._get_csv_files()
                                file_count = len(csv_files)
                                
                                emit_event(run_id, "export", "metric",
                                    f"Export in progress: {records:,} records, {file_size_mb:.2f} MB",
                                    {
                                        "instrument": instrument,
                                        "dataType": progress_data.get("dataType", "unknown"),
                                        "totalBarsProcessed": records,
                                        "fileSizeMB": file_size_mb,
                                        "fileCount": file_count,
                                        "gapsDetected": progress_data.get("gapsDetected", 0),
                                        "invalidDataSkipped": progress_data.get("invalidDataSkipped", 0)
                                    })
                        
                        self.logger.info(f"Export in progress: {records:,} records, {file_size_mb:.2f} MB")
                except Exception as e:
                    pass  # Ignore read errors
            
            # Check for new CSV files
            current_files = self._get_csv_files()
            current_count = len(current_files)
            
            new_files = set(current_files) - set(initial_files)
            if new_files:
                self.logger.info(f"New export file detected: {len(new_files)} file(s)")
                for new_file in new_files:
                    file_size_mb = new_file.stat().st_size / 1024 / 1024
                    self.logger.info(f"  - {new_file.name} ({file_size_mb:.2f} MB)")
                    last_size_check[new_file] = new_file.stat().st_size
                
                # Wait a bit to see if file is still being written
                time.sleep(5)
                # Check if file is still growing (export in progress)
                for new_file in new_files:
                    current_size = new_file.stat().st_size
                    if current_size > last_size_check.get(new_file, 0):
                        self.logger.info(f"  {new_file.name} is still being written, waiting for completion...")
                        last_size_check[new_file] = current_size
                    else:
                        # File stopped growing, might be complete
                        # Check for completion signal (in logs subfolder)
                        completion_pattern = f"export_complete_{new_file.stem}.json"
                        completion_signal = DATA_RAW_LOGS / completion_pattern
                        if completion_signal.exists():
                            self.logger.info(f"Export complete: {new_file.name}")
                            return True
                        else:
                            # File exists but no completion signal - might still be writing
                            # Continue monitoring
                            pass
            
            # Check if existing files were modified recently
            for file_path in current_files:
                if file_path not in initial_files or file_path.stat().st_mtime > initial_times.get(file_path, 0):
                    mod_time = datetime.fromtimestamp(file_path.stat().st_mtime)
                    time_since_mod = (datetime.now() - mod_time).total_seconds()
                    
                    if time_since_mod < 120:  # Modified within last 2 minutes
                        self.logger.info(f"Export file recently modified: {file_path.name}")
                        # Check for completion signal (in logs subfolder)
                        completion_pattern = f"export_complete_{file_path.stem}.json"
                        completion_signal = DATA_RAW_LOGS / completion_pattern
                        if completion_signal.exists():
                            return True
                        return True  # File modified, assume export in progress
            
            # Check for stalled exports (file not growing)
            elapsed = int(time.time() - start_time)
            if elapsed % stalled_check_interval == 0 and last_size_check:
                for file_path, last_size in list(last_size_check.items()):
                    if file_path.exists():
                        current_size = file_path.stat().st_size
                        if current_size == last_size:
                            # File hasn't grown - check if there's a completion signal (in logs subfolder)
                            completion_pattern = f"export_complete_{file_path.stem}.json"
                            completion_signal = DATA_RAW_LOGS / completion_pattern
                            if completion_signal.exists():
                                self.logger.info(f"Export completed: {file_path.name}")
                                return True
                            else:
                                # File stalled but no completion signal - might be an error
                                self.logger.warning(f"Export may have stalled: {file_path.name} (no growth in 5 min)")
                                if run_id:
                                    emit_event(run_id, "export", "failure",
                                        f"Export may have stalled: {file_path.name} (no growth in 5 minutes)",
                                        {
                                            "fileName": file_path.name,
                                            "fileSizeMB": file_path.stat().st_size / (1024 * 1024)
                                        })
                        else:
                            last_size_check[file_path] = current_size
            
            # Log progress
            if elapsed % 300 == 0:  # Log every 5 minutes
                self.logger.info(f"Still waiting... ({elapsed // 60} min elapsed)")
            
            time.sleep(check_interval)
        
        self.logger.warning(f"Timeout reached ({timeout_minutes} min) - no export completion detected")
        if run_id:
            emit_event(run_id, "export", "failure",
                f"Export timeout: no completion detected after {timeout_minutes} minutes",
                {"timeoutMinutes": timeout_minutes})
        return False
    
    def _get_csv_files(self) -> List[Path]:
        """Get all CSV export files from data_raw directory."""
        if not DATA_RAW.exists():
            return []
        
        # Look for all CSV export patterns (MinuteDataExport, TickDataExport, DataExport)
        files = list(DATA_RAW.glob("MinuteDataExport_*.csv"))
        files.extend(DATA_RAW.glob("TickDataExport_*.csv"))
        files.extend(DATA_RAW.glob("DataExport_*.csv"))
        # Exclude files in subdirectories (like logs folder)
        files = [f for f in files if f.parent == DATA_RAW]
        return sorted(files, key=lambda x: x.stat().st_mtime, reverse=True)


# ============================================================
# Pipeline Orchestration
# ============================================================

class PipelineOrchestrator:
    """Orchestrates the complete data processing pipeline."""
    
    def __init__(self, logger: logging.Logger, run_id: str):
        self.logger = logger
        self.run_id = run_id
        self.stage_results: Dict[str, bool] = {}
        
    def run_translator(self) -> bool:
        """Stage 1: Run data translator to process raw CSV files."""
        self.logger.info("=" * 60)
        self.logger.info("STAGE 1: Data Translator")
        self.logger.info("=" * 60)
        
        emit_event(self.run_id, "translator", "start", "Starting data translator stage")
        
        try:
            # Check if raw files exist
            raw_files = list(DATA_RAW.glob("*.csv"))
            # Exclude files in subdirectories (like logs folder)
            raw_files = [f for f in raw_files if f.parent == DATA_RAW]
            if not raw_files:
                self.logger.warning("No raw CSV files found in data_raw/")
                emit_event(self.run_id, "translator", "failure", "No raw CSV files found")
                return False
            
            self.logger.info(f"Found {len(raw_files)} raw file(s) to process")
            emit_event(self.run_id, "translator", "metric", "Files found", {"raw_file_count": len(raw_files)})
            
            # Run translator via CLI (non-interactive)
            # Note: This assumes you have a CLI version or can run translator headless
            # Adjust this to match your actual translator interface
            # Ensure paths are absolute and exist
            input_path = DATA_RAW.resolve()
            output_path = Path(DATA_PROCESSED).resolve()
            
            if not input_path.exists():
                self.logger.error(f"Input folder does not exist: {input_path}")
                emit_event(self.run_id, "translator", "failure", f"Input folder does not exist: {input_path}")
                return False
            
            translator_cmd = [
                sys.executable,
                str(QTSW2_ROOT / "tools" / "translate_raw.py"),
                "--input", str(input_path),
                "--output", str(output_path),
                "--separate-years",
                "--no-merge"  # Process each file separately
            ]
            
            self.logger.info(f"Executing: {' '.join(translator_cmd)}")
            emit_event(self.run_id, "translator", "log", f"Starting translation of {len(raw_files)} file(s)")
            
            # Run translator with timeout and capture output
            try:
                result = subprocess.run(
                    translator_cmd,
                    cwd=str(QTSW2_ROOT),
                    capture_output=True,
                    text=True,
                    timeout=3600  # 1 hour timeout
                )
                
                # Log output for debugging
                if result.stdout:
                    self.logger.info("Translator output:")
                    for line in result.stdout.split('\n'):
                        if line.strip():
                            self.logger.info(f"  {line}")
                            # Emit progress events for key milestones
                            if any(keyword in line for keyword in ["Processing:", "Loaded:", "Saving", "rows", "completed"]):
                                # Only emit if line has content
                                if line.strip():
                                    emit_event(self.run_id, "translator", "log", line.strip())
                            # Emit event when a year file is written
                            if "Saved:" in line or "Saved PARQUET:" in line or "Saved CSV:" in line:
                                # Extract file info from line like "Saved: ES_2024.parquet" or "Saved PARQUET: ES_2024_file.parquet (1,234 rows)"
                                emit_event(self.run_id, "translator", "log", f"File written: {line.strip()}")
                                # Also emit as metric for tracking
                                if ".parquet" in line:
                                    # Try to extract year and instrument from filename
                                    match = re.search(r'([A-Z]+)_(\d{4})', line)
                                    if match:
                                        instrument = match.group(1)
                                        year = match.group(2)
                                        emit_event(self.run_id, "translator", "metric", f"Wrote {instrument} {year} file", {
                                            "instrument": instrument,
                                            "year": year,
                                            "file_type": "parquet"
                                        })
                
                if result.stderr:
                    self.logger.warning("Translator warnings/errors:")
                    for line in result.stderr.split('\n'):
                        if line.strip():
                            self.logger.warning(f"  {line}")
                            
            except subprocess.TimeoutExpired:
                self.logger.error("✗ Translator timed out after 1 hour")
                emit_event(self.run_id, "translator", "failure", "Translator timed out after 1 hour")
                self.stage_results["translator"] = False
                return False
            except Exception as e:
                self.logger.error(f"✗ Translator exception: {e}")
                emit_event(self.run_id, "translator", "failure", f"Translator exception: {str(e)}")
                self.stage_results["translator"] = False
                return False
            
            if result.returncode == 0:
                self.logger.info("✓ Translator completed successfully")
                self.logger.info(result.stdout[-500:])  # Last 500 chars
                
                # Count processed files
                processed_files = list(DATA_PROCESSED.glob("*.parquet"))
                processed_files.extend(DATA_PROCESSED.glob("*.csv"))
                emit_event(self.run_id, "translator", "metric", "Translation complete", {
                    "processed_file_count": len(processed_files)
                })
                
                # Delete processed raw CSV files
                deleted_count = self._delete_raw_files(raw_files)
                if deleted_count > 0:
                    self.logger.info(f"✓ Deleted {deleted_count} raw CSV file(s)")
                    emit_event(self.run_id, "translator", "metric", "Raw files deleted", {
                        "deleted_file_count": deleted_count
                    })
                
                emit_event(self.run_id, "translator", "success", "Translator completed successfully")
                
                self.stage_results["translator"] = True
                return True
            else:
                self.logger.error(f"✗ Translator failed (code: {result.returncode})")
                if result.stderr:
                    self.logger.error(f"Error output: {result.stderr}")
                emit_event(self.run_id, "translator", "failure", f"Translator failed with code {result.returncode}")
                self.stage_results["translator"] = False
                return False
                
        except Exception as e:
            # This catches any other exceptions not caught above
            self.logger.error(f"✗ Translator exception: {e}")
            emit_event(self.run_id, "translator", "failure", f"Translator exception: {str(e)}")
            self.stage_results["translator"] = False
            return False
    
    def _delete_raw_files(self, raw_files: List[Path]) -> int:
        """Delete processed raw CSV files."""
        deleted_count = 0
        for raw_file in raw_files:
            try:
                raw_file.unlink()
                deleted_count += 1
                self.logger.info(f"  Deleted: {raw_file.name}")
            except Exception as e:
                self.logger.warning(f"  Failed to delete {raw_file.name}: {e}")
                # Continue with other files
        
        return deleted_count
    
    def _delete_processed_files(self, processed_files: List[Path]) -> int:
        """Delete analyzed processed files."""
        deleted_count = 0
        for proc_file in processed_files:
            try:
                proc_file.unlink()
                deleted_count += 1
                self.logger.info(f"  Deleted: {proc_file.name}")
            except Exception as e:
                self.logger.warning(f"  Failed to delete {proc_file.name}: {e}")
                # Continue with other files
        
        return deleted_count
    
    
    def run_analyzer(self, instruments: List[str] = None) -> bool:
        """Stage 2: Run breakout analyzer on processed data."""
        self.logger.info("=" * 60)
        self.logger.info("STAGE 2: Breakout Analyzer")
        self.logger.info("=" * 60)
        
        # Check if processed files exist
        processed_files = []
        if DATA_PROCESSED.exists():
            processed_files = list(DATA_PROCESSED.glob("*.parquet"))
            processed_files.extend(list(DATA_PROCESSED.glob("*.csv")))
        
        if not processed_files:
            self.logger.warning("No processed files found in data/processed - cannot run analyzer")
            self.logger.info(f"  Checked directory: {DATA_PROCESSED}")
            self.logger.info(f"  Directory exists: {DATA_PROCESSED.exists()}")
            emit_event(self.run_id, "analyzer", "failure", "No processed files found in data/processed", {
                "data_folder": str(DATA_PROCESSED),
                "folder_exists": DATA_PROCESSED.exists()
            })
            return False
        
        self.logger.info(f"Found {len(processed_files)} processed file(s) in {DATA_PROCESSED}")
        file_list_msg = f"Found {len(processed_files)} processed file(s) ready for analysis:\n"
        for i, proc_file in enumerate(processed_files[:10], 1):  # Show first 10 files
            file_size_mb = proc_file.stat().st_size / (1024 * 1024)
            self.logger.info(f"  [{i}] {proc_file.name} ({file_size_mb:.2f} MB)")
            file_list_msg += f"  [{i}] {proc_file.name} ({file_size_mb:.2f} MB)\n"
        if len(processed_files) > 10:
            self.logger.info(f"  ... and {len(processed_files) - 10} more file(s)")
            file_list_msg += f"  ... and {len(processed_files) - 10} more file(s)\n"
        
        emit_event(self.run_id, "analyzer", "start", "Starting analyzer stage")
        emit_event(self.run_id, "analyzer", "log", file_list_msg.strip())
        
        if instruments is None:
            instruments = ["ES", "NQ", "YM", "CL", "NG", "GC"]
        
        emit_event(self.run_id, "analyzer", "metric", "Analyzer started", {
            "instrument_count": len(instruments),
            "instruments": instruments,
            "processed_file_count": len(processed_files)
        })
        
        success_count = 0
        self.logger.info(f"Starting analyzer for {len(instruments)} instrument(s): {', '.join(instruments)}")
        emit_event(self.run_id, "analyzer", "log", f"Processing {len(instruments)} instrument(s): {', '.join(instruments)}")
        
        for i, instrument in enumerate(instruments, 1):
            try:
                self.logger.info(f"=" * 60)
                self.logger.info(f"Starting analyzer for {instrument} ({i}/{len(instruments)})")
                self.logger.info(f"=" * 60)
                emit_event(self.run_id, "analyzer", "log", f"Starting {instrument} ({i}/{len(instruments)})")
                
                # Build command with all available time slots for both sessions
                # S1 slots: 07:30, 08:00, 09:00
                # S2 slots: 09:30, 10:00, 10:30, 11:00
                analyzer_cmd = [
                    sys.executable,
                    str(ANALYZER_SCRIPT),
                    "--folder", str(DATA_PROCESSED),
                    "--instrument", instrument,
                    "--sessions", "S1", "S2",
                    "--slots", 
                    "S1:07:30", "S1:08:00", "S1:09:00",  # All S1 slots
                    "S2:09:30", "S2:10:00", "S2:10:30", "S2:11:00",  # All S2 slots
                    "--debug"  # Enable debug output to diagnose issues
                ]
                
                self.logger.info(f"Running analyzer for {instrument}...")
                self.logger.info(f"  Command: {' '.join(analyzer_cmd)}")
                self.logger.info(f"  Working directory: {QTSW2_ROOT}")
                self.logger.info(f"  Data folder: {DATA_PROCESSED}")
                self.logger.info(f"  Data folder exists: {DATA_PROCESSED.exists()}")
                emit_event(self.run_id, "analyzer", "log", f"Starting analyzer process for {instrument}")
                # Force flush to ensure output appears immediately (skip handlers that don't support it)
                for handler in self.logger.handlers:
                    try:
                        handler.flush()
                    except (OSError, AttributeError):
                        # Some handlers (like debug window) don't support flush
                        pass
                
                if DATA_PROCESSED.exists():
                    parquet_files = list(DATA_PROCESSED.glob("*.parquet"))
                    csv_files = list(DATA_PROCESSED.glob("*.csv"))
                    total_files = len(parquet_files) + len(csv_files)
                    self.logger.info(f"  Files available for {instrument}: {total_files} total ({len(parquet_files)} parquet, {len(csv_files)} csv)")
                    if parquet_files:
                        self.logger.info(f"  Parquet files: {[f.name for f in parquet_files[:5]]}")
                        if len(parquet_files) > 5:
                            self.logger.info(f"    ... and {len(parquet_files) - 5} more parquet files")
                    if csv_files:
                        self.logger.info(f"  CSV files: {[f.name for f in csv_files[:5]]}")
                        if len(csv_files) > 5:
                            self.logger.info(f"    ... and {len(csv_files) - 5} more csv files")
                    # Emit event with file details
                    emit_event(self.run_id, "analyzer", "log", 
                        f"Files available for {instrument}: {total_files} total ({len(parquet_files)} parquet, {len(csv_files)} csv)")
                    # Force flush (skip handlers that don't support it)
                    for handler in self.logger.handlers:
                        try:
                            handler.flush()
                        except (OSError, AttributeError):
                            # Some handlers (like debug window) don't support flush
                            pass
                else:
                    self.logger.warning(f"  WARNING: Data folder {DATA_PROCESSED} does not exist!")
                    emit_event(self.run_id, "analyzer", "log", f"WARNING: Data folder {DATA_PROCESSED} does not exist!")
                
                emit_event(self.run_id, "analyzer", "log", f"Running analyzer for {instrument}")
                
                # For large files, use Popen to stream output instead of capturing everything in memory
                # This prevents memory issues with very large files
                try:
                    # Set environment variable to indicate this is an automatic pipeline run
                    env = os.environ.copy()
                    env["PIPELINE_RUN"] = "1"
                    
                    process = subprocess.Popen(
                        analyzer_cmd,
                        cwd=str(QTSW2_ROOT),
                        stdout=subprocess.PIPE,
                        stderr=subprocess.PIPE,
                        text=True,
                        bufsize=1,  # Line buffered
                        env=env  # Pass environment with PIPELINE_RUN flag
                    )
                    # Process started - no need to log PID unless debugging
                except Exception as e:
                    self.logger.error(f"[{instrument}] Failed to start analyzer process: {e}")
                    emit_event(self.run_id, "analyzer", "failure", f"[{instrument}] Failed to start analyzer: {str(e)}")
                    raise
                
                # Stream output in real-time and collect last portion for error reporting
                stdout_lines = []
                stderr_lines = []
                max_lines_to_keep = 100  # Keep last 100 lines of output
                
                # Read output streams
                def read_stream(stream, lines_list, stream_name):
                    try:
                        for line in iter(stream.readline, ''):
                            if line:
                                lines_list.append(line.rstrip())
                                # Keep only last N lines to prevent memory issues
                                if len(lines_list) > max_lines_to_keep:
                                    lines_list.pop(0)
                                
                                # Log only critical milestones - minimal verbosity
                                line_upper = line.upper()
                                line_stripped = line.rstrip()
                                
                                # Skip verbose messages
                                skip_patterns = [
                                    'TRADE EXECUTION SUMMARY',
                                    'PROCESSING RANGE',
                                    'PROGRESS:',
                                    'LOADING FILE',
                                    'LOADED',
                                    'CONCATENATING',
                                    'CONCATENATED',
                                    'MEMORY USAGE',
                                    'PROCESSING DATE',  # Only log completion, not processing start
                                    'STARTING TRADE EXECUTION',
                                    'RANGES FOUND',
                                    'PROCESSING RANGE'
                                ]
                                
                                if any(pattern in line_upper for pattern in skip_patterns):
                                    continue  # Skip verbose messages
                                
                                # Only log critical information - errors, warnings, and completion
                                # Skip "Found X parquet file(s) to load" type messages
                                if 'FOUND' in line_upper and ('TO LOAD' in line_upper or 'PARQUET FILE' in line_upper):
                                    continue
                                
                                # Log only essential messages
                                if any(keyword in line_upper for keyword in [
                                    'ERROR',
                                    'WARNING', 
                                    'COMPLETED',  # Completion messages
                                    'WRITTEN',    # File written confirmation
                                    'WROTE',      # File written confirmation
                                ]):
                                    self.logger.info(f"[{instrument}] {line_stripped}")
                                    # Also emit as event for dashboard
                                    # Only emit if line is not empty after stripping
                                    if line_stripped and line_stripped.strip():
                                        emit_event(self.run_id, "analyzer", "log", f"[{instrument}] {line_stripped}")
                    except Exception as e:
                        self.logger.error(f"Error reading {stream_name} stream: {e}")
                
                import threading
                stdout_thread = threading.Thread(target=read_stream, args=(process.stdout, stdout_lines, "stdout"), daemon=True)
                stderr_thread = threading.Thread(target=read_stream, args=(process.stderr, stderr_lines, "stderr"), daemon=True)
                stdout_thread.start()
                stderr_thread.start()
                
                # Wait for process with extended timeout for large files
                # Use polling to implement timeout since Popen.wait() doesn't support timeout
                import time
                start_time = time.time()
                timeout_seconds = 21600  # 6 hour timeout per instrument for very large files
                last_progress_log = 0
                progress_interval = 300  # Log progress every 5 minutes
                
                while process.poll() is None:
                    elapsed = time.time() - start_time
                    
                    # Log progress periodically for long-running operations
                    if elapsed - last_progress_log >= progress_interval:
                        elapsed_minutes = int(elapsed / 60)
                        self.logger.info(f"[{instrument}] Analyzer still running... ({elapsed_minutes} minutes elapsed)")
                        emit_event(self.run_id, "analyzer", "metric", f"{instrument} analyzer running ({elapsed_minutes} min)", {
                            "instrument": instrument,
                            "elapsed_minutes": elapsed_minutes
                        })
                        last_progress_log = elapsed
                    
                    if elapsed > timeout_seconds:
                        self.logger.error(f"✗ {instrument} analyzer timed out after 6 hours")
                        emit_event(self.run_id, "analyzer", "failure", f"{instrument} analyzer timed out after 6 hours", {
                            "instrument": instrument
                        })
                        process.kill()
                        process.wait()
                        returncode = -1
                        stderr_lines.append("Process timed out after 6 hours")
                        break
                    time.sleep(1)  # Check every second
                else:
                    returncode = process.returncode
                    elapsed_minutes = int((time.time() - start_time) / 60)
                    if elapsed_minutes > 1:  # Only log if it took more than 1 minute
                        self.logger.info(f"[{instrument}] Analyzer completed in {elapsed_minutes} minutes")
                
                # Wait for output threads to finish
                stdout_thread.join(timeout=5)
                stderr_thread.join(timeout=5)
                
                # Only log summary if there's an issue
                if returncode != 0 or (not stdout_lines and not stderr_lines):
                    self.logger.warning(f"[{instrument}] Analyzer output summary:")
                    self.logger.warning(f"[{instrument}]   Captured {len(stdout_lines)} stdout lines, {len(stderr_lines)} stderr lines")
                
                # Create result-like object
                class Result:
                    def __init__(self, returncode, stdout, stderr):
                        self.returncode = returncode
                        self.stdout = stdout
                        self.stderr = stderr
                
                result = Result(
                    returncode=returncode,
                    stdout='\n'.join(stdout_lines[-500:]) if stdout_lines else '',  # Last 500 lines
                    stderr='\n'.join(stderr_lines[-1000:]) if stderr_lines else ''  # Last 1000 lines
                )
                
                # Log full output if process failed or returned no output
                if returncode != 0 or (not stdout_lines and not stderr_lines):
                    self.logger.warning(f"[{instrument}] Analyzer returned code {returncode} with no output captured")
                    if result.stdout:
                        self.logger.info(f"[{instrument}] Full stdout:\n{result.stdout}")
                    if result.stderr:
                        self.logger.info(f"[{instrument}] Full stderr:\n{result.stderr}")
                
                # Log analyzer output - all important lines at INFO level
                if result.stdout:
                    self.logger.info(f"  Analyzer output for {instrument}:")
                    for line in result.stdout.split('\n'):
                        if line.strip():
                            # Log all lines, but highlight important ones
                            if any(keyword in line.lower() for keyword in ['available instruments', 'rows matching', 'no results', 'wrote', 'error', 'loaded', 'date range', 'data details']):
                                self.logger.info(f"    >>> {line}")
                            else:
                                self.logger.info(f"    {line}")
                    # Force flush after output (skip handlers that don't support it)
                    for handler in self.logger.handlers:
                        try:
                            handler.flush()
                        except (OSError, AttributeError):
                            # Some handlers (like debug window) don't support flush
                            pass
                
                if result.stderr:
                    self.logger.warning(f"  Analyzer stderr for {instrument}:")
                    for line in result.stderr.split('\n'):
                        if line.strip():
                            self.logger.warning(f"    {line}")
                
                if result.returncode == 0:
                    # Check if results were actually generated
                    output_lines = result.stdout.split('\n') if result.stdout else []
                    has_results = any('rows to' in line.lower() or 'wrote' in line.lower() for line in output_lines)
                    no_results = any('no results' in line.lower() or 'warning: no results' in line.lower() for line in output_lines)
                    
                    if no_results:
                        self.logger.warning(f"⚠ {instrument} analysis completed but generated NO RESULTS")
                        self.logger.warning(f"  Check analyzer output above for details")
                        emit_event(self.run_id, "analyzer", "metric", f"{instrument} completed (no results)", {
                            "instrument": instrument,
                            "status": "success_no_results"
                        })
                    elif has_results:
                        self.logger.info(f"✓ {instrument} analysis completed with results")
                        emit_event(self.run_id, "analyzer", "metric", f"{instrument} completed", {
                            "instrument": instrument,
                            "status": "success"
                        })
                    else:
                        self.logger.info(f"✓ {instrument} analysis completed")
                        emit_event(self.run_id, "analyzer", "metric", f"{instrument} completed", {
                            "instrument": instrument,
                            "status": "success"
                        })
                    success_count += 1
                    self.logger.info(f"✓ {instrument} completed successfully ({success_count}/{len(instruments)} instruments done)")
                    emit_event(self.run_id, "analyzer", "log", f"{instrument} completed ({success_count}/{len(instruments)} instruments done)")
                else:
                    self.logger.error(f"✗ {instrument} analysis failed (code: {result.returncode})")
                    self.logger.error(f"  Command: {' '.join(analyzer_cmd)}")
                    if result.stdout:
                        self.logger.error(f"  Output (last 1000 chars): {result.stdout[-1000:]}")
                    if result.stderr:
                        self.logger.error(f"  Error output: {result.stderr[:500]}")
                    emit_event(self.run_id, "analyzer", "failure", f"{instrument} analysis failed (code: {result.returncode})", {
                        "instrument": instrument,
                        "return_code": result.returncode,
                        "error": result.stderr[:500] if result.stderr else None
                    })
                    
            except Exception as e:
                import traceback
                error_details = traceback.format_exc()
                self.logger.error(f"✗ {instrument} analyzer exception: {e}")
                self.logger.error(f"  Exception details:\n{error_details}")
                emit_event(self.run_id, "analyzer", "failure", f"{instrument} exception: {str(e)}", {
                    "instrument": instrument,
                    "error_details": str(e),
                    "traceback": error_details[-500:] if len(error_details) > 500 else error_details
                })
            
            # Log that we're moving to next instrument (or done)
            if i < len(instruments):
                next_instrument = instruments[i]
                self.logger.info(f"Moving to next instrument: {next_instrument}")
                emit_event(self.run_id, "analyzer", "log", f"Moving to next instrument: {next_instrument}")
        
        self.logger.info(f"=" * 60)
        self.logger.info(f"All instruments processed: {success_count}/{len(instruments)} successful")
        self.logger.info(f"=" * 60)
        success = success_count > 0
        self.stage_results["analyzer"] = success
        
        # Delete processed files after successful analysis
        if success:
            # Get list of processed files that match the analyzed instruments
            processed_files_to_delete = []
            if DATA_PROCESSED.exists():
                all_processed = list(DATA_PROCESSED.glob("*.parquet"))
                all_processed.extend(list(DATA_PROCESSED.glob("*.csv")))
                
                # Filter files that match analyzed instruments
                for proc_file in all_processed:
                    # Check if file name contains any of the analyzed instruments
                    file_name_upper = proc_file.name.upper()
                    for instrument in instruments:
                        if instrument.upper() in file_name_upper:
                            processed_files_to_delete.append(proc_file)
                            break
                
                # Delete the processed files
                if processed_files_to_delete:
                    deleted_count = self._delete_processed_files(processed_files_to_delete)
                    if deleted_count > 0:
                        self.logger.info(f"✓ Deleted {deleted_count} processed file(s)")
                        emit_event(self.run_id, "analyzer", "metric", "Processed files deleted", {
                            "deleted_file_count": deleted_count
                        })
        
        if success:
            emit_event(self.run_id, "analyzer", "success", f"Analyzer completed: {success_count}/{len(instruments)} instruments")
        else:
            emit_event(self.run_id, "analyzer", "failure", f"Analyzer failed: {success_count}/{len(instruments)} instruments")
        
        self.logger.info(f"Analyzer completed: {success_count}/{len(instruments)} instruments")
        return success
    
    def run_data_merger(self) -> bool:
        """Run data merger to consolidate daily files into monthly files."""
        self.logger.info("=" * 60)
        self.logger.info("DATA MERGER: Consolidating daily files into monthly files")
        self.logger.info("=" * 60)
        
        emit_event(self.run_id, "merger", "start", "Starting data merger")
        
        # Build command
        merger_cmd = [
            sys.executable,
            str(DATA_MERGER_SCRIPT)
        ]
        
        self.logger.info(f"Running data merger...")
        self.logger.info(f"  Command: {' '.join(merger_cmd)}")
        self.logger.info(f"  Working directory: {QTSW2_ROOT}")
        
        # Run data merger with timeout
        try:
            result = subprocess.run(
                merger_cmd,
                cwd=str(QTSW2_ROOT),
                capture_output=True,
                text=True,
                timeout=1800  # 30 minute timeout
            )
            
            # Log output
            if result.stdout:
                self.logger.info("Data merger output:")
                for line in result.stdout.split('\n'):
                    if line.strip():
                        self.logger.info(f"  {line}")
                        # Emit key milestones
                        if any(keyword in line.upper() for keyword in ["MERGED", "PROCESSED", "COMPLETE", "ERROR", "FAILED"]):
                            emit_event(self.run_id, "merger", "log", line.strip())
            
            if result.stderr:
                self.logger.warning("Data merger stderr:")
                for line in result.stderr.split('\n'):
                    if line.strip():
                        self.logger.warning(f"  {line}")
                        if "ERROR" in line.upper() or "FAILED" in line.upper():
                            emit_event(self.run_id, "merger", "log", f"ERROR: {line.strip()}")
            
            if result.returncode == 0:
                self.logger.info("✓ Data merger completed successfully")
                emit_event(self.run_id, "merger", "success", "Data merger completed successfully")
                self.stage_results["merger"] = True
                return True
            else:
                self.logger.error(f"✗ Data merger failed with return code {result.returncode}")
                emit_event(self.run_id, "merger", "failure", f"Data merger failed with code {result.returncode}")
                self.stage_results["merger"] = False
                return False
                
        except subprocess.TimeoutExpired:
            self.logger.error("✗ Data merger timed out after 30 minutes")
            emit_event(self.run_id, "merger", "failure", "Data merger timed out after 30 minutes")
            self.stage_results["merger"] = False
            return False
        except Exception as e:
            self.logger.error(f"✗ Failed to run data merger: {e}")
            emit_event(self.run_id, "merger", "failure", f"Data merger exception: {str(e)}")
            self.stage_results["merger"] = False
            return False
    
    def run_sequential_processor(self, instruments: List[str] = None) -> bool:
        """Stage 3: Run sequential processor on analyzer output files."""
        self.logger.info("=" * 60)
        self.logger.info("STAGE 3: Sequential Processor")
        self.logger.info("=" * 60)
        
        emit_event(self.run_id, "sequential", "start", "Starting sequential processor stage")
        
        # Find analyzer output files from analyzer_runs (merged monthly files)
        # Look for files in analyzer_runs/<instrument><session>/<year>/ folders
        analyzer_runs_dir = QTSW2_ROOT / "data" / "analyzer_runs"
        
        if not analyzer_runs_dir.exists():
            self.logger.warning(f"No analyzer_runs directory found: {analyzer_runs_dir}")
            self.logger.info("Sequential processor requires merged analyzer files")
            emit_event(self.run_id, "sequential", "log", f"No analyzer_runs directory found: {analyzer_runs_dir}")
            emit_event(self.run_id, "sequential", "success", "Sequential processor skipped (no analyzer_runs directory)")
            self.stage_results["sequential_processor"] = True  # Mark as skipped but not failed
            return True
        
        # Find analyzer files grouped by instrument-session (ES1, ES2, NQ1, NQ2, etc.)
        # Pattern: analyzer_runs/<instrument><session>/<year>/*.parquet
        # Example: analyzer_runs/ES1/2025/ES1_an_2025_11.parquet
        instrument_session_files = {}  # Key: "ES1", "ES2", etc. Value: list of files
        
        # Common instruments and sessions
        instruments_list = ["ES", "NQ", "YM", "CL", "NG", "GC"]
        sessions = ["1", "2"]  # S1 -> 1, S2 -> 2
        
        for instrument in instruments_list:
            for session in sessions:
                instrument_session = f"{instrument}{session}"
                instrument_dir = analyzer_runs_dir / instrument_session
                
                if instrument_dir.exists():
                    # Look for parquet files in year subdirectories - collect ALL monthly files
                    session_files = []
                    for year_dir in instrument_dir.iterdir():
                        if year_dir.is_dir() and year_dir.name.isdigit():
                            year_files = list(year_dir.glob("*.parquet"))
                            # Add ALL monthly files, not just the most recent
                            session_files.extend(year_files)
                    
                    if session_files:
                        # Sort files by path to ensure consistent ordering (by year/month)
                        session_files = sorted(session_files)
                        # Store ALL files for this instrument-session combination
                        instrument_session_files[instrument_session] = session_files
        
        if not instrument_session_files:
            self.logger.warning(f"No analyzer parquet files found in {analyzer_runs_dir}")
            emit_event(self.run_id, "sequential", "log", f"No analyzer parquet files found in {analyzer_runs_dir}")
            emit_event(self.run_id, "sequential", "success", "Sequential processor skipped (no analyzer files)")
            self.stage_results["sequential_processor"] = True
            return True
        
        # Get list of instrument-session combinations to process
        instrument_sessions = sorted(instrument_session_files.keys())
        total_count = len(instrument_sessions)
        
        self.logger.info(f"Found {total_count} instrument-session combination(s) to process: {', '.join(instrument_sessions)}")
        emit_event(self.run_id, "sequential", "log", f"Found {total_count} stream(s) to process: {', '.join(instrument_sessions)}")
        emit_event(self.run_id, "sequential", "metric", "Sequential processor started", {
            "stream_count": total_count,
            "streams": instrument_sessions
        })
        
        success_count = 0
        for i, instrument_session in enumerate(instrument_sessions, 1):
            # Extract instrument and session from key (e.g., "ES1" -> "ES", "1")
            instrument = instrument_session[:-1]  # Remove last character (session number)
            session_num = instrument_session[-1]  # Get session number
            
            # Get ALL files for this instrument-session (all monthly files across all years)
            session_files = instrument_session_files[instrument_session]
            
            self.logger.info(f"=" * 60)
            self.logger.info(f"Starting sequential processor for {instrument_session} ({i}/{total_count})")
            self.logger.info(f"  Processing {len(session_files)} monthly file(s) for {instrument_session}")
            emit_event(self.run_id, "sequential", "log", f"Starting {instrument_session} ({i}/{total_count})")
            emit_event(self.run_id, "sequential", "log", f"Processing {len(session_files)} monthly file(s)")
            
            # Log all files being processed
            for file_idx, data_file in enumerate(session_files, 1):
                self.logger.info(f"    [{file_idx}/{len(session_files)}] {data_file.name}")
            
            # Determine start time based on session: S1 uses 08:00, S2 uses 09:30
            start_time = "08:00" if session_num == "1" else "09:30"
            
            self.logger.info(f"  Stream: {instrument_session}, Session: S{session_num}, Start time: {start_time}")
            emit_event(self.run_id, "sequential", "log", f"Stream: {instrument_session}, Start time: {start_time}")
            
            # Build command - pass ALL files to process complete dataset
            # The sequential processor script needs to be updated to accept multiple files
            # For now, we'll create a temporary combined file or pass files as a comma-separated list
            # Check if sequential processor supports multiple files via CLI
            if len(session_files) == 1:
                # Single file - use existing --data-file argument
                sequential_cmd = [
                    sys.executable,
                    str(SEQUENTIAL_PROCESSOR_SCRIPT),
                    "--data-file", str(session_files[0]),
                    "--start-time", start_time,
                    "--max-days", "10000",
                    "--output-folder", "data/sequencer_runs"
                ]
            else:
                # Multiple files - need to combine them or pass as list
                # For now, combine all files into a temporary file
                import tempfile
                temp_combined_file = tempfile.NamedTemporaryFile(
                    mode='w', suffix='.parquet', delete=False,
                    dir=str(QTSW2_ROOT / "data" / "temp")
                )
                temp_combined_path = Path(temp_combined_file.name)
                temp_combined_file.close()
                
                # Ensure temp directory exists
                temp_combined_path.parent.mkdir(parents=True, exist_ok=True)
                
                # Load and combine all files
                self.logger.info(f"  Combining {len(session_files)} files into temporary file...")
                combined_dfs = []
                for data_file in session_files:
                    df = pd.read_parquet(data_file)
                    combined_dfs.append(df)
                
                combined_df = pd.concat(combined_dfs, ignore_index=True)
                combined_df = combined_df.sort_values('Date').reset_index(drop=True)
                combined_df.to_parquet(temp_combined_path, index=False, compression='snappy')
                
                self.logger.info(f"  Combined {len(combined_df):,} rows from {len(session_files)} file(s)")
                emit_event(self.run_id, "sequential", "log", f"Combined {len(combined_df):,} rows from {len(session_files)} file(s)")
                
                sequential_cmd = [
                    sys.executable,
                    str(SEQUENTIAL_PROCESSOR_SCRIPT),
                    "--data-file", str(temp_combined_path),
                    "--start-time", start_time,
                    "--max-days", "10000",
                    "--output-folder", "data/sequencer_runs"
                ]
            
            self.logger.info(f"Running sequential processor for {instrument_session}...")
            self.logger.info(f"  Command: {' '.join(sequential_cmd)}")
            self.logger.info(f"  Working directory: {QTSW2_ROOT}")
            
            # Track temporary file for cleanup
            temp_combined_path = None
            if len(session_files) > 1:
                # Find the temp_combined_path from the command
                temp_combined_path = Path(sequential_cmd[sequential_cmd.index("--data-file") + 1])
            
            # Run sequential processor with timeout
            try:
                # Set environment variables
                env = os.environ.copy()
                env["PIPELINE_RUN"] = "1"
                # Set UTF-8 encoding for Windows console to handle Unicode characters
                env["PYTHONIOENCODING"] = "utf-8"
                
                result = subprocess.run(
                    sequential_cmd,
                    cwd=str(QTSW2_ROOT),
                    capture_output=True,
                    text=True,
                    encoding='utf-8',  # Explicit UTF-8 encoding
                    errors='replace',  # Replace problematic characters instead of failing
                    timeout=3600,  # 1 hour timeout
                    env=env
                )
                
                # Log output for debugging
                if result.stdout:
                    self.logger.info(f"[{instrument_session}] Sequential processor output:")
                    for line in result.stdout.split('\n'):
                        if line.strip():
                            self.logger.info(f"  {line}")
                            # Emit key milestones
                            if any(keyword in line.upper() for keyword in ["SAVED", "RESULTS", "COMPLETE", "ERROR"]):
                                if line.strip():
                                    emit_event(self.run_id, "sequential", "log", f"[{instrument_session}] {line.strip()}")
                
                if result.stderr:
                    self.logger.warning(f"[{instrument_session}] Sequential processor stderr:")
                    for line in result.stderr.split('\n'):
                        if line.strip():
                            self.logger.warning(f"  {line}")
                            if "ERROR" in line.upper() or "FAILED" in line.upper():
                                emit_event(self.run_id, "sequential", "log", f"[{instrument_session}] ERROR: {line.strip()}")
                
                if result.returncode == 0:
                    self.logger.info(f"✓ [{instrument_session}] Sequential processor completed successfully")
                    emit_event(self.run_id, "sequential", "log", f"[{instrument_session}] Completed successfully")
                    success_count += 1
                else:
                    self.logger.error(f"✗ [{instrument_session}] Sequential processor failed with return code {result.returncode}")
                    emit_event(self.run_id, "sequential", "log", f"[{instrument_session}] Failed with return code {result.returncode}")
                    
            except subprocess.TimeoutExpired:
                self.logger.error(f"✗ [{instrument_session}] Sequential processor timed out after 1 hour")
                emit_event(self.run_id, "sequential", "failure", f"[{instrument_session}] Timed out after 1 hour")
            except Exception as e:
                self.logger.error(f"✗ [{instrument_session}] Failed to run sequential processor: {e}")
                emit_event(self.run_id, "sequential", "failure", f"[{instrument_session}] Failed: {str(e)}")
            finally:
                # Clean up temporary combined file if it was created
                if temp_combined_path and temp_combined_path.exists():
                    try:
                        temp_combined_path.unlink()
                        self.logger.info(f"  Cleaned up temporary combined file: {temp_combined_path.name}")
                    except Exception as e:
                        self.logger.warning(f"  Failed to clean up temporary file {temp_combined_path.name}: {e}")
        
        # Summary
        all_success = success_count == total_count
        if all_success:
            self.logger.info(f"✓ Sequential processor completed for all {success_count} stream(s)")
            emit_event(self.run_id, "sequential", "success", f"Completed for all {success_count} stream(s)")
        else:
            self.logger.warning(f"Sequential processor completed for {success_count}/{total_count} stream(s)")
            emit_event(self.run_id, "sequential", "log", f"Completed for {success_count}/{total_count} stream(s)")
        
        self.stage_results["sequential_processor"] = all_success
        return all_success
    
    def generate_audit_report(self) -> Dict:
        """Generate audit report of pipeline execution."""
        emit_event(self.run_id, "audit", "start", "Generating audit report")
        
        report = {
            "timestamp": datetime.now(CHICAGO_TZ).isoformat(),
            "stages": self.stage_results,
            "success": all(self.stage_results.values()),
            "log_file": str(LOG_FILE)
        }
        
        # Save report
        report_file = LOGS_DIR / f"pipeline_report_{datetime.now().strftime('%Y%m%d')}.json"
        with open(report_file, 'w') as f:
            json.dump(report, f, indent=2)
        
        emit_event(self.run_id, "audit", "metric", "Audit report generated", {
            "success": report["success"],
            "stages": self.stage_results
        })
        emit_event(self.run_id, "audit", "success", "Pipeline complete" if report["success"] else "Pipeline completed with failures")
        
        return report


# ============================================================
# Main Scheduler
# ============================================================

class DailyPipelineScheduler:
    """Main scheduler class for daily automated pipeline."""
    
    def __init__(self, schedule_time: str = "07:30", debug_window=None):
        """
        Initialize scheduler.
        
        Args:
            schedule_time: Time in HH:MM format (Chicago time, 24-hour)
            debug_window: Optional DebugLogWindow instance for GUI logging
        """
        self.debug_window = debug_window
        self.logger = setup_logging(LOG_FILE, debug_window=debug_window)
        self.schedule_time = schedule_time
        self.nt_controller = NinjaTraderController(self.logger)
        self.orchestrator: Optional[PipelineOrchestrator] = None
        
        # Update debug window periodically if it exists
        if self.debug_window:
            import threading
            def update_window():
                while self.debug_window and not self.debug_window.closed:
                    try:
                        self.debug_window.update()
                        time.sleep(0.1)
                    except:
                        break
            threading.Thread(target=update_window, daemon=True).start()
        
    def run_now(self, wait_for_export: bool = False, launch_ninjatrader: bool = False) -> bool:
        """
        Execute pipeline immediately (for testing or manual runs).
        
        Args:
            wait_for_export: If True, wait for new exports before processing
            launch_ninjatrader: If True, launch NinjaTrader before waiting for exports
        """
        run_id = str(uuid.uuid4())
        self.logger.info("=" * 60)
        self.logger.info("DAILY DATA PIPELINE - MANUAL RUN")
        self.logger.info(f"Run ID: {run_id}")
        self.logger.info("=" * 60)
        
        emit_event(run_id, "pipeline", "start", "Pipeline run started")
        
        # Create orchestrator with run_id
        self.orchestrator = PipelineOrchestrator(self.logger, run_id)
        
        # Optionally launch NinjaTrader and wait for exports
        if launch_ninjatrader:
            self.logger.info("Launching NinjaTrader...")
            if not self.nt_controller.launch():
                self.logger.error("Failed to launch NinjaTrader - aborting pipeline")
                emit_event(run_id, "pipeline", "failure", "Failed to launch NinjaTrader")
                return False
        
        if wait_for_export:
            self.logger.info("Waiting for exports to complete...")
            emit_event(run_id, "pipeline", "log", "Waiting for exports to complete")
            if not self.nt_controller.wait_for_export(timeout_minutes=60, run_id=run_id):
                self.logger.error("Export timeout or failure - aborting pipeline")
                emit_event(run_id, "pipeline", "failure", "Export timeout or failure")
                return False
            self.logger.info("Exports detected - proceeding to translator")
            emit_event(run_id, "pipeline", "log", "Exports detected - proceeding to translator")
        
        # Check for existing CSV files in data/raw
        emit_event(run_id, "pipeline", "log", "Checking for CSV files in data/raw")
        raw_files = list(DATA_RAW.glob("*.csv"))
        # Exclude files in subdirectories (like logs folder)
        raw_files = [f for f in raw_files if f.parent == DATA_RAW]
        
        # Check for processed files in data/processed
        processed_files = []
        if DATA_PROCESSED.exists():
            processed_files = list(DATA_PROCESSED.glob("*.parquet"))
            processed_files.extend(list(DATA_PROCESSED.glob("*.csv")))
        
        # Determine which stages to run
        run_translator_stage = len(raw_files) > 0
        run_analyzer_stage = len(processed_files) > 0
        
        if not run_translator_stage and not run_analyzer_stage:
            self.logger.warning("No CSV files found in data/raw/ and no processed files found - nothing to process")
            emit_event(run_id, "pipeline", "log", "No files found - skipping all stages")
            emit_event(run_id, "pipeline", "success", "Pipeline complete (no files to process)")
            return True
        
        # Log what we found
        if run_translator_stage:
            self.logger.info(f"Found {len(raw_files)} CSV file(s) to process:")
            for raw_file in raw_files:
                file_size_mb = raw_file.stat().st_size / (1024 * 1024)
                self.logger.info(f"  - {raw_file.name} ({file_size_mb:.2f} MB)")
            emit_event(run_id, "pipeline", "log", f"Found {len(raw_files)} CSV file(s), starting translator")
        
        if run_analyzer_stage:
            self.logger.info(f"Found {len(processed_files)} processed file(s) ready for analysis:")
            for proc_file in processed_files[:5]:  # Show first 5
                file_size_mb = proc_file.stat().st_size / (1024 * 1024)
                self.logger.info(f"  - {proc_file.name} ({file_size_mb:.2f} MB)")
            if len(processed_files) > 5:
                self.logger.info(f"  ... and {len(processed_files) - 5} more")
            emit_event(run_id, "pipeline", "log", f"Found {len(processed_files)} processed file(s), will run analyzer")
        
        # Run pipeline stages
        success = True
        
        # Stage 1: Translator (only if raw files exist)
        if run_translator_stage:
            if not self.orchestrator.run_translator():
                self.logger.error("Translator failed - aborting pipeline")
                success = False
                # Don't run analyzer if translator failed (unless we already have processed files)
                if not run_analyzer_stage:
                    return False
            else:
                # Translator succeeded, refresh processed files list
                if DATA_PROCESSED.exists():
                    processed_files = list(DATA_PROCESSED.glob("*.parquet"))
                    processed_files.extend(list(DATA_PROCESSED.glob("*.csv")))
                    run_analyzer_stage = len(processed_files) > 0
        
        # Stage 2: Analyzer (if processed files exist)
        if run_analyzer_stage and success:
            if not self.orchestrator.run_analyzer():
                self.logger.error("Analyzer failed - continuing anyway")
                success = False  # Non-fatal
        elif not run_analyzer_stage and run_translator_stage:
            # Translator ran but no processed files were created
            self.logger.warning("Translator completed but no processed files found - skipping analyzer")
            emit_event(run_id, "analyzer", "log", "Skipped: No processed files available")
        
        # Stage 2.5: Data Merger (merge analyzer files into monthly files)
        if success and run_analyzer_stage:
            self.orchestrator.run_data_merger()
        
        # Stage 3: Sequential Processor (runs on merged analyzer files)
        if success:
            self.orchestrator.run_sequential_processor()
        
        # Stage 3.5: Data Merger (merge sequencer files into monthly files)
        if success:
            self.orchestrator.run_data_merger()
        
        # Generate audit report
        report = self.orchestrator.generate_audit_report()
        self.logger.info("=" * 60)
        self.logger.info("PIPELINE COMPLETE")
        self.logger.info(f"Success: {report['success']}")
        self.logger.info(f"Report saved: {report['log_file']}")
        self.logger.info("=" * 60)
        
        return success
    
    def wait_for_schedule(self):
        """Wait until scheduled time, then execute."""
        while True:
            now_chicago = datetime.now(CHICAGO_TZ)
            target_time = datetime.strptime(self.schedule_time, "%H:%M").time()
            target_datetime = CHICAGO_TZ.localize(
                datetime.combine(now_chicago.date(), target_time)
            )
            
            # If target time already passed today, schedule for tomorrow
            if target_datetime <= now_chicago:
                target_datetime += timedelta(days=1)
            
            wait_seconds = (target_datetime - now_chicago).total_seconds()
            wait_hours = wait_seconds / 3600
            
            self.logger.info(f"Scheduled for {target_datetime.strftime('%Y-%m-%d %H:%M:%S %Z')}")
            self.logger.info(f"Waiting {wait_hours:.1f} hours...")
            
            time.sleep(wait_seconds)
            
            # Execute pipeline with NinjaTrader launch and export waiting
            self.run_now(wait_for_export=True, launch_ninjatrader=True)
            
            # Wait a bit before scheduling next run (avoid tight loops)
            time.sleep(60)


# ============================================================
# Entry Point
# ============================================================

def main():
    """Main entry point for scheduler."""
    parser = argparse.ArgumentParser(
        description="Daily Data Pipeline Scheduler for Quantitative Trading System"
    )
    parser.add_argument(
        "--schedule",
        type=str,
        default="07:30",
        help="Scheduled time (HH:MM, Chicago time, 24-hour format). Default: 07:30"
    )
    parser.add_argument(
        "--now",
        action="store_true",
        help="Execute pipeline immediately instead of waiting for schedule"
    )
    parser.add_argument(
        "--wait-for-export",
        action="store_true",
        help="Wait for new exports to complete before processing (use with --now)"
    )
    parser.add_argument(
        "--launch-ninjatrader",
        action="store_true",
        help="Launch NinjaTrader before waiting for exports (use with --now and --wait-for-export)"
    )
    parser.add_argument(
        "--test",
        action="store_true",
        help="Test mode: check configuration without executing"
    )
    parser.add_argument(
        "--no-debug-window",
        action="store_true",
        help="Disable debug log window (run headless)"
    )
    parser.add_argument(
        "--stage",
        type=str,
        choices=["translator", "analyzer", "sequential"],
        help="Run only a specific pipeline stage (translator, analyzer, or sequential)"
    )
    
    args = parser.parse_args()
    
    # Create debug window (unless disabled)
    debug_window = None
    if not args.no_debug_window:
        try:
            import sys
            from pathlib import Path
            # Add automation directory to path for import
            automation_dir = Path(__file__).parent
            if str(automation_dir) not in sys.path:
                sys.path.insert(0, str(automation_dir))
            from debug_log_window import create_debug_window
            debug_window = create_debug_window(enabled=True)
            if debug_window:
                print("Debug log window opened")
        except Exception as e:
            print(f"Warning: Could not create debug window: {e}")
    
    scheduler = DailyPipelineScheduler(schedule_time=args.schedule, debug_window=debug_window)
    
    if args.test:
        scheduler.logger.info("TEST MODE: Configuration check")
        scheduler.logger.info(f"NinjaTrader EXE exists: {NINJATRADER_EXE.exists()}")
        scheduler.logger.info(f"Workspace exists: {NINJATRADER_WORKSPACE.exists()}")
        scheduler.logger.info(f"Data raw dir exists: {DATA_RAW.exists()}")
        scheduler.logger.info(f"Data processed dir exists: {DATA_PROCESSED.exists()}")
        return
    
    if args.stage:
        # Run only a specific stage
        run_id = str(uuid.uuid4())
        scheduler.logger.info("=" * 60)
        scheduler.logger.info(f"RUNNING SINGLE STAGE: {args.stage.upper()}")
        scheduler.logger.info(f"Run ID: {run_id}")
        scheduler.logger.info("=" * 60)
        
        emit_event(run_id, "pipeline", "start", f"Running {args.stage} stage only")
        
        orchestrator = PipelineOrchestrator(scheduler.logger, run_id)
        success = False
        
        if args.stage == "translator":
            success = orchestrator.run_translator()
        elif args.stage == "analyzer":
            success = orchestrator.run_analyzer()
        elif args.stage == "sequential":
            success = orchestrator.run_sequential_processor()
        
        if success:
            emit_event(run_id, args.stage, "success", f"{args.stage} stage completed successfully")
            scheduler.logger.info(f"✓ {args.stage.upper()} stage completed successfully")
        else:
            emit_event(run_id, args.stage, "failure", f"{args.stage} stage failed")
            scheduler.logger.error(f"✗ {args.stage.upper()} stage failed")
        
        sys.exit(0 if success else 1)
    elif args.now:
        success = scheduler.run_now(
            wait_for_export=args.wait_for_export,
            launch_ninjatrader=args.launch_ninjatrader
        )
        sys.exit(0 if success else 1)
    else:
        scheduler.wait_for_schedule()


if __name__ == "__main__":
    main()

