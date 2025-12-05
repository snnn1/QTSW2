"""
Analyzer Service - Runs breakout analysis on processed data

Single Responsibility: Analyze processed data files
"""

import sys
import os
import logging
from pathlib import Path
from typing import List, Set, Optional
from dataclasses import dataclass

from automation.services.process_supervisor import ProcessSupervisor, ProcessResult
from automation.services.file_manager import FileManager
from automation.services.event_logger import EventLogger
from automation.config import PipelineConfig


@dataclass
class AnalyzerResult:
    """Result of analyzer stage execution"""
    stage_name: str = "analyzer"
    status: str = "pending"  # success, failure, skipped
    processed_files_found: int = 0
    instruments_processed: List[str] = None
    duration_seconds: float = 0.0
    error_message: Optional[str] = None
    output_files_created: Set[Path] = None
    
    def __post_init__(self):
        if self.instruments_processed is None:
            self.instruments_processed = []
        if self.output_files_created is None:
            self.output_files_created = set()


class AnalyzerService:
    """
    Service for running breakout analysis on processed data.
    
    Responsibilities:
    - Inspect processed directory for parquet/CSV files
    - Decide which instruments to analyze
    - Build commands for parallel analyzer runner
    - Call process supervisor
    - Interpret success/failure
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
    
    def run(self, run_id: str, instruments: Optional[List[str]] = None) -> AnalyzerResult:
        """
        Run the analyzer stage.
        
        Args:
            run_id: Pipeline run ID
            instruments: Optional list of instruments to analyze (defaults to config)
        
        Returns:
            AnalyzerResult with stage outcome
        """
        result = AnalyzerResult()
        
        if instruments is None:
            instruments = self.config.default_instruments.copy()
        
        # Check for processed files
        processed_files = []
        for pattern in ["*.parquet", "*.csv"]:
            processed_files.extend(
                self.file_manager.scan_directory(self.config.data_processed, pattern)
            )
        
        result.processed_files_found = len(processed_files)
        
        if not processed_files:
            self.logger.warning("No processed files found - cannot run analyzer")
            self.event_logger.emit(run_id, "analyzer", "failure", "No processed files found in data/processed", {
                "data_folder": str(self.config.data_processed),
                "folder_exists": self.config.data_processed.exists()
            })
            result.status = "skipped"
            return result
        
        self.logger.info(f"Found {len(processed_files)} processed file(s)")
        self.event_logger.emit(run_id, "analyzer", "start", "Starting analyzer stage")
        self.event_logger.emit(run_id, "analyzer", "metric", "Analyzer started", {
            "instrument_count": len(instruments),
            "instruments": instruments,
            "processed_file_count": len(processed_files)
        })
        
        # Build command for parallel analyzer runner
        parallel_cmd = [
            sys.executable,
            str(self.config.parallel_analyzer_script),
            "--instruments"
        ] + instruments + [
            "--folder", str(self.config.data_processed),
            "--run-id", run_id
        ]
        
        self.logger.info(f"Running parallel analyzer for {len(instruments)} instrument(s): {', '.join(instruments)}")
        
        # Set environment variables for event logging
        env = os.environ.copy()
        env["PIPELINE_RUN"] = "1"
        env["PIPELINE_EVENT_LOG"] = str(self.config.event_logs_dir / f"pipeline_{run_id}.jsonl")
        env["PIPELINE_RUN_ID"] = run_id
        
        def on_stdout_line(line: str):
            """Handle stdout lines"""
            line_upper = line.upper()
            if any(keyword in line_upper for keyword in ['ERROR', 'WARNING', 'COMPLETED', 'FAILED', 'SUCCESS']):
                self.logger.info(f"[Parallel Analyzer] {line}")
                self.event_logger.emit(run_id, "analyzer", "log", line)
        
        def on_stderr_line(line: str):
            """Handle stderr lines"""
            if "error" in line.lower() or "exception" in line.lower():
                self.logger.warning(f"[Parallel Analyzer stderr] {line}")
                self.event_logger.emit(run_id, "analyzer", "log", f"Warning: {line}")
        
        def on_progress(metrics: dict):
            """Handle progress updates"""
            elapsed_minutes = int(metrics["elapsed_seconds"] / 60)
            self.event_logger.emit(run_id, "analyzer", "metric",
                f"Analysis in progress ({elapsed_minutes} min elapsed)", {
                    "elapsed_minutes": elapsed_minutes
                })
        
        # Execute analyzer
        process_result = self.process_supervisor.execute(
            command=parallel_cmd,
            cwd=self.config.qtsw2_root,
            on_stdout_line=on_stdout_line,
            on_stderr_line=on_stderr_line,
            on_progress=on_progress
        )
        
        result.duration_seconds = process_result.execution_time
        result.instruments_processed = instruments
        
        # Check for output files
        if self.config.analyzer_runs.exists():
            output_files = list(self.config.analyzer_runs.rglob("*.parquet"))
            result.output_files_created = set(output_files)
        
        # Determine success
        analyzer_succeeded = process_result.success or len(result.output_files_created) > 0
        
        if analyzer_succeeded:
            result.status = "success"
            self.logger.info("✓ Analyzer completed successfully")
            self.event_logger.emit(run_id, "analyzer", "success", "Analyzer completed successfully")
        else:
            result.status = "failure"
            result.error_message = f"Analyzer failed (code: {process_result.returncode})"
            self.logger.error(f"✗ {result.error_message}")
            self.event_logger.emit(run_id, "analyzer", "failure", result.error_message)
        
        return result



