#!/usr/bin/env python3
"""
Parallel Analyzer Runner
Processes multiple instruments in parallel to speed up analyzer execution
"""

import sys
import subprocess
import multiprocessing
import os
import json
from pathlib import Path
from concurrent.futures import ProcessPoolExecutor, as_completed
from typing import List, Tuple, Optional
import time
import logging

# Setup logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger(__name__)

def emit_event(run_id: Optional[str], stage: str, event: str, msg: str = "", data: dict = None):
    """Emit event to event log file if PIPELINE_EVENT_LOG is set."""
    event_log_path = os.environ.get("PIPELINE_EVENT_LOG")
    if not event_log_path:
        return
    
    # Ensure run_id is set - get from environment if not provided
    if not run_id:
        run_id = os.environ.get("PIPELINE_RUN_ID")
    
    # If still no run_id, this is an error - events require run_id for EventBus
    if not run_id:
        logger.error(f"CRITICAL: Event emitted without run_id: {stage}/{event}. Event will be rejected by EventBus. Check that PIPELINE_RUN_ID environment variable is set.")
        # Don't write event without run_id - it will be rejected anyway
        return
    
    try:
        from datetime import datetime
        import pytz
        # Use Chicago timezone to match pipeline format
        chicago_tz = pytz.timezone("America/Chicago")
        timestamp_iso = datetime.now(chicago_tz).isoformat()
        
        event_data = {
            "run_id": run_id,
            "stage": stage,
            "event": event,
            "msg": msg,
            "timestamp": timestamp_iso,
            "data": data or {}
        }
        
        with open(event_log_path, 'a', encoding='utf-8') as f:
            f.write(json.dumps(event_data) + '\n')
            f.flush()
    except Exception as e:
        logger.warning(f"Failed to emit event: {e}")

# Base paths
QTSW2_ROOT = Path(__file__).parent.parent
ANALYZER_SCRIPT = QTSW2_ROOT / "modules" / "analyzer" / "scripts" / "run_data_processed.py"
DATA_PROCESSED = QTSW2_ROOT / "data" / "data_processed"
EVENT_LOGS_DIR = QTSW2_ROOT / "automation" / "logs" / "events"

