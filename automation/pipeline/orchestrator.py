"""
Pipeline Orchestrator - Legacy module for backward compatibility

NOTE: The actual orchestrator is in modules/orchestrator/
This module exists only to provide PipelineReport for backward compatibility.
"""

from dataclasses import dataclass
from datetime import datetime
from typing import Dict, Optional, Any


@dataclass
class PipelineReport:
    """
    Pipeline execution report for audit purposes.
    
    This is a legacy class kept for backward compatibility with audit.py
    """
    run_id: str
    start_time: datetime
    end_time: Optional[datetime] = None
    overall_status: str = "unknown"  # success, failure, partial
    stage_results: Dict[str, Any] = None  # Results for each stage
    metrics: Dict[str, Any] = None  # Overall metrics
    
    def __post_init__(self):
        if self.stage_results is None:
            self.stage_results = {}
        if self.metrics is None:
            self.metrics = {}
