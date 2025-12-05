"""
Pipeline Runner - Simple entry point for running the pipeline once

Single Responsibility: Run the pipeline once
Can be called by OS scheduler (Windows Task Scheduler, cron)
"""

import sys
import uuid
import logging
from pathlib import Path
from datetime import datetime

from automation.config import PipelineConfig
from automation.logging_setup import create_logger
from automation.services.event_logger import EventLogger
from automation.services.process_supervisor import ProcessSupervisor
from automation.services.file_manager import FileManager
from automation.pipeline.stages.translator import TranslatorService
from automation.pipeline.stages.analyzer import AnalyzerService
from automation.pipeline.stages.merger import MergerService
from automation.pipeline.orchestrator import PipelineOrchestrator
from automation.audit import AuditReporter


def run_pipeline_once(config: PipelineConfig = None) -> None:
    """
    Run the pipeline once.
    
    This is the main entry point that can be called by:
    - OS scheduler (Windows Task Scheduler, cron)
    - Manual execution
    - Test scripts
    
    Args:
        config: Optional PipelineConfig (uses default if not provided)
    """
    if config is None:
        config = PipelineConfig.from_environment()
    
    # Generate run ID
    run_id = str(uuid.uuid4())
    
    # Setup logging
    log_file = config.logs_dir / f"pipeline_{datetime.now().strftime('%Y%m%d_%H%M%S')}.log"
    logger = create_logger("PipelineRunner", log_file, level=logging.INFO)
    
    # Setup event logging
    # Use same naming convention as dashboard expects: pipeline_{run_id}.jsonl
    event_log_file = config.event_logs_dir / f"pipeline_{run_id}.jsonl"
    event_logger = EventLogger(event_log_file, logger=logger)
    
    # Create services
    process_supervisor = ProcessSupervisor(logger, timeout_seconds=config.translator_timeout)
    file_manager = FileManager(logger, lock_timeout=300)
    
    translator_service = TranslatorService(
        config, logger, process_supervisor, file_manager, event_logger
    )
    
    analyzer_service = AnalyzerService(
        config, logger, process_supervisor, file_manager, event_logger
    )
    
    merger_service = MergerService(
        config, logger, process_supervisor, event_logger
    )
    
    # Create orchestrator
    orchestrator = PipelineOrchestrator(
        config, logger, event_logger,
        translator_service, analyzer_service, merger_service
    )
    
    # Run pipeline
    try:
        report = orchestrator.run(run_id)
        
        # Generate audit report
        audit_reporter = AuditReporter(config)
        report_file = audit_reporter.generate_report(report)
        logger.info(f"Audit report saved: {report_file}")
        
        # Exit with appropriate code
        if report.overall_status == "success":
            sys.exit(0)
        elif report.overall_status == "partial":
            sys.exit(1)  # Partial success
        else:
            sys.exit(2)  # Failure
    
    except Exception as e:
        logger.error(f"Pipeline execution failed: {e}", exc_info=True)
        event_logger.emit(run_id, "pipeline", "failure", f"Pipeline exception: {str(e)}")
        sys.exit(3)  # Exception


if __name__ == "__main__":
    run_pipeline_once()

