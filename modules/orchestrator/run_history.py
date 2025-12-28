"""
Run History - Persisted Run Summaries

Promotes RunContext to a first-class persisted artifact.
Stores run summaries in JSONL format for auditability and history.
"""

import json
import logging
from pathlib import Path
from typing import Optional, List, Dict, Any
from datetime import datetime
from dataclasses import dataclass, asdict
from enum import Enum


class RunResult(Enum):
    """Run completion result"""
    SUCCESS = "success"
    FAILED = "failed"
    STOPPED = "stopped"


@dataclass
class RunSummary:
    """
    First-class persisted run artifact.
    
    Contains all information needed for auditability and history.
    """
    run_id: str
    started_at: str  # ISO format timestamp
    ended_at: Optional[str] = None  # ISO format timestamp
    result: Optional[str] = None  # success, failed, stopped
    failure_reason: Optional[str] = None
    stages_executed: List[str] = None  # List of stage names executed
    stages_failed: List[str] = None  # List of stage names that failed
    retry_count: int = 0
    metadata: Dict[str, Any] = None
    
    def __post_init__(self):
        if self.stages_executed is None:
            self.stages_executed = []
        if self.stages_failed is None:
            self.stages_failed = []
        if self.metadata is None:
            self.metadata = {}
    
    def to_dict(self) -> Dict[str, Any]:
        """Convert to dictionary for JSON serialization"""
        return {
            "run_id": self.run_id,
            "started_at": self.started_at,
            "ended_at": self.ended_at,
            "result": self.result,
            "failure_reason": self.failure_reason,
            "stages_executed": self.stages_executed,
            "stages_failed": self.stages_failed,
            "retry_count": self.retry_count,
            "metadata": self.metadata,
        }


class RunHistory:
    """
    Manages persisted run summaries in JSONL format.
    
    Each run is persisted as a single JSON line for efficient
    append-only writes and sequential reads.
    """
    
    def __init__(
        self,
        runs_dir: Path,
        logger: Optional[logging.Logger] = None
    ):
        self.runs_dir = runs_dir
        self.logger = logger or logging.getLogger(__name__)
        
        # Ensure runs directory exists
        self.runs_dir.mkdir(parents=True, exist_ok=True)
        
        # JSONL file for run summaries
        self.runs_file = self.runs_dir / "runs.jsonl"
    
    def persist_run(self, summary: RunSummary) -> bool:
        """
        Persist a run summary to JSONL file.
        
        Args:
            summary: RunSummary to persist
            
        Returns:
            True if persisted successfully, False otherwise
        """
        try:
            # Append to JSONL file (append-only, efficient)
            with open(self.runs_file, "a", encoding="utf-8") as f:
                json_line = json.dumps(summary.to_dict(), ensure_ascii=False)
                f.write(json_line + "\n")
            
            self.logger.info(
                f"Persisted run summary: {summary.run_id[:8]} "
                f"({summary.result}, {len(summary.stages_executed)} stages)"
            )
            return True
            
        except Exception as e:
            self.logger.error(f"Failed to persist run summary {summary.run_id[:8]}: {e}", exc_info=True)
            return False
    
    def get_run(self, run_id: str) -> Optional[RunSummary]:
        """
        Get a specific run summary by run_id.
        
        Args:
            run_id: Run ID to look up
            
        Returns:
            RunSummary if found, None otherwise
        """
        if not self.runs_file.exists():
            return None
        
        try:
            with open(self.runs_file, "r", encoding="utf-8") as f:
                for line in f:
                    if not line.strip():
                        continue
                    try:
                        data = json.loads(line)
                        if data.get("run_id") == run_id:
                            return RunSummary(**data)
                    except json.JSONDecodeError:
                        continue
        except Exception as e:
            self.logger.error(f"Failed to read run summary {run_id[:8]}: {e}")
        
        return None
    
    def list_runs(
        self,
        limit: int = 100,
        result_filter: Optional[str] = None,
        since: Optional[datetime] = None
    ) -> List[RunSummary]:
        """
        List run summaries, most recent first.
        
        Args:
            limit: Maximum number of runs to return
            result_filter: Filter by result (success, failed, stopped)
            since: Only return runs started after this datetime
            
        Returns:
            List of RunSummary objects, most recent first
        """
        if not self.runs_file.exists():
            return []
        
        runs: List[RunSummary] = []
        
        try:
            # Read all runs (for small files, this is fine)
            # For large files, we'd need to read from end of file
            with open(self.runs_file, "r", encoding="utf-8") as f:
                for line in f:
                    if not line.strip():
                        continue
                    try:
                        data = json.loads(line)
                        
                        # Apply filters
                        if result_filter and data.get("result") != result_filter:
                            continue
                        
                        if since:
                            started_at_str = data.get("started_at")
                            if started_at_str:
                                try:
                                    started_at = datetime.fromisoformat(started_at_str.replace("Z", "+00:00"))
                                    if started_at < since:
                                        continue
                                except (ValueError, AttributeError):
                                    pass
                        
                        runs.append(RunSummary(**data))
                    except json.JSONDecodeError:
                        continue
            
            # Sort by started_at (most recent first)
            runs.sort(key=lambda r: r.started_at or "", reverse=True)
            
            # Apply limit
            return runs[:limit]
            
        except Exception as e:
            self.logger.error(f"Failed to list runs: {e}", exc_info=True)
            return []
    
    def get_run_count(self, result_filter: Optional[str] = None) -> int:
        """
        Get count of runs, optionally filtered by result.
        
        Args:
            result_filter: Filter by result (success, failed, stopped)
            
        Returns:
            Count of runs
        """
        if not self.runs_file.exists():
            return 0
        
        count = 0
        try:
            with open(self.runs_file, "r", encoding="utf-8") as f:
                for line in f:
                    if not line.strip():
                        continue
                    try:
                        data = json.loads(line)
                        if result_filter and data.get("result") != result_filter:
                            continue
                        count += 1
                    except json.JSONDecodeError:
                        continue
        except Exception as e:
            self.logger.error(f"Failed to count runs: {e}")
        
        return count

