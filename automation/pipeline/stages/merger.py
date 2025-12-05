"""
Merger Service - Consolidates daily analyzer files into monthly files

Single Responsibility: Merge analyzer output files
"""

import sys
import logging
from pathlib import Path
from typing import Set, Optional
from dataclasses import dataclass

from automation.services.process_supervisor import ProcessSupervisor, ProcessResult
from automation.services.event_logger import EventLogger
from automation.config import PipelineConfig


@dataclass
class MergerResult:
    """Result of merger stage execution"""
    stage_name: str = "merger"
    status: str = "pending"  # success, failure, skipped
    duration_seconds: float = 0.0
    error_message: Optional[str] = None
    merged_files_count: int = 0


class MergerService:
    """
    Service for merging daily analyzer files into monthly files.
    
    Responsibilities:
    - Invoke the merger tool
    - Interpret success/failure
    - Report metrics (merged file counts, etc.)
    """
    
    def __init__(
        self,
        config: PipelineConfig,
        logger: logging.Logger,
        process_supervisor: ProcessSupervisor,
        event_logger: EventLogger
    ):
        self.config = config
        self.logger = logger
        self.process_supervisor = process_supervisor
        self.event_logger = event_logger
    
    def run(self, run_id: str) -> MergerResult:
        """
        Run the merger stage.
        
        Args:
            run_id: Pipeline run ID
        
        Returns:
            MergerResult with stage outcome
        """
        result = MergerResult()
        
        self.logger.info("Starting data merger")
        self.event_logger.emit(run_id, "merger", "start", "Starting data merger")
        
        # Build command
        merger_cmd = [
            sys.executable,
            str(self.config.merger_script)
        ]
        
        def on_stdout_line(line: str):
            """Handle stdout lines"""
            self.logger.info(f"  {line}")
            # Emit key milestones
            if any(keyword in line.upper() for keyword in ["MERGED", "PROCESSED", "COMPLETE", "ERROR", "FAILED"]):
                self.event_logger.emit(run_id, "merger", "log", line)
        
        def on_stderr_line(line: str):
            """Handle stderr lines"""
            self.logger.warning(f"  [stderr] {line}")
            if "ERROR" in line.upper() or "FAILED" in line.upper():
                self.event_logger.emit(run_id, "merger", "log", f"ERROR: {line}")
        
        # Execute merger
        process_result = self.process_supervisor.execute(
            command=merger_cmd,
            cwd=self.config.qtsw2_root,
            on_stdout_line=on_stdout_line,
            on_stderr_line=on_stderr_line
        )
        
        result.duration_seconds = process_result.execution_time
        
        # Determine success
        if process_result.success:
            result.status = "success"
            self.logger.info("✓ Data merger completed successfully")
            self.event_logger.emit(run_id, "merger", "success", "Data merger completed successfully")
        else:
            result.status = "failure"
            result.error_message = f"Merger failed (code: {process_result.returncode})"
            self.logger.error(f"✗ {result.error_message}")
            self.event_logger.emit(run_id, "merger", "failure", result.error_message)
        
        return result



