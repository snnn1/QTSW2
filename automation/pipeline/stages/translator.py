"""
Translator Service - Processes raw CSV files into processed Parquet files

Single Responsibility: Translate raw data files
"""

import sys
import re
import logging
from pathlib import Path
from typing import List, Set, Optional
from dataclasses import dataclass

from automation.services.process_supervisor import ProcessSupervisor, ProcessResult
from automation.services.file_manager import FileManager
from automation.services.event_logger import EventLogger
from automation.config import PipelineConfig


@dataclass
class TranslatorResult:
    """Result of translator stage execution"""
    stage_name: str = "translator"
    status: str = "pending"  # success, failure, skipped
    raw_files_found: int = 0
    files_written: int = 0
    processed_files: Set[Path] = None
    duration_seconds: float = 0.0
    error_message: Optional[str] = None
    
    def __post_init__(self):
        if self.processed_files is None:
            self.processed_files = set()


class TranslatorService:
    """
    Service for translating raw CSV files to processed Parquet files.
    
    Responsibilities:
    - Inspect raw data directory for CSVs
    - Build command to invoke translation tool
    - Call process supervisor
    - Interpret result and return stage result
    - Does NOT delete files (only reports what was processed)
    """
    
    def __init__(
        self,
        config: PipelineConfig,
        logger: logging.Logger,
        process_supervisor: ProcessSupervisor,
        file_manager: FileManager,
        event_logger: EventLogger
    ):
        self.config = config
        self.logger = logger
        self.process_supervisor = process_supervisor
        self.file_manager = file_manager
        self.event_logger = event_logger
    
    def run(self, run_id: str) -> TranslatorResult:
        """
        Run the translator stage.
        
        Args:
            run_id: Pipeline run ID
        
        Returns:
            TranslatorResult with stage outcome
        """
        result = TranslatorResult()
        
        # Check for raw files
        raw_files = self.file_manager.scan_directory(
            self.config.data_raw,
            "*.csv"
        )
        result.raw_files_found = len(raw_files)
        
        if not raw_files:
            self.logger.warning("No raw CSV files found")
            self.event_logger.emit(run_id, "translator", "failure", "No raw CSV files found")
            result.status = "skipped"
            return result
        
        self.logger.info(f"Found {len(raw_files)} raw file(s) to process")
        self.event_logger.emit(run_id, "translator", "start", "Starting data translator stage")
        self.event_logger.emit(run_id, "translator", "metric", "Files found", {"raw_file_count": len(raw_files)})
        
        # Build command
        translator_cmd = [
            sys.executable,
            str(self.config.translator_script),
            "--input", str(self.config.data_raw.resolve()),
            "--output", str(self.config.data_processed.resolve()),
            "--separate-years",
            "--no-merge"
        ]
        
        # Track files written during execution
        files_written = []
        processed_instruments = set()
        expected_instruments = {f.stem.upper() for f in raw_files}
        
        def on_stdout_line(line: str):
            """Handle stdout lines"""
            self.logger.info(f"  {line}")
            
            # Check for completion message
            if "[SUCCESS]" in line and "completed successfully" in line:
                self.event_logger.emit(run_id, "translator", "log", line)
            
            # Track files written
            if "Saved:" in line or "Saved PARQUET:" in line or "Saved CSV:" in line:
                files_written.append(line)
                self.event_logger.emit(run_id, "translator", "log", f"File written: {line}")
                
                # Extract instrument from saved file
                if ".parquet" in line:
                    match = re.search(r'([A-Z]+)_(\d{4})', line)
                    if match:
                        instrument = match.group(1)
                        year = match.group(2)
                        processed_instruments.add(instrument)
                        self.event_logger.emit(run_id, "translator", "metric", f"Wrote {instrument} {year} file", {
                            "instrument": instrument,
                            "year": year,
                            "file_type": "parquet"
                        })
                        self.logger.info(f"  ✓ {instrument} ({len(processed_instruments)}/{len(expected_instruments)} instruments complete)")
        
        def on_stderr_line(line: str):
            """Handle stderr lines"""
            self.logger.warning(f"  [stderr] {line}")
            if "error" in line.lower() or "exception" in line.lower():
                self.event_logger.emit(run_id, "translator", "log", f"Warning: {line}")
        
        def on_progress(metrics: dict):
            """Handle progress updates"""
            elapsed_minutes = int(metrics["elapsed_seconds"] / 60)
            self.event_logger.emit(run_id, "translator", "metric", 
                f"Translation in progress ({elapsed_minutes} min elapsed, {len(files_written)} files written)", {
                    "elapsed_minutes": elapsed_minutes,
                    "files_written": len(files_written)
                })
        
        def completion_detector(stdout_lines: List[str]) -> bool:
            """Detect if translator has completed"""
            # Check for success message
            full_output = ''.join(stdout_lines)
            if "[SUCCESS]" in full_output and "completed successfully" in full_output:
                return True
            
            # Check if all expected instruments have been processed
            if len(processed_instruments) >= len(expected_instruments):
                # Wait a bit to see if more output comes
                return False  # Let timeout handle it
            
            return False
        
        # Execute translator
        process_result = self.process_supervisor.execute(
            command=translator_cmd,
            cwd=self.config.qtsw2_root,
            on_stdout_line=on_stdout_line,
            on_stderr_line=on_stderr_line,
            on_progress=on_progress,
            completion_detector=completion_detector,
            completion_timeout=30
        )
        
        result.duration_seconds = process_result.execution_time
        result.files_written = len(files_written)
        
        # Check for processed files
        if self.config.data_processed.exists():
            processed_files = list(self.config.data_processed.glob("*.parquet"))
            processed_files.extend(list(self.config.data_processed.glob("*.csv")))
            result.processed_files = {f for f in processed_files if f.parent == self.config.data_processed}
        
        # Determine success
        # Success if: returncode 0, success message, or files were written
        translator_succeeded = (
            process_result.success or
            len(result.processed_files) > 0 or
            len(files_written) > 0
        )
        
        if translator_succeeded:
            result.status = "success"
            self.logger.info("✓ Translator completed successfully")
            self.event_logger.emit(run_id, "translator", "metric", "Translation complete", {
                "processed_file_count": len(result.processed_files),
                "files_written_count": len(files_written),
                "return_code": process_result.returncode
            })
            self.event_logger.emit(run_id, "translator", "success", "Translator completed successfully")
        else:
            result.status = "failure"
            result.error_message = f"Translator failed (code: {process_result.returncode})"
            self.logger.error(f"✗ {result.error_message}")
            self.event_logger.emit(run_id, "translator", "failure", result.error_message)
        
        return result



