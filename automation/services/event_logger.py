"""
EventLogger - Structured event logging service

Single Responsibility: Emit structured events to JSONL files
Tolerant to failures - never crashes the pipeline
"""

import json
import shutil
from pathlib import Path
from datetime import datetime
from typing import Optional, Dict
import pytz
import logging


class EventLogger:
    """
    Manages structured event logging for pipeline runs.
    Each instance has its own log file - no global state.
    Tolerant to failures - never crashes the pipeline.
    
    Automatically rotates files when they exceed MAX_FILE_SIZE_MB (50 MB).
    """
    
    MAX_FILE_SIZE_MB = 50  # Rotate files at 50 MB
    MAX_FILE_SIZE_BYTES = MAX_FILE_SIZE_MB * 1024 * 1024
    
    def __init__(self, log_file: Path, timezone=pytz.timezone("America/Chicago"), logger: Optional[logging.Logger] = None):
        self.log_file = log_file
        self.timezone = timezone
        self.logger = logger or logging.getLogger("EventLogger")
        # Ensure directory exists
        self.log_file.parent.mkdir(parents=True, exist_ok=True)
        # Create file if it doesn't exist
        if not self.log_file.exists():
            self.log_file.touch()
    
    def _rotate_if_needed(self) -> None:
        """Rotate log file if it exceeds MAX_FILE_SIZE_MB"""
        try:
            if self.log_file.exists():
                file_size = self.log_file.stat().st_size
                if file_size >= self.MAX_FILE_SIZE_BYTES:
                    # Create archive directory
                    archive_dir = self.log_file.parent / "archive"
                    archive_dir.mkdir(parents=True, exist_ok=True)
                    
                    # Generate archive filename with timestamp
                    timestamp = datetime.now(self.timezone).strftime("%Y%m%d_%H%M%S")
                    archive_path = archive_dir / f"{self.log_file.stem}_{timestamp}{self.log_file.suffix}"
                    
                    # Move current file to archive
                    shutil.move(str(self.log_file), str(archive_path))
                    self.logger.info(f"Rotated log file {self.log_file.name} ({file_size / (1024*1024):.2f} MB) → archive/{archive_path.name}")
                    
                    # Create new empty file
                    self.log_file.touch()
        except Exception as e:
            # Don't fail if rotation fails - just log and continue
            self.logger.warning(f"Failed to rotate log file {self.log_file.name}: {e}")
    
    def emit(
        self,
        run_id: str,
        stage: str,
        event: str,
        msg: Optional[str] = None,
        data: Optional[Dict] = None
    ) -> None:
        """
        Emit a structured event to the log file.
        Never raises exceptions - failures are logged but don't crash pipeline.
        
        Args:
            run_id: Unique identifier for this pipeline run
            stage: Current pipeline stage (translator, analyzer, merger, etc.)
            event: Event type (start, metric, success, failure, log)
            msg: Optional message
            data: Optional data dictionary
        """
        event_obj = {
            "run_id": run_id,
            "stage": stage,
            "event": event,
            "timestamp": datetime.now(self.timezone).isoformat()
        }
        
        if msg is not None:
            event_obj["msg"] = msg
        
        if data is not None:
            event_obj["data"] = data
        
        try:
            # Rotate file if it's too large (before writing)
            self._rotate_if_needed()
            
            with open(self.log_file, "a", encoding="utf-8") as f:
                f.write(json.dumps(event_obj) + "\n")
                f.flush()  # Ensure immediate write
        except Exception as e:
            # Don't fail pipeline if event logging fails
            self.logger.warning(f"Failed to write event log: {e}")