def run_analyzer_instrument(instrument: str, data_folder: Path, analyzer_script: Path, run_id: Optional[str] = None) -> Tuple[str, bool, str]:
    """
    Run analyzer for a single instrument.
    
    Returns:
        Tuple of (instrument, success, output_message)
    """
    start_time = time.time()
    start_timestamp = time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(start_time))
    
    # Build command with all available time slots for both sessions
    # Explicitly include all trading days (Mon-Fri) to ensure Friday is processed
    analyzer_cmd = [
        sys.executable,
        str(analyzer_script),
        "--folder", str(data_folder),
        "--instrument", instrument,
        "--sessions", "S1", "S2",
        "--slots", 
        "S1:07:30", "S1:08:00", "S1:09:00",  # All S1 slots
        "S2:09:30", "S2:10:00", "S2:10:30", "S2:11:00",  # All S2 slots
        "--days", "Mon", "Tue", "Wed", "Thu", "Fri",  # Explicitly include all trading days including Friday
    ]
    
    try:
        logger.info(f"Starting analyzer for {instrument}...")
        # Emit file processing start event
        emit_event(run_id, "analyzer", "file_start", f"Starting analysis for {instrument}", {
            "instrument": instrument,
            "start_time": start_timestamp,
            "start_timestamp": start_time
        })
        
        # Run analyzer process
        # Pass environment variables (especially PIPELINE_RUN) to child process
        process_env = os.environ.copy()
        if run_id:
            process_env["PIPELINE_RUN_ID"] = run_id
        
        process = subprocess.Popen(
            analyzer_cmd,
            cwd=str(QTSW2_ROOT),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            bufsize=1,
            env=process_env  # Pass environment to ensure PIPELINE_RUN is inherited
        )
        
        # Collect output (last 50 lines for error reporting) AND stream progress in real-time
        stdout_lines = []
        stderr_lines = []
        max_lines = 50
        
        def collect_output(stream, lines_list, prefix=""):
            # Only print progress if NOT running via pipeline (no PIPELINE_EVENT_LOG set)
            # This prevents cluttering dashboard events - pipeline handles its own logging
            is_pipeline_run = bool(os.environ.get("PIPELINE_EVENT_LOG"))
            for line in iter(stream.readline, ''):
                if line:
                    line_stripped = line.rstrip()
                    lines_list.append(line_stripped)
                    if len(lines_list) > max_lines:
                        lines_list.pop(0)
                    # Only print progress in CLI mode (not pipeline mode) to avoid dashboard noise
                    if not is_pipeline_run:
                        print(f"[{instrument}] {prefix}{line_stripped}", flush=True)
        
        import threading
        stdout_thread = threading.Thread(target=collect_output, args=(process.stdout, stdout_lines, ""), daemon=True)
        stderr_thread = threading.Thread(target=collect_output, args=(process.stderr, stderr_lines, "[stderr] "), daemon=True)
        stdout_thread.start()
        stderr_thread.start()
        
        # Wait for completion and ensure threads finish
        returncode = process.wait()
        # Give threads a moment to finish reading any remaining output
        stdout_thread.join(timeout=1.0)
        stderr_thread.join(timeout=1.0)
        finish_time = time.time()
        elapsed = finish_time - start_time
        finish_timestamp = time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(finish_time))
        
        if returncode == 0:
            logger.info(f"[OK] {instrument} completed in {elapsed:.1f}s")
            # Emit file processing finish event
            emit_event(run_id, "analyzer", "file_finish", f"Completed analysis for {instrument} in {elapsed:.1f}s", {
                "instrument": instrument,
                "start_time": start_timestamp,
                "finish_time": finish_timestamp,
                "start_timestamp": start_time,
                "finish_timestamp": finish_time,
                "duration_seconds": elapsed,
                "status": "success"
            })
            return (instrument, True, f"Completed in {elapsed:.1f}s")
        else:
            error_msg = '\n'.join(stderr_lines[-10:]) if stderr_lines else '\n'.join(stdout_lines[-10:])
            # Check if this is an expected "incomplete data" error (not a real failure)
            is_incomplete_data = "data ends" in error_msg.lower() and "before expected end time" in error_msg.lower()
            
            if is_incomplete_data:
                # This is expected - data is incomplete, analyzer processed what it could
                # Don't log as error or emit failure events
                logger.info(f"ℹ {instrument} processed incomplete data in {elapsed:.1f}s (expected when data is incomplete)")
                # Emit as success since it processed available data correctly
                emit_event(run_id, "analyzer", "file_finish", f"Processed incomplete data for {instrument} in {elapsed:.1f}s", {
                    "instrument": instrument,
                    "start_time": start_timestamp,
                    "finish_time": finish_timestamp,
                    "start_timestamp": start_time,
                    "finish_timestamp": finish_time,
                    "duration_seconds": elapsed,
                    "status": "success",
                    "note": "incomplete_data"
                })
                return (instrument, True, f"Processed incomplete data in {elapsed:.1f}s")
            else:
                # Real error - log and emit failure
                logger.error(f"[ERROR] {instrument} failed after {elapsed:.1f}s")
                logger.error(f"Error output: {error_msg}")
                # Emit file processing finish event with failure
                emit_event(run_id, "analyzer", "file_finish", f"Failed analysis for {instrument} after {elapsed:.1f}s", {
                    "instrument": instrument,
                    "start_time": start_timestamp,
                    "finish_time": finish_timestamp,
                    "start_timestamp": start_time,
                    "finish_timestamp": finish_time,
                    "duration_seconds": elapsed,
                    "status": "failed",
                    "error": error_msg
                })
                return (instrument, False, f"Failed: {error_msg}")
            
    except Exception as e:
        finish_time = time.time()
        elapsed = finish_time - start_time
        finish_timestamp = time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(finish_time))
        logger.error(f"[ERROR] {instrument} exception after {elapsed:.1f}s: {e}")
        # Emit file processing finish event with exception
        emit_event(run_id, "analyzer", "file_finish", f"Exception during analysis for {instrument} after {elapsed:.1f}s", {
            "instrument": instrument,
            "start_time": start_timestamp,
            "finish_time": finish_timestamp,
            "start_timestamp": start_time,
            "finish_timestamp": finish_time,
            "duration_seconds": elapsed,
            "status": "exception",
            "error": str(e)
        })
        return (instrument, False, f"Exception: {str(e)}")


