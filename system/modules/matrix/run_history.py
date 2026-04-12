"""
Run history tracking for Master Matrix operations.

Tracks all matrix runs (rebuild and window_update) with detailed summaries
for auditability and debugging.
"""

import json
import logging
import uuid
from pathlib import Path
from typing import Dict, Optional
from datetime import datetime

from .logging_config import setup_matrix_logger

logger = setup_matrix_logger(__name__, console=True, level=logging.INFO)


class RunHistory:
    """Tracks matrix run summaries for auditability."""
    
    def __init__(self, history_file: str = "data/matrix/state/run_history.jsonl"):
        """
        Initialize run history tracker.
        
        Args:
            history_file: Path to JSONL file storing run summaries
        """
        self.history_file = Path(history_file)
        self.history_file.parent.mkdir(parents=True, exist_ok=True)
    
    def record_run(
        self,
        mode: str,  # "rebuild" or "window_update"
        run_id: Optional[str] = None,
        requested_days: Optional[int] = None,
        reprocess_start_date: Optional[str] = None,
        merged_data_max_date: Optional[str] = None,
        checkpoint_restore_id: Optional[str] = None,
        rows_read: Optional[int] = None,
        rows_written: Optional[int] = None,
        duration_seconds: Optional[float] = None,
        success: bool = True,
        error_message: Optional[str] = None,
        **kwargs
    ) -> str:
        """
        Record a matrix run summary.
        
        Args:
            mode: Run mode ("rebuild" or "window_update")
            run_id: Optional run ID (generated if not provided)
            requested_days: Number of days requested for window update
            reprocess_start_date: Computed reprocess start date
            merged_data_max_date: Maximum date in merged data used
            checkpoint_restore_id: Checkpoint ID used for restoration
            rows_read: Number of rows read from merged data
            rows_written: Number of rows written to matrix output
            duration_seconds: Run duration in seconds
            success: Whether run succeeded
            error_message: Error message if failed
            **kwargs: Additional fields to include
            
        Returns:
            run_id (str)
        """
        if run_id is None:
            run_id = str(uuid.uuid4())
        
        run_summary = {
            "run_id": run_id,
            "mode": mode,
            "timestamp": datetime.now().isoformat(),
            "requested_days": requested_days,
            "reprocess_start_date": reprocess_start_date,
            "merged_data_max_date": merged_data_max_date,
            "checkpoint_restore_id": checkpoint_restore_id,
            "rows_read": rows_read,
            "rows_written": rows_written,
            "duration_seconds": duration_seconds,
            "success": success,
            "error_message": error_message,
            **kwargs
        }
        
        # Append to JSONL file
        try:
            with open(self.history_file, 'a', encoding='utf-8') as f:
                f.write(json.dumps(run_summary, ensure_ascii=False) + '\n')
            
            logger.info(f"Recorded run {run_id} ({mode}): success={success}")
            return run_id
        except Exception as e:
            logger.error(f"Failed to record run history: {e}")
            raise
    
    def get_recent_runs(self, limit: int = 20) -> list[Dict]:
        """
        Get recent run summaries.
        
        Args:
            limit: Maximum number of runs to return
            
        Returns:
            List of run summary dicts, most recent first
        """
        if not self.history_file.exists():
            return []
        
        runs = []
        try:
            with open(self.history_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        run_data = json.loads(line)
                        runs.append(run_data)
                    except json.JSONDecodeError as e:
                        logger.warning(f"Failed to parse run history line: {e}")
                        continue
        except Exception as e:
            logger.error(f"Failed to read run history: {e}")
            return []
        
        # Sort by timestamp descending
        runs.sort(key=lambda x: x.get('timestamp', ''), reverse=True)
        return runs[:limit]
    
    def get_run_by_id(self, run_id: str) -> Optional[Dict]:
        """
        Get a specific run summary by ID.
        
        Args:
            run_id: Run ID to look up
            
        Returns:
            Run summary dict, or None if not found
        """
        if not self.history_file.exists():
            return None
        
        try:
            with open(self.history_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        run_data = json.loads(line)
                        if run_data.get('run_id') == run_id:
                            return run_data
                    except json.JSONDecodeError:
                        continue
        except Exception as e:
            logger.error(f"Failed to read run history: {e}")
        
        return None


__all__ = ['RunHistory']

