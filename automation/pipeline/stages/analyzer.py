"""
Analyzer Service - Runs parallel analyzer on translated data

Single Responsibility: Invoke parallel analyzer to process translated Parquet files
"""

import sys
import logging
from pathlib import Path
from typing import Optional
from dataclasses import dataclass

from automation.services.process_supervisor import ProcessSupervisor, ProcessResult
from automation.services.event_logger import EventLogger
from automation.config import PipelineConfig

# FileManager may not be available - make it optional
try:
    from automation.services.file_manager import FileManager
except ImportError:
    FileManager = None  # type: ignore


@dataclass
class AnalyzerResult:
    """Result of analyzer stage execution"""
    stage_name: str = "analyzer"
    status: str = "pending"  # success, failure, skipped
    duration_seconds: float = 0.0
    error_message: Optional[str] = None
    instruments_processed: int = 0


class AnalyzerService:
    """
    Service for running the parallel analyzer on translated data.
    
    Responsibilities:
    - Check for translated files
    - Invoke parallel analyzer script
    - Interpret success/failure
    - Report metrics (instruments processed, etc.)
    """
    
    def __init__(
        self,
        config: PipelineConfig,
        logger: logging.Logger,
        process_supervisor: ProcessSupervisor,
        file_manager,  # Optional[FileManager] - may be None
        event_logger: EventLogger
    ):
        self.config = config
        self.logger = logger
        self.process_supervisor = process_supervisor
        self.file_manager = file_manager
        self.event_logger = event_logger
    
    def run(self, run_id: str) -> AnalyzerResult:
        """
        Run the analyzer stage.
        
        Args:
            run_id: Pipeline run ID
        
        Returns:
            AnalyzerResult with stage outcome
        """
        result = AnalyzerResult()
        
        self.logger.info("Starting analyzer stage")
        self.event_logger.emit(run_id, "analyzer", "start", "Starting analyzer stage")
        
        # Check for translated files (prefer data_translated, fallback to data_processed for legacy)
        input_folder = self.config.data_translated
        if not input_folder.exists():
            # Fallback to legacy path if data_translated doesn't exist
            legacy_path = self.config.qtsw2_root / "data" / "processed"
            if legacy_path.exists():
                input_folder = legacy_path
                self.logger.warning(f"Using legacy data_processed folder: {legacy_path}")
            else:
                result.status = "skipped"
                result.error_message = f"No input data found (checked {self.config.data_translated} and {legacy_path})"
                self.logger.warning(result.error_message)
                self.event_logger.emit(run_id, "analyzer", "log", result.error_message)
                return result
        
        # Find processed files to determine which instruments to analyze
        processed_files = list(input_folder.rglob("*.parquet"))
        
        if not processed_files:
            result.status = "skipped"
            result.error_message = f"No translated Parquet files found in {input_folder}"
            self.logger.warning(result.error_message)
            self.event_logger.emit(run_id, "analyzer", "log", result.error_message)
            return result
        
        self.logger.info(f"Found {len(processed_files)} processed file(s)")
        # Don't emit metric event for file count - too verbose, info is in logs
        
        # Extract unique instruments from file paths
        # Path format: {translated_root}/{instrument}/1m/YYYY/MM/{instrument}_1m_{date}.parquet
        instruments = set()
        for file_path in processed_files:
            try:
                # Get instrument from path: {translated_root}/{instrument}/...
                parts = file_path.parts
                # Find the index after data_translated
                try:
                    translated_idx = next(i for i, part in enumerate(parts) if part == input_folder.name or "translated" in part.lower() or "processed" in part.lower())
                    if translated_idx + 1 < len(parts):
                        instrument = parts[translated_idx + 1].upper()
                        if instrument and len(instrument) <= 4:  # Valid instrument symbols are 2-4 chars
                            instruments.add(instrument)
                except (StopIteration, IndexError):
                    # Fallback: try to extract from filename
                    stem = file_path.stem
                    if "_" in stem:
                        instrument = stem.split("_")[0].upper()
                        if instrument and len(instrument) <= 4:
                            instruments.add(instrument)
            except Exception:
                continue
        
        if not instruments:
            result.status = "skipped"
            result.error_message = "Could not determine instruments from file paths"
            self.logger.warning(result.error_message)
            self.event_logger.emit(run_id, "analyzer", "log", result.error_message)
            return result
        
        instruments_list = sorted(list(instruments))
        self.logger.info(f"Analyzer input folder: {input_folder}")
        self.logger.info(f"Running parallel analyzer for {len(instruments_list)} instrument(s): {', '.join(instruments_list)}")
        # Don't emit log event - too verbose, info is in logs
        
        # Build command
        # run_analyzer_parallel.py expects --instruments (plural) with list of instruments
        analyzer_cmd = [
            sys.executable,
            str(self.config.parallel_analyzer_script),
            "--folder", str(input_folder),
            "--instruments"
        ] + instruments_list
        
        def on_stdout_line(line: str):
            """Handle stdout lines"""
            self.logger.info(f"  {line}")
            # Emit key milestones
            if any(keyword in line.upper() for keyword in ["COMPLETE", "SUCCESS", "ERROR", "FAILED", "PROCESSING"]):
                self.event_logger.emit(run_id, "analyzer", "log", line)
        
        def on_stderr_line(line: str):
            """Handle stderr lines"""
            self.logger.warning(f"  [stderr] {line}")
            if "ERROR" in line.upper() or "FAILED" in line.upper():
                self.event_logger.emit(run_id, "analyzer", "log", f"ERROR: {line}")
        
        # Set environment variables for pipeline run
        import os
        env = os.environ.copy()
        env["PIPELINE_RUN"] = "1"  # Tell analyzer to write to analyzer_temp (not manual_analyzer_runs)
        env["PIPELINE_RUN_ID"] = run_id  # Pass run_id for tracking
        env["PIPELINE_EVENT_LOG"] = str(self.config.event_logs_dir / f"pipeline_{run_id}.jsonl")
        
        # Execute analyzer
        process_result = self.process_supervisor.execute(
            command=analyzer_cmd,
            cwd=self.config.qtsw2_root,
            on_stdout_line=on_stdout_line,
            on_stderr_line=on_stderr_line,
            env=env
        )
        
        result.duration_seconds = process_result.execution_time
        result.instruments_processed = len(instruments_list)
        
        # Determine success
        if process_result.success:
            result.status = "success"
            self.logger.info("[SUCCESS] Analyzer completed successfully")
            self.event_logger.emit(run_id, "analyzer", "success", "Analyzer completed successfully", {
                "instruments_processed": len(instruments_list),
                "instruments": instruments_list
            })
            
            # Write success marker file for run_id-specific validation
            # This ensures validation can check that THIS run produced output, not a previous run
            marker_file = self.config.analyzer_runs / f".success_{run_id}.marker"
            try:
                marker_file.parent.mkdir(parents=True, exist_ok=True)
                marker_file.write_text(f"run_id={run_id}\nstatus=success\ninstruments={','.join(instruments_list)}\n")
                self.logger.debug(f"Wrote analyzer success marker: {marker_file}")
            except Exception as e:
                # Non-fatal - marker file is for validation only
                self.logger.warning(f"Failed to write analyzer success marker: {e}")
        else:
            result.status = "failure"
            result.error_message = f"Analyzer failed (code: {process_result.returncode})"
            self.logger.error(f"[ERROR] {result.error_message}")
            self.event_logger.emit(run_id, "analyzer", "failure", result.error_message)
        
        return result