def run_parallel(instruments: List[str], max_workers: int = None, data_folder: Path = None, analyzer_script: Path = None, run_id: Optional[str] = None):
    """
    Run analyzer for multiple instruments in parallel.
    
    Args:
        instruments: List of instrument symbols (e.g., ["ES", "NQ", "CL"])
        max_workers: Maximum number of parallel processes (None = auto-detect)
        data_folder: Path to data_processed folder (default: QTSW2_ROOT/data/data_processed)
        analyzer_script: Path to analyzer script (default: auto-detect)
    """
    if data_folder is None:
        data_folder = DATA_PROCESSED
    if analyzer_script is None:
        analyzer_script = ANALYZER_SCRIPT
    
    if not analyzer_script.exists():
        logger.error(f"Analyzer script not found: {analyzer_script}")
        return False
    
    if not data_folder.exists():
        logger.error(f"Data folder not found: {data_folder}")
        return False
    
    # Auto-detect max_workers if not specified
    if max_workers is None:
        cpu_count = multiprocessing.cpu_count()
        # Use 75% of CPU cores, but at least 2 and at most number of instruments
        max_workers = max(2, min(int(cpu_count * 0.75), len(instruments)))
    
    logger.info("=" * 60)
    logger.info(f"Parallel Analyzer Runner")
    logger.info("=" * 60)
    logger.info(f"Instruments: {', '.join(instruments)}")
    logger.info(f"Parallel workers: {max_workers}")
    logger.info(f"Data folder: {data_folder}")
    logger.info(f"Analyzer script: {analyzer_script}")
    logger.info("=" * 60)
    
    # Don't emit start/log/metric events here - AnalyzerService already emits them
    # This prevents duplicate events in the live feed
    # Only emit file-level events (file_start, file_finish) which are useful for tracking individual instrument progress
    
    start_time = time.time()
    results = {}
    
    # Process instruments in parallel
    with ProcessPoolExecutor(max_workers=max_workers) as executor:
        # Submit all tasks
        future_to_instrument = {
            executor.submit(run_analyzer_instrument, inst, data_folder, analyzer_script, run_id): inst
            for inst in instruments
        }
        
        # Collect results as they complete
        for future in as_completed(future_to_instrument):
            instrument = future_to_instrument[future]
            try:
                inst, success, message = future.result()
                results[inst] = (success, message)
                
                # Emit completion event
                if success:
                    emit_event(run_id, "analyzer", "log", f"[{inst}] [OK] Completed: {message}")
                else:
                    # Only emit failure if it's a real error (not incomplete data)
                    if "incomplete data" not in message.lower() and "data ends" not in message.lower():
                        emit_event(run_id, "analyzer", "log", f"[{inst}] [ERROR] Failed: {message}")
                        emit_event(run_id, "analyzer", "failure", f"[{inst}] Analyzer failed: {message}")
            except Exception as e:
                logger.error(f"Exception processing {instrument}: {e}")
                results[instrument] = (False, f"Exception: {str(e)}")
                emit_event(run_id, "analyzer", "failure", f"[{instrument}] Exception: {str(e)}")
    
    # Summary
    elapsed = time.time() - start_time
    successful = sum(1 for success, _ in results.values() if success)
    failed = len(results) - successful
    
    logger.info("=" * 60)
    logger.info("Summary")
    logger.info("=" * 60)
    logger.info(f"Total time: {elapsed:.1f}s ({elapsed/60:.1f} minutes)")
    logger.info(f"Successful: {successful}/{len(instruments)}")
    logger.info(f"Failed: {failed}/{len(instruments)}")
    
    # Emit summary event
    if successful == len(instruments):
        emit_event(run_id, "analyzer", "success", f"All {len(instruments)} instruments completed successfully in {elapsed/60:.1f} minutes", {
            "total_time_minutes": elapsed / 60,
            "successful": successful,
            "failed": failed
        })
    else:
        emit_event(run_id, "analyzer", "failure", f"Analyzer completed with {failed} failure(s) out of {len(instruments)} instruments", {
            "total_time_minutes": elapsed / 60,
            "successful": successful,
            "failed": failed
        })
    
    if failed > 0:
        logger.info("\nFailed instruments:")
        for inst, (success, msg) in results.items():
            if not success:
                logger.info(f"  - {inst}: {msg}")
    
    return successful == len(instruments)


def main():
    """Main entry point."""
    import argparse
    
    parser = argparse.ArgumentParser(description="Run analyzer for multiple instruments in parallel")
    parser.add_argument("--instruments", nargs="+", required=True,
                       choices=["ES", "NQ", "YM", "CL", "NG", "GC", "RTY"],
                       help="List of instruments to process")
    parser.add_argument("--workers", type=int, default=None,
                       help="Number of parallel workers (default: auto-detect)")
    parser.add_argument("--folder", type=str, default=None,
                       help="Path to data_processed folder (default: auto-detect)")
    parser.add_argument("--analyzer-script", type=str, default=None,
                       help="Path to analyzer script (default: auto-detect)")
    parser.add_argument("--run-id", type=str, default=None,
                       help="Pipeline run ID for event logging")
    
    args = parser.parse_args()
    
    data_folder = Path(args.folder) if args.folder else None
    analyzer_script = Path(args.analyzer_script) if args.analyzer_script else None
    run_id = args.run_id or os.environ.get("PIPELINE_RUN_ID")
    
    success = run_parallel(
        instruments=args.instruments,
        max_workers=args.workers,
        data_folder=data_folder,
        analyzer_script=analyzer_script,
        run_id=run_id
    )
    
    sys.exit(0 if success else 1)


if __name__ == "__main__":
    main()

