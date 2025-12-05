"""
Pipeline Orchestrator - Coordinates stage execution

Single Responsibility: Orchestrate pipeline stages in sequence
Thin layer that delegates to services
"""

import logging
from typing import Optional
from dataclasses import dataclass
from datetime import datetime

from automation.config import PipelineConfig
from automation.services.event_logger import EventLogger
from automation.pipeline.state import PipelineState, StageResult
from automation.pipeline.stages.translator import TranslatorService, TranslatorResult
from automation.pipeline.stages.analyzer import AnalyzerService, AnalyzerResult
from automation.pipeline.stages.merger import MergerService, MergerResult


@dataclass
class PipelineReport:
    """Final pipeline execution report"""
    run_id: str
    start_time: datetime
    end_time: Optional[datetime] = None
    overall_status: str = "pending"  # success, failure, partial
    stage_results: dict = None
    metrics: dict = None
    
    def __post_init__(self):
        if self.stage_results is None:
            self.stage_results = {}
        if self.metrics is None:
            self.metrics = {}


class PipelineOrchestrator:
    """
    Orchestrates the complete data processing pipeline.
    
    Responsibilities:
    - Determine which stages should run based on file state
    - Call services in order
    - Update stage state
    - Emit structured events
    - Handle partial failures
    - Generate final report
    
    Does NOT:
    - Know subprocess details
    - Parse stdout strings
    - Directly delete files
    """
    
    def __init__(
        self,
        config: PipelineConfig,
        logger: logging.Logger,
        event_logger: EventLogger,
        translator_service: TranslatorService,
        analyzer_service: AnalyzerService,
        merger_service: MergerService
    ):
        self.config = config
        self.logger = logger
        self.event_logger = event_logger
        self.translator_service = translator_service
        self.analyzer_service = analyzer_service
        self.merger_service = merger_service
    
    def run(self, run_id: str) -> PipelineReport:
        """
        Run the complete pipeline.
        
        Args:
            run_id: Unique identifier for this pipeline run
        
        Returns:
            PipelineReport with execution results
        """
        report = PipelineReport(run_id=run_id, start_time=datetime.now())
        
        self.logger.info("=" * 60)
        self.logger.info("DAILY DATA PIPELINE - RUN")
        self.logger.info(f"Run ID: {run_id}")
        self.logger.info("=" * 60)
        
        self.event_logger.emit(run_id, "pipeline", "start", "Pipeline run started")
        
        # Determine which stages to run based on file state
        # Use file manager from translator service (they share the same instance)
        file_manager = self.translator_service.file_manager
        
        raw_files = file_manager.scan_directory(self.config.data_raw, "*.csv")
        processed_files = []
        for pattern in ["*.parquet", "*.csv"]:
            processed_files.extend(
                file_manager.scan_directory(self.config.data_processed, pattern)
            )
        
        run_translator = len(raw_files) > 0
        run_analyzer = len(processed_files) > 0
        
        if not run_translator and not run_analyzer:
            self.logger.warning("No files found - nothing to process")
            self.event_logger.emit(run_id, "pipeline", "log", "No files found - skipping all stages")
            self.event_logger.emit(run_id, "pipeline", "success", "Pipeline complete (no files to process)")
            report.overall_status = "success"
            report.end_time = datetime.now()
            return report
        
        # Stage 1: Translator
        translator_result = None
        if run_translator:
            self.logger.info("=" * 60)
            self.logger.info("STAGE 1: Data Translator")
            self.logger.info("=" * 60)
            
            translator_result = self.translator_service.run(run_id)
            report.stage_results["translator"] = {
                "status": translator_result.status,
                "raw_files_found": translator_result.raw_files_found,
                "files_written": translator_result.files_written,
                "duration_seconds": translator_result.duration_seconds,
                "error": translator_result.error_message
            }
            
            # Refresh processed files after translator
            if translator_result.status == "success":
                processed_files = []
                for pattern in ["*.parquet", "*.csv"]:
                    processed_files.extend(
                        file_manager.scan_directory(self.config.data_processed, pattern)
                    )
                run_analyzer = len(processed_files) > 0
        
        # Stage 2: Analyzer
        analyzer_result = None
        if run_analyzer:
            self.logger.info("=" * 60)
            self.logger.info("STAGE 2: Breakout Analyzer")
            self.logger.info("=" * 60)
            
            analyzer_result = self.analyzer_service.run(run_id)
            report.stage_results["analyzer"] = {
                "status": analyzer_result.status,
                "processed_files_found": analyzer_result.processed_files_found,
                "instruments_processed": analyzer_result.instruments_processed,
                "duration_seconds": analyzer_result.duration_seconds,
                "error": analyzer_result.error_message
            }
        else:
            self.logger.warning("Skipping analyzer - no processed files available")
            self.event_logger.emit(run_id, "analyzer", "log", "Skipped: No processed files available")
            report.stage_results["analyzer"] = {"status": "skipped"}
        
        # Stage 3: Data Merger (only if analyzer succeeded)
        merger_result = None
        if analyzer_result and analyzer_result.status == "success":
            self.logger.info("=" * 60)
            self.logger.info("STAGE 3: Data Merger")
            self.logger.info("=" * 60)
            
            merger_result = self.merger_service.run(run_id)
            report.stage_results["merger"] = {
                "status": merger_result.status,
                "duration_seconds": merger_result.duration_seconds,
                "error": merger_result.error_message
            }
        elif analyzer_result:
            self.logger.warning("Skipping merger - analyzer did not succeed")
            self.event_logger.emit(run_id, "merger", "log", "Skipped: Analyzer did not succeed")
            report.stage_results["merger"] = {"status": "skipped"}
        
        # Determine overall status
        stage_statuses = [r.get("status") for r in report.stage_results.values()]
        if all(s == "success" for s in stage_statuses if s != "skipped"):
            report.overall_status = "success"
        elif any(s == "success" for s in stage_statuses):
            report.overall_status = "partial"
        else:
            report.overall_status = "failure"
        
        report.end_time = datetime.now()
        report.metrics = {
            "total_duration_seconds": (report.end_time - report.start_time).total_seconds(),
            "stages_run": len([r for r in report.stage_results.values() if r.get("status") != "skipped"])
        }
        
        self.logger.info("=" * 60)
        self.logger.info("PIPELINE COMPLETE")
        self.logger.info(f"Overall status: {report.overall_status}")
        self.logger.info("=" * 60)
        
        self.event_logger.emit(run_id, "pipeline", "success" if report.overall_status == "success" else "failure",
            f"Pipeline complete - {report.overall_status}")
        
        return report

