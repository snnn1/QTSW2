"""
Quantitative Trading System - Daily Data Pipeline Scheduler

Automated scheduler that orchestrates the complete data pipeline:
1. Runs every 15 minutes at :00, :15, :30, :45
2. Checks for CSV files in data/raw/ → Runs Translator
3. Checks for processed files in data/processed/ → Runs Analyzer
4. Runs Data Merger after analyzer completes (pipeline ends here)

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
import threading
import queue
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

# Data directories
# Note: DATA_RAW matches DataExporter output path (QTSW2/data/raw)
DATA_RAW = QTSW2_ROOT / "data" / "raw"
DATA_RAW_LOGS = DATA_RAW / "logs"  # Signal files are stored in logs subfolder
DATA_PROCESSED = QTSW2_ROOT / "data" / "processed"  # QTSW2/data/processed (where files should go)
ANALYZER_RUNS = QTSW2_ROOT / "data" / "analyzer_runs"  # QTSW2/data/analyzer_runs
SEQUENCER_RUNS = QTSW2_ROOT / "data" / "sequencer_runs"  # QTSW2/data/sequencer_runs
LOGS_DIR = QTSW2_ROOT / "automation" / "logs"
EVENT_LOGS_DIR = LOGS_DIR / "events"
EVENT_LOGS_DIR.mkdir(parents=True, exist_ok=True)

# Pipeline scripts (adjust paths as needed)
TRANSLATOR_SCRIPT = QTSW2_ROOT / "scripts" / "translate_raw_app.py"
ANALYZER_SCRIPT = QTSW2_ROOT / "scripts" / "breakout_analyzer" / "scripts" / "run_data_processed.py"
PARALLEL_ANALYZER_SCRIPT = QTSW2_ROOT / "tools" / "run_analyzer_parallel.py"
DATA_MERGER_SCRIPT = QTSW2_ROOT / "tools" / "data_merger.py"

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
        stage: Current pipeline stage (translator, analyzer, merger, audit, etc.)
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
            # Use Popen instead of run to allow progress monitoring
            try:
                process = subprocess.Popen(
                    translator_cmd,
                    cwd=str(QTSW2_ROOT),
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    text=True,
                    bufsize=1  # Line buffered
                )
                
                # Monitor progress and emit periodic updates
                import threading
                import queue
                
                stdout_queue = queue.Queue()
                stderr_queue = queue.Queue()
                
                def read_stdout():
                    for line in iter(process.stdout.readline, ''):
                        stdout_queue.put(line)
                    process.stdout.close()
                
                def read_stderr():
                    for line in iter(process.stderr.readline, ''):
                        stderr_queue.put(line)
                    process.stderr.close()
                
                stdout_thread = threading.Thread(target=read_stdout, daemon=True)
                stderr_thread = threading.Thread(target=read_stderr, daemon=True)
                stdout_thread.start()
                stderr_thread.start()
                
                # Monitor process with progress updates
                files_written = []
                # Track which input files have been processed by instrument name
                processed_instruments = set()
                # Extract instrument names from raw file names (e.g., "CL.csv" -> "CL")
                expected_instruments = {f.stem.upper() for f in raw_files}
                last_progress_time = time.time()
                last_output_time = time.time()  # Track when we last saw output
                progress_interval = 60  # Emit progress every 60 seconds
                no_output_warning_interval = 300  # Warn if no output for 5 minutes
                start_time = time.time()
                timeout_seconds = 3600  # 1 hour timeout
                no_output_timeout = 30  # If no output for 30 seconds after all files written, treat as complete
                last_file_written_time = None
                
                self.logger.info(f"Expected instruments to process: {sorted(expected_instruments)}")
                
                while process.poll() is None:
                    elapsed = time.time() - start_time
                    if elapsed > timeout_seconds:
                        self.logger.error("✗ Translator timed out after 1 hour")
                        process.kill()
                        emit_event(self.run_id, "translator", "failure", "Translator timed out after 1 hour")
                        self.stage_results["translator"] = False
                        return False
                    
                    # Check for new output
                    output_received = False
                    translator_completed = False
                    try:
                        while True:
                            line = stdout_queue.get_nowait()
                            if line.strip():
                                output_received = True
                                last_output_time = time.time()
                                self.logger.info(f"  {line.strip()}")
                                
                                # Check for completion message
                                if "[SUCCESS]" in line and "completed successfully" in line:
                                    translator_completed = True
                                    emit_event(self.run_id, "translator", "log", line.strip())
                                
                                # Emit event when a file is successfully written
                                if "Saved:" in line or "Saved PARQUET:" in line or "Saved CSV:" in line:
                                    emit_event(self.run_id, "translator", "log", f"File written: {line.strip()}")
                                    files_written.append(line.strip())
                                    last_file_written_time = time.time()  # Track when last file was written
                                    
                                    # Extract instrument from saved file name (e.g., "CL_2025_CL.parquet" -> "CL")
                                    if ".parquet" in line:
                                        match = re.search(r'([A-Z]+)_(\d{4})', line)
                                        if match:
                                            instrument = match.group(1)
                                            year = match.group(2)
                                            processed_instruments.add(instrument)
                                            emit_event(self.run_id, "translator", "metric", f"Wrote {instrument} {year} file", {
                                                "instrument": instrument,
                                                "year": year,
                                                "file_type": "parquet"
                                            })
                                            self.logger.info(f"  ✓ File written: {instrument} ({len(processed_instruments)}/{len(expected_instruments)} instruments complete)")
                                    else:
                                        self.logger.info(f"  ✓ File written: {len(files_written)} files total")
                                # Also emit any "Processing:" messages to show activity
                                elif "Processing:" in line:
                                    emit_event(self.run_id, "translator", "log", line.strip())
                                # Emit other important messages
                                elif "[INFO]" in line or "[ERROR]" in line:
                                    emit_event(self.run_id, "translator", "log", line.strip())
                    except queue.Empty:
                        pass
                    
                    # Check for stderr output too
                    try:
                        while True:
                            line = stderr_queue.get_nowait()
                            if line.strip():
                                output_received = True
                                last_output_time = time.time()
                                self.logger.warning(f"  [stderr] {line.strip()}")
                                # Emit warnings/errors as events
                                if "error" in line.lower() or "exception" in line.lower() or "traceback" in line.lower():
                                    emit_event(self.run_id, "translator", "log", f"Warning: {line.strip()}")
                    except queue.Empty:
                        pass
                    
                    # Warn if no output for extended period (might be stuck on large file)
                    time_since_output = time.time() - last_output_time
                    if time_since_output >= no_output_warning_interval:
                        elapsed_minutes = int(elapsed / 60)
                        no_output_minutes = int(time_since_output / 60)
                        self.logger.warning(f"Translator has produced no output for {no_output_minutes} minutes (elapsed: {elapsed_minutes} min)")
                        emit_event(self.run_id, "translator", "metric", f"Translation in progress - no output for {no_output_minutes} min (may be processing large file)", {
                            "elapsed_minutes": elapsed_minutes,
                            "no_output_minutes": no_output_minutes,
                            "files_written": len(files_written)
                        })
                        last_output_time = time.time()  # Reset to avoid spam
                    
                    # Emit periodic progress updates (even if no new output)
                    if time.time() - last_progress_time >= progress_interval:
                        elapsed_minutes = int(elapsed / 60)
                        emit_event(self.run_id, "translator", "metric", f"Translation in progress ({elapsed_minutes} min elapsed, {len(files_written)} files written)", {
                            "elapsed_minutes": elapsed_minutes,
                            "files_written": len(files_written)
                        })
                        last_progress_time = time.time()
                    
                    # If translator printed success message, wait a bit for process to exit naturally
                    # If it doesn't exit within 10 seconds, we'll treat it as complete anyway
                    if translator_completed:
                        wait_start = time.time()
                        while process.poll() is None and (time.time() - wait_start) < 10:
                            time.sleep(0.5)
                        # If still running after 10 seconds, it might be stuck - break out and treat as complete
                        if process.poll() is None:
                            self.logger.warning("Translator printed success but process hasn't exited after 10s - treating as complete")
                            process.terminate()  # Try to terminate gracefully
                            time.sleep(2)
                            if process.poll() is None:
                                process.kill()  # Force kill if still running
                            # Set returncode to 0 since we saw success message
                            process.returncode = 0
                            break  # Exit monitoring loop
                    
                    # If all expected instruments have been processed and no output for 30 seconds, treat as complete
                    # This handles cases where translator hangs after writing files
                    # Check by instrument count (more reliable than file count since one input can produce multiple outputs)
                    all_instruments_processed = len(processed_instruments) >= len(expected_instruments)
                    
                    if all_instruments_processed and last_file_written_time is not None:
                        time_since_last_file = time.time() - last_file_written_time
                        if time_since_last_file >= no_output_timeout:
                            missing = expected_instruments - processed_instruments
                            self.logger.info(f"All {len(expected_instruments)} instruments processed: {sorted(processed_instruments)}")
                            if missing:
                                self.logger.warning(f"Missing instruments: {sorted(missing)}")
                            self.logger.info(f"No output for {no_output_timeout}s - treating translator as complete")
                            self.logger.warning("Translator process may be hanging - terminating and treating as success")
                            emit_event(self.run_id, "translator", "log", f"All {len(expected_instruments)} instruments processed - terminating hanging process")
                            process.terminate()  # Try to terminate gracefully
                            time.sleep(2)
                            if process.poll() is None:
                                process.kill()  # Force kill if still running
                            # Set returncode to 0 since files were written
                            process.returncode = 0
                            break  # Exit monitoring loop
                    elif all_instruments_processed:
                        # All instruments processed but last_file_written_time not set - check if we should wait
                        if last_file_written_time is None:
                            # Set it now if we just detected all instruments are processed
                            last_file_written_time = time.time()
                            self.logger.info(f"All {len(expected_instruments)} instruments detected as processed: {sorted(processed_instruments)} - monitoring for completion")
                    
                    time.sleep(0.5)  # Small delay to avoid busy waiting
                
                # Process finished, get remaining output
                result_stdout = []
                result_stderr = []
                try:
                    while True:
                        result_stdout.append(stdout_queue.get_nowait())
                except queue.Empty:
                    pass
                try:
                    while True:
                        result_stderr.append(stderr_queue.get_nowait())
                except queue.Empty:
                    pass
                
                stdout_thread.join(timeout=5)
                stderr_thread.join(timeout=5)
                
                result = type('obj', (object,), {
                    'returncode': process.returncode,
                    'stdout': ''.join(result_stdout),
                    'stderr': ''.join(result_stderr)
                })()
                
                # Log remaining output (files_written already populated from monitoring loop)
                if result.stdout:
                    remaining_lines = result.stdout.split('\n')
                    for line in remaining_lines:
                        if line.strip() and line.strip() not in [f.strip() for f in files_written]:
                            self.logger.info(f"  {line.strip()}")
                            if "Saved:" in line or "Saved PARQUET:" in line or "Saved CSV:" in line:
                                emit_event(self.run_id, "translator", "log", f"File written: {line.strip()}")
                                files_written.append(line.strip())
                                if ".parquet" in line:
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
                            
            except Exception as e:
                # Handle any errors in the monitoring loop
                if hasattr(e, 'timeout') or 'timeout' in str(e).lower():
                    self.logger.error("✗ Translator timed out after 1 hour")
                    emit_event(self.run_id, "translator", "failure", "Translator timed out after 1 hour")
                else:
                    self.logger.error(f"✗ Translator exception: {e}")
                    emit_event(self.run_id, "translator", "failure", f"Translator exception: {str(e)}")
                self.stage_results["translator"] = False
                return False
            
            # Check if translator completed (either by return code, success message, or files written)
            translator_success_in_output = "[SUCCESS]" in result.stdout and "completed successfully" in result.stdout
            translator_success_in_stderr = "[SUCCESS]" in result.stderr and "completed successfully" in result.stderr
            
            # Check if files were actually written (more reliable than return code)
            processed_files_after = []
            if DATA_PROCESSED.exists():
                processed_files_after = list(DATA_PROCESSED.glob("*.parquet"))
                processed_files_after.extend(DATA_PROCESSED.glob("*.csv"))
            
            # Success if: return code 0, success message in output, OR files were written
            translator_succeeded = (
                result.returncode == 0 or 
                translator_success_in_output or 
                translator_success_in_stderr or
                len(processed_files_after) > 0 or
                len(files_written) > 0
            )
            
            # Debug logging
            self.logger.info(f"Translator completion check:")
            self.logger.info(f"  Return code: {result.returncode}")
            self.logger.info(f"  Success in stdout: {translator_success_in_output}")
            self.logger.info(f"  Success in stderr: {translator_success_in_stderr}")
            self.logger.info(f"  Processed files found: {len(processed_files_after)}")
            self.logger.info(f"  Files written during monitoring: {len(files_written)}")
            self.logger.info(f"  Translator succeeded: {translator_succeeded}")
            
            if translator_succeeded:
                self.logger.info("=" * 60)
                self.logger.info("✓ Translator completed successfully")
                self.logger.info(f"  Files written: {len(files_written)}")
                self.logger.info(f"  Processed files found: {len(processed_files_after)}")
                self.logger.info(f"  Return code: {result.returncode}")
                self.logger.info("=" * 60)
                
                if result.stdout:
                    self.logger.info(f"Last 500 chars of stdout: {result.stdout[-500:]}")
                
                # Count processed files
                emit_event(self.run_id, "translator", "metric", "Translation complete", {
                    "processed_file_count": len(processed_files_after),
                    "files_written_count": len(files_written),
                    "return_code": result.returncode
                })
                
                # NOTE: Raw files are NOT deleted (as per requirements)
                # They remain in data/raw/ for backup/archival purposes
                self.logger.info(f"✓ Translator completed - {len(raw_files)} raw file(s) preserved in data/raw/")
                
                emit_event(self.run_id, "translator", "success", "Translator completed successfully")
                self.logger.info("✓ Translator success event emitted")
                
                self.stage_results["translator"] = True
                return True
            else:
                self.logger.error(f"✗ Translator failed (code: {result.returncode})")
                self.logger.error(f"Files written during monitoring: {len(files_written)}")
                self.logger.error(f"Processed files found: {len(processed_files_after)}")
                if result.stderr:
                    self.logger.error(f"Error output: {result.stderr[-500:]}")
                emit_event(self.run_id, "translator", "failure", f"Translator failed with code {result.returncode}")
                self.stage_results["translator"] = False
                return False
                
        except Exception as e:
            # This catches any other exceptions not caught above
            self.logger.error(f"✗ Translator exception: {e}")
            emit_event(self.run_id, "translator", "failure", f"Translator exception: {str(e)}")
            self.stage_results["translator"] = False
            return False
    
    # NOTE: _delete_raw_files() method removed - raw files are never deleted (as per requirements)
    
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
        
        # Use parallel analyzer runner for faster processing
        self.logger.info(f"Using parallel analyzer runner for {len(instruments)} instrument(s): {', '.join(instruments)}")
        emit_event(self.run_id, "analyzer", "log", f"Starting parallel analyzer for {len(instruments)} instrument(s): {', '.join(instruments)}")
        
        # Build command for parallel analyzer runner
        parallel_cmd = [
            sys.executable,
            str(PARALLEL_ANALYZER_SCRIPT),
            "--instruments"
        ] + instruments + [
            "--folder", str(DATA_PROCESSED),
            "--run-id", self.run_id
        ]
        
        self.logger.info(f"Running parallel analyzer...")
        self.logger.info(f"  Command: {' '.join(parallel_cmd)}")
        emit_event(self.run_id, "analyzer", "log", "Starting parallel analyzer processes")
        
        try:
            # Set environment variable for event logging
            env = os.environ.copy()
            env["PIPELINE_RUN"] = "1"
            env["PIPELINE_EVENT_LOG"] = str(EVENT_LOGS_DIR / f"pipeline_{self.run_id}.jsonl")
            env["PIPELINE_RUN_ID"] = self.run_id
            
            process = subprocess.Popen(
                parallel_cmd,
                cwd=str(QTSW2_ROOT),
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                bufsize=1,
                env=env
            )
            
            # Stream output
            stdout_lines = []
            stderr_lines = []
            max_lines = 100
            
            def read_stream(stream, lines_list):
                try:
                    for line in iter(stream.readline, ''):
                        if line:
                            lines_list.append(line.rstrip())
                            if len(lines_list) > max_lines:
                                lines_list.pop(0)
                            
                            # Log important messages
                            line_upper = line.upper()
                            line_stripped = line.rstrip()
                            
                            if any(keyword in line_upper for keyword in ['ERROR', 'WARNING', 'COMPLETED', 'FAILED', 'SUCCESS']):
                                self.logger.info(f"[Parallel Analyzer] {line_stripped}")
                                emit_event(self.run_id, "analyzer", "log", line_stripped)
                except Exception as e:
                    self.logger.error(f"Error reading stream: {e}")
            
            import threading
            stdout_thread = threading.Thread(target=read_stream, args=(process.stdout, stdout_lines), daemon=True)
            stderr_thread = threading.Thread(target=read_stream, args=(process.stderr, stderr_lines), daemon=True)
            stdout_thread.start()
            stderr_thread.start()
            
            # Wait for completion
            import time
            start_time = time.time()
            timeout_seconds = 21600  # 6 hour timeout
            
            while process.poll() is None:
                elapsed = time.time() - start_time
                if elapsed > timeout_seconds:
                    self.logger.error("Parallel analyzer timed out after 6 hours")
                    emit_event(self.run_id, "analyzer", "failure", "Parallel analyzer timed out after 6 hours")
                    process.kill()
                    process.wait()
                    return False
                time.sleep(1)
            
            returncode = process.returncode
            elapsed_minutes = int((time.time() - start_time) / 60)
            
            # Wait for threads
            stdout_thread.join(timeout=5)
            stderr_thread.join(timeout=5)
            
            if returncode == 0:
                self.logger.info(f"✓ Parallel analyzer completed successfully in {elapsed_minutes} minutes")
                emit_event(self.run_id, "analyzer", "success", f"Parallel analyzer completed in {elapsed_minutes} minutes")
                success = True
            else:
                error_msg = '\n'.join(stderr_lines[-20:]) if stderr_lines else '\n'.join(stdout_lines[-20:])
                self.logger.error(f"✗ Parallel analyzer failed (code: {returncode})")
                self.logger.error(f"Error output: {error_msg}")
                emit_event(self.run_id, "analyzer", "failure", f"Parallel analyzer failed: {error_msg}")
                success = False
            
            # Set stage result
            self.stage_results["analyzer"] = success
            
            # Delete processed files after successful analysis (same as old code)
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
                
                if processed_files_to_delete:
                    deleted_count = 0
                    for proc_file in processed_files_to_delete:
                        try:
                            proc_file.unlink()
                            deleted_count += 1
                        except Exception as e:
                            self.logger.warning(f"Failed to delete {proc_file.name}: {e}")
                    
                    if deleted_count > 0:
                        self.logger.info(f"Deleted {deleted_count} processed file(s) after successful analysis")
                        emit_event(self.run_id, "analyzer", "log", f"Deleted {deleted_count} processed file(s)")
            
            return success
                
        except Exception as e:
            self.logger.error(f"Failed to run parallel analyzer: {e}")
            emit_event(self.run_id, "analyzer", "failure", f"Failed to start parallel analyzer: {str(e)}")
            self.stage_results["analyzer"] = False
            return False
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
                                        # Filter out verbose analyzer completion messages
                                        # Skip "Completed date" messages - too verbose, one per date
                                        if "Completed date" in line_stripped and "processed slots" in line_stripped:
                                            # Don't emit these - they're too verbose
                                            pass
                                        else:
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
        
    def run_now(self) -> bool:
        """
        Execute pipeline immediately (for testing or manual runs).
        """
        run_id = str(uuid.uuid4())
        
        # Create event log file for this run (if not already set)
        global EVENT_LOG_PATH
        if EVENT_LOG_PATH is None:
            event_log_file = EVENT_LOGS_DIR / f"pipeline_{run_id}.jsonl"
            event_log_file.touch()
            EVENT_LOG_PATH = str(event_log_file)
            self.logger.info(f"Created event log: {EVENT_LOG_PATH}")
        
        self.logger.info("=" * 60)
        self.logger.info("DAILY DATA PIPELINE - RUN")
        self.logger.info(f"Run ID: {run_id}")
        self.logger.info("=" * 60)
        
        emit_event(run_id, "pipeline", "start", "Pipeline run started")
        
        # Create orchestrator with run_id
        self.orchestrator = PipelineOrchestrator(self.logger, run_id)
        
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
        
        # Store initial processed_files count for logging
        initial_processed_count = len(processed_files)
        
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
                # Wait a moment for files to be written to disk
                time.sleep(2)
                
                if DATA_PROCESSED.exists():
                    # Check for parquet and csv files (recursive to catch any subdirectories)
                    processed_files = list(DATA_PROCESSED.rglob("*.parquet"))
                    processed_files.extend(list(DATA_PROCESSED.rglob("*.csv")))
                    # Filter out files in subdirectories if they exist (keep only root level)
                    processed_files = [f for f in processed_files if f.parent == DATA_PROCESSED]
                    
                    self.logger.info(f"After translator: Found {len(processed_files)} processed file(s)")
                    if processed_files:
                        for proc_file in processed_files[:5]:
                            self.logger.info(f"  - {proc_file.name}")
                        if len(processed_files) > 5:
                            self.logger.info(f"  ... and {len(processed_files) - 5} more")
                    
                    run_analyzer_stage = len(processed_files) > 0
                    
                    if not run_analyzer_stage:
                        self.logger.warning("Translator completed but no processed files detected in data/processed/")
                        emit_event(run_id, "pipeline", "log", "Translator completed but no processed files found - analyzer will be skipped")
                else:
                    self.logger.warning(f"Data processed directory does not exist: {DATA_PROCESSED}")
                    run_analyzer_stage = False
        
        # Stage 2: Analyzer (if processed files exist)
        if run_analyzer_stage and success:
            self.logger.info("=" * 60)
            self.logger.info("Proceeding to analyzer stage")
            self.logger.info(f"Processed files available: {len(processed_files)}")
            if not self.orchestrator.run_analyzer():
                self.logger.error("Analyzer failed - continuing anyway")
                success = False  # Non-fatal
        elif not run_analyzer_stage:
            # No processed files available
            if run_translator_stage:
                # Translator ran but no processed files were created
                self.logger.warning("Translator completed but no processed files found - skipping analyzer")
                self.logger.warning(f"Checked directory: {DATA_PROCESSED}")
                self.logger.warning(f"Directory exists: {DATA_PROCESSED.exists()}")
                emit_event(run_id, "analyzer", "log", "Skipped: No processed files available")
            else:
                # No translator ran, and no processed files exist
                self.logger.warning(f"No processed files found (initial count: {initial_processed_count}) - skipping analyzer")
                emit_event(run_id, "analyzer", "log", "Skipped: No processed files available")
        
        # Stage 2.5: Data Merger (merge analyzer files into monthly files)
        # Pipeline ends here after merger completes
        if success and run_analyzer_stage:
            success = success and self.orchestrator.run_data_merger()
        
        # Pipeline ends after data merger - sequential processor removed
        
        # Generate audit report
        report = self.orchestrator.generate_audit_report()
        self.logger.info("=" * 60)
        self.logger.info("PIPELINE COMPLETE")
        self.logger.info(f"Success: {report['success']}")
        self.logger.info(f"Report saved: {report['log_file']}")
        self.logger.info("=" * 60)
        
        return success
    
    def wait_for_schedule(self):
        """Wait until scheduled time, then execute. Runs every 15 minutes at :00, :15, :30, :45."""
        global EVENT_LOG_PATH
        
        self.logger.info("=" * 60)
        self.logger.info("SCHEDULER STARTED - Running every 15 minutes")
        self.logger.info("=" * 60)
        
        # Emit a scheduler start event (create a temporary event log for this)
        scheduler_start_id = str(uuid.uuid4())
        if EVENT_LOG_PATH is None:
            event_log_file = EVENT_LOGS_DIR / f"scheduler_{scheduler_start_id}.jsonl"
            event_log_file.touch()
            EVENT_LOG_PATH = str(event_log_file)
            emit_event(scheduler_start_id, "scheduler", "start", "Scheduler started - will run every 15 minutes")
            EVENT_LOG_PATH = None  # Reset so each pipeline run gets its own log
        
        while True:
            try:
                now_chicago = datetime.now(CHICAGO_TZ)
                
                # Calculate next 15-minute interval (:00, :15, :30, :45)
                current_minute = now_chicago.minute
                current_second = now_chicago.second
                current_microsecond = now_chicago.microsecond
                
                # Calculate minutes until next 15-minute mark
                minutes_until_next = 15 - (current_minute % 15)
                
                # If we're exactly at a 15-minute mark, wait 15 minutes
                if current_minute % 15 == 0 and current_second == 0 and current_microsecond == 0:
                    minutes_until_next = 15
                
                # Create target datetime at next 15-minute mark
                target_datetime = now_chicago.replace(second=0, microsecond=0)
                target_datetime += timedelta(minutes=minutes_until_next)
                
                wait_seconds = (target_datetime - now_chicago).total_seconds()
                wait_minutes = wait_seconds / 60
                
                self.logger.info(f"Next run scheduled for: {target_datetime.strftime('%Y-%m-%d %H:%M:%S %Z')}")
                self.logger.info(f"Waiting {wait_minutes:.1f} minutes ({wait_seconds:.0f} seconds)...")
                
                # Sleep until next 15-minute mark
                time.sleep(wait_seconds)
                
                # Execute pipeline (process existing files)
                self.logger.info("=" * 60)
                self.logger.info(f"SCHEDULED RUN - {datetime.now(CHICAGO_TZ).strftime('%Y-%m-%d %H:%M:%S %Z')}")
                self.logger.info("=" * 60)
                
                # Reset EVENT_LOG_PATH for each scheduled run so a new log file is created
                EVENT_LOG_PATH = None
                
                self.run_now()
                
                # Small delay before calculating next run
                time.sleep(5)
                
            except Exception as e:
                self.logger.error(f"Scheduler error: {e}", exc_info=True)
                # Wait 1 minute before retrying
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
        choices=["translator", "analyzer"],
        help="Run only a specific pipeline stage (translator or analyzer)"
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
        
        if success:
            emit_event(run_id, args.stage, "success", f"{args.stage} stage completed successfully")
            scheduler.logger.info(f"✓ {args.stage.upper()} stage completed successfully")
        else:
            emit_event(run_id, args.stage, "failure", f"{args.stage} stage failed")
            scheduler.logger.error(f"✗ {args.stage.upper()} stage failed")
        
        sys.exit(0 if success else 1)
    elif args.now:
        success = scheduler.run_now()
        sys.exit(0 if success else 1)
    else:
        scheduler.wait_for_schedule()


if __name__ == "__main__":
    main()

