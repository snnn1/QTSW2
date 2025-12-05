"""
Audit Reporting - Generate and persist pipeline execution reports

Single Responsibility: Serialize pipeline reports to disk
"""

import json
from pathlib import Path
from datetime import datetime
from typing import Dict

from automation.pipeline.orchestrator import PipelineReport
from automation.config import PipelineConfig


class AuditReporter:
    """
    Generates and persists audit reports of pipeline execution.
    
    Responsibilities:
    - Take final pipeline report
    - Persist JSON report to disk
    - Emit summary event (optional)
    
    Does NOT:
    - Recompute state
    - Make decisions
    - Just serializes already-collected outcomes
    """
    
    def __init__(self, config: PipelineConfig):
        self.config = config
    
    def generate_report(self, report: PipelineReport) -> Path:
        """
        Generate and save audit report.
        
        Args:
            report: Pipeline execution report
        
        Returns:
            Path to saved report file
        """
        # Create report file
        timestamp = report.start_time.strftime("%Y%m%d_%H%M%S")
        report_file = self.config.logs_dir / f"pipeline_report_{timestamp}_{report.run_id[:8]}.json"
        
        # Serialize report
        report_data = {
            "run_id": report.run_id,
            "start_time": report.start_time.isoformat(),
            "end_time": report.end_time.isoformat() if report.end_time else None,
            "overall_status": report.overall_status,
            "stage_results": report.stage_results,
            "metrics": report.metrics
        }
        
        # Write to file
        try:
            with open(report_file, "w", encoding="utf-8") as f:
                json.dump(report_data, f, indent=2)
            return report_file
        except Exception as e:
            # Don't fail pipeline if audit report fails
            print(f"Warning: Failed to write audit report: {e}")
            return report_file



