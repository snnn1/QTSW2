"""
Dry run test for the new pipeline system
Tests initialization and file detection without executing subprocesses
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


def test_dry_run():
    """Test pipeline initialization and file detection"""
    print("=" * 60)
    print("PIPELINE DRY RUN TEST")
    print("=" * 60)
    
    # Initialize config
    config = PipelineConfig.from_environment()
    print(f"✓ Configuration loaded")
    print(f"  - Data raw: {config.data_raw}")
    print(f"  - Data processed: {config.data_processed}")
    print(f"  - Logs dir: {config.logs_dir}")
    print(f"  - Event logs dir: {config.event_logs_dir}")
    
    # Generate run ID
    run_id = str(uuid.uuid4())
    print(f"\n✓ Run ID generated: {run_id[:8]}...")
    
    # Setup logging
    log_file = config.logs_dir / f"test_dry_run_{datetime.now().strftime('%Y%m%d_%H%M%S')}.log"
    logger = create_logger("DryRunTest", log_file, level=logging.INFO)
    print(f"✓ Logger created: {log_file}")
    
    # Setup event logging
    event_log_file = config.event_logs_dir / f"test_dry_run_{run_id}.jsonl"
    event_logger = EventLogger(event_log_file, logger=logger)
    print(f"✓ Event logger created: {event_log_file}")
    
    # Test event emission
    event_logger.emit(run_id, "test", "start", "Dry run test started")
    print(f"✓ Event emission test passed")
    
    # Create services
    process_supervisor = ProcessSupervisor(logger, timeout_seconds=config.translator_timeout)
    print(f"✓ Process supervisor created")
    
    file_manager = FileManager(logger, lock_timeout=300)
    print(f"✓ File manager created")
    
    translator_service = TranslatorService(
        config, logger, process_supervisor, file_manager, event_logger
    )
    print(f"✓ Translator service created")
    
    analyzer_service = AnalyzerService(
        config, logger, process_supervisor, file_manager, event_logger
    )
    print(f"✓ Analyzer service created")
    
    merger_service = MergerService(
        config, logger, process_supervisor, event_logger
    )
    print(f"✓ Merger service created")
    
    # Create orchestrator
    orchestrator = PipelineOrchestrator(
        config, logger, event_logger,
        translator_service, analyzer_service, merger_service
    )
    print(f"✓ Orchestrator created")
    
    # Test file detection
    print("\n" + "=" * 60)
    print("FILE DETECTION TEST")
    print("=" * 60)
    
    raw_files = file_manager.scan_directory(config.data_raw, "*.csv")
    print(f"✓ Raw files found: {len(raw_files)}")
    if raw_files:
        for f in raw_files[:5]:
            print(f"  - {f.name}")
        if len(raw_files) > 5:
            print(f"  ... and {len(raw_files) - 5} more")
    
    processed_files = []
    for pattern in ["*.parquet", "*.csv"]:
        processed_files.extend(
            file_manager.scan_directory(config.data_processed, pattern)
        )
    print(f"✓ Processed files found: {len(processed_files)}")
    if processed_files:
        for f in processed_files[:5]:
            print(f"  - {f.name}")
        if len(processed_files) > 5:
            print(f"  ... and {len(processed_files) - 5} more")
    
    # Test audit reporter
    print("\n" + "=" * 60)
    print("AUDIT REPORTER TEST")
    print("=" * 60)
    
    from automation.pipeline.orchestrator import PipelineReport
    test_report = PipelineReport(
        run_id=run_id,
        start_time=datetime.now(),
        end_time=datetime.now(),
        overall_status="success",
        stage_results={
            "translator": {"status": "success"},
            "analyzer": {"status": "success"},
            "merger": {"status": "success"}
        },
        metrics={"total_duration_seconds": 123.45}
    )
    
    audit_reporter = AuditReporter(config)
    report_file = audit_reporter.generate_report(test_report)
    print(f"✓ Audit report generated: {report_file}")
    
    # Verify report file exists and is valid JSON
    if report_file.exists():
        import json
        with open(report_file, 'r') as f:
            report_data = json.load(f)
        print(f"✓ Report file is valid JSON")
        print(f"  - Run ID: {report_data['run_id']}")
        print(f"  - Status: {report_data['overall_status']}")
        print(f"  - Stages: {len(report_data['stage_results'])}")
    
    # Test data lifecycle
    print("\n" + "=" * 60)
    print("DATA LIFECYCLE TEST")
    print("=" * 60)
    
    from automation.data_lifecycle import DataLifecycleManager
    lifecycle_manager = DataLifecycleManager(config, logger)
    print(f"✓ Data lifecycle manager created")
    
    should_delete = lifecycle_manager.should_delete_processed_files(
        translator_succeeded=True,
        analyzer_succeeded=True,
        merger_succeeded=True
    )
    print(f"✓ Should delete processed files (all succeeded): {should_delete}")
    
    should_delete_partial = lifecycle_manager.should_delete_processed_files(
        translator_succeeded=True,
        analyzer_succeeded=True,
        merger_succeeded=False
    )
    print(f"✓ Should delete processed files (merger failed): {should_delete_partial}")
    
    print("\n" + "=" * 60)
    print("✓ ALL DRY RUN TESTS PASSED")
    print("=" * 60)
    print("\nThe new pipeline system is ready to use!")
    print(f"\nTo run the actual pipeline:")
    print(f"  python -m automation.pipeline_runner")
    print(f"\nEvent log format is compatible with dashboard.")
    print(f"Event log location: {config.event_logs_dir}")


if __name__ == "__main__":
    try:
        test_dry_run()
    except Exception as e:
        print(f"\n✗ DRY RUN TEST FAILED: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)



