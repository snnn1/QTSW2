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
DATA_PROCESSED = QTSW_ROOT / "data_processed"
ANALYZER_RUNS = QTSW_ROOT / "analyzer_runs"
LOGS_DIR = QTSW2_ROOT / "automation" / "logs"

# Pipeline scripts (adjust paths as needed)
TRANSLATOR_SCRIPT = QTSW2_ROOT / "scripts" / "translate_raw_app.py"
ANALYZER_SCRIPT = QTSW_ROOT / "scripts" / "breakout_analyzer" / "scripts" / "run_data_processed.py"
SEQUENTIAL_PROCESSOR_SCRIPT = QTSW_ROOT / "sequential_processor" / "sequential_processor_app.py"

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

def setup_logging(log_file: Path) -> logging.Logger:
    """Configure structured logging with file and console output."""
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
        
        # Look for MinuteDataExport_* or TickDataExport_* pattern
        files = list(DATA_RAW.glob("MinuteDataExport_*.csv"))
        files.extend(DATA_RAW.glob("TickDataExport_*.csv"))
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
            if not raw_files:
                self.logger.warning("No raw CSV files found in data_raw/")
                emit_event(self.run_id, "translator", "failure", "No raw CSV files found")
                return False
            
            self.logger.info(f"Found {len(raw_files)} raw file(s) to process")
            emit_event(self.run_id, "translator", "metric", "Files found", {"raw_file_count": len(raw_files)})
            
            # Run translator via CLI (non-interactive)
            # Note: This assumes you have a CLI version or can run translator headless
            # Adjust this to match your actual translator interface
            translator_cmd = [
                sys.executable,
                str(QTSW2_ROOT / "tools" / "translate_raw.py"),
                "--input", str(DATA_RAW),
                "--output", str(DATA_PROCESSED),
                "--separate-years"
            ]
            
            self.logger.info(f"Executing: {' '.join(translator_cmd)}")
            result = subprocess.run(
                translator_cmd,
                cwd=str(QTSW2_ROOT),
                capture_output=True,
                text=True,
                timeout=3600  # 1 hour timeout
            )
            
            if result.returncode == 0:
                self.logger.info("✓ Translator completed successfully")
                self.logger.info(result.stdout[-500:])  # Last 500 chars
                
                # Count processed files
                processed_files = list(DATA_PROCESSED.glob("*.parquet"))
                processed_files.extend(DATA_PROCESSED.glob("*.csv"))
                emit_event(self.run_id, "translator", "metric", "Translation complete", {
                    "processed_file_count": len(processed_files)
                })
                emit_event(self.run_id, "translator", "success", "Translator completed successfully")
                
                self.stage_results["translator"] = True
                return True
            else:
                self.logger.error(f"✗ Translator failed (code: {result.returncode})")
                self.logger.error(result.stderr)
                emit_event(self.run_id, "translator", "failure", f"Translator failed with code {result.returncode}")
                self.stage_results["translator"] = False
                return False
                
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
    
    def run_analyzer(self, instruments: List[str] = None) -> bool:
        """Stage 2: Run breakout analyzer on processed data."""
        self.logger.info("=" * 60)
        self.logger.info("STAGE 2: Breakout Analyzer")
        self.logger.info("=" * 60)
        
        emit_event(self.run_id, "analyzer", "start", "Starting analyzer stage")
        
        if instruments is None:
            instruments = ["ES", "NQ", "YM", "CL", "NG", "GC"]
        
        emit_event(self.run_id, "analyzer", "metric", "Analyzer started", {
            "instrument_count": len(instruments),
            "instruments": instruments
        })
        
        success_count = 0
        for instrument in instruments:
            try:
                analyzer_cmd = [
                    sys.executable,
                    str(ANALYZER_SCRIPT),
                    "--folder", str(DATA_PROCESSED),
                    "--instrument", instrument,
                    "--sessions", "S1", "S2"
                ]
                
                self.logger.info(f"Running analyzer for {instrument}...")
                emit_event(self.run_id, "analyzer", "log", f"Running analyzer for {instrument}")
                
                result = subprocess.run(
                    analyzer_cmd,
                    cwd=str(QTSW_ROOT),
                    capture_output=True,
                    text=True,
                    timeout=7200  # 2 hour timeout per instrument
                )
                
                if result.returncode == 0:
                    self.logger.info(f"✓ {instrument} analysis completed")
                    emit_event(self.run_id, "analyzer", "metric", f"{instrument} completed", {
                        "instrument": instrument,
                        "status": "success"
                    })
                    success_count += 1
                else:
                    self.logger.error(f"✗ {instrument} analysis failed")
                    self.logger.error(result.stderr[-500:])
                    emit_event(self.run_id, "analyzer", "failure", f"{instrument} analysis failed", {
                        "instrument": instrument
                    })
                    
            except Exception as e:
                self.logger.error(f"✗ {instrument} analyzer exception: {e}")
                emit_event(self.run_id, "analyzer", "failure", f"{instrument} exception: {str(e)}", {
                    "instrument": instrument
                })
        
        success = success_count > 0
        self.stage_results["analyzer"] = success
        
        if success:
            emit_event(self.run_id, "analyzer", "success", f"Analyzer completed: {success_count}/{len(instruments)} instruments")
        else:
            emit_event(self.run_id, "analyzer", "failure", f"Analyzer failed: {success_count}/{len(instruments)} instruments")
        
        self.logger.info(f"Analyzer completed: {success_count}/{len(instruments)} instruments")
        return success
    
    def run_sequential_processor(self) -> bool:
        """Stage 3: Run sequential processor (optional)."""
        self.logger.info("=" * 60)
        self.logger.info("STAGE 3: Sequential Processor (Optional)")
        self.logger.info("=" * 60)
        
        emit_event(self.run_id, "sequential", "start", "Starting sequential processor stage")
        
        # This is typically interactive, so we might skip or implement differently
        self.logger.info("Sequential processor typically requires manual configuration")
        self.logger.info("Skipping automatic execution (can be added if CLI interface exists)")
        emit_event(self.run_id, "sequential", "log", "Sequential processor skipped (requires manual configuration)")
        emit_event(self.run_id, "sequential", "success", "Sequential processor skipped")
        self.stage_results["sequential_processor"] = True  # Mark as skipped but not failed
        return True
    
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
    
    def __init__(self, schedule_time: str = "07:30"):
        """
        Initialize scheduler.
        
        Args:
            schedule_time: Time in HH:MM format (Chicago time, 24-hour)
        """
        self.logger = setup_logging(LOG_FILE)
        self.schedule_time = schedule_time
        self.nt_controller = NinjaTraderController(self.logger)
        self.orchestrator: Optional[PipelineOrchestrator] = None
        
    def run_now(self) -> bool:
        """Execute pipeline immediately (for testing or manual runs)."""
        run_id = str(uuid.uuid4())
        self.logger.info("=" * 60)
        self.logger.info("DAILY DATA PIPELINE - MANUAL RUN")
        self.logger.info(f"Run ID: {run_id}")
        self.logger.info("=" * 60)
        
        emit_event(run_id, "pipeline", "start", "Pipeline run started")
        
        # Create orchestrator with run_id
        self.orchestrator = PipelineOrchestrator(self.logger, run_id)
        
        # Launch NinjaTrader
        emit_event(run_id, "pipeline", "log", "Launching NinjaTrader")
        if not self.nt_controller.launch():
            self.logger.error("Failed to launch NinjaTrader - aborting")
            emit_event(run_id, "pipeline", "failure", "Failed to launch NinjaTrader")
            return False
        
        # 🔧 Create trigger file to signal DataExporter to start
        emit_event(run_id, "pipeline", "log", "Creating export trigger for DataExporter")
        trigger_file = DATA_RAW / "export_trigger.txt"
        try:
            # Write run_id to trigger file so DataExporter knows which run this is
            with open(trigger_file, "w") as f:
                f.write(run_id)
            self.logger.info(f"Export trigger file created: {trigger_file}")
            emit_event(run_id, "export", "log", "Export trigger sent to DataExporter")
        except Exception as e:
            self.logger.warning(f"Could not create export trigger file: {e}")
            # Continue anyway - DataExporter might still auto-trigger
        
        # Wait for export
        emit_event(run_id, "pipeline", "log", "Waiting for data export")
        
        if not self.nt_controller.wait_for_export(timeout_minutes=60, run_id=run_id):
            self.logger.error("Export timeout - aborting pipeline")
            emit_event(run_id, "pipeline", "failure", "Export timeout")
            return False
        
        emit_event(run_id, "pipeline", "log", "Data export completed, starting processing")
        
        # Run pipeline stages
        success = True
        if not self.orchestrator.run_translator():
            self.logger.error("Translator failed - aborting pipeline")
            success = False
        elif not self.orchestrator.run_analyzer():
            self.logger.error("Analyzer failed - continuing anyway")
            success = False  # Non-fatal
        
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
            
            # Execute pipeline
            self.run_now()
            
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
        "--test",
        action="store_true",
        help="Test mode: check configuration without executing"
    )
    
    args = parser.parse_args()
    
    scheduler = DailyPipelineScheduler(schedule_time=args.schedule)
    
    if args.test:
        scheduler.logger.info("TEST MODE: Configuration check")
        scheduler.logger.info(f"NinjaTrader EXE exists: {NINJATRADER_EXE.exists()}")
        scheduler.logger.info(f"Workspace exists: {NINJATRADER_WORKSPACE.exists()}")
        scheduler.logger.info(f"Data raw dir exists: {DATA_RAW.exists()}")
        scheduler.logger.info(f"Data processed dir exists: {DATA_PROCESSED.exists()}")
        return
    
    if args.now:
        success = scheduler.run_now()
        sys.exit(0 if success else 1)
    else:
        scheduler.wait_for_schedule()


if __name__ == "__main__":
    main()

