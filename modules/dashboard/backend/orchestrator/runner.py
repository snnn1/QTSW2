"""
Pipeline Runner - Stage runners with retry logic
"""

import asyncio
import logging
import os
import sys
import time
from pathlib import Path
from typing import Optional
from datetime import datetime

from .state import PipelineStage, PipelineRunState, RunContext
from .config import OrchestratorConfig, StageConfig
from .events import EventBus
from .event_logger_with_bus import EventLoggerWithBus

# Import existing pipeline services
# Add parent directories to path to import automation package
import sys
qtsw2_root = Path(__file__).parent.parent.parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

# Import automation modules - ensure project root is in path
def _import_pipeline_services():
    """Import pipeline stage services and dependencies with fallback for different import contexts."""
    # Ensure project root is in path
    qtsw2_root = Path(__file__).parent.parent.parent.parent
    if str(qtsw2_root) not in sys.path:
        sys.path.insert(0, str(qtsw2_root))
    
    # Import all required modules
    from automation.config import PipelineConfig
    from automation.logging_setup import create_logger
    from automation.services.process_supervisor import ProcessSupervisor
    from automation.services.file_manager import FileManager
    from automation.pipeline.stages.translator import TranslatorService
    from automation.pipeline.stages.analyzer import AnalyzerService
    from automation.pipeline.stages.merger import MergerService
    
    return (
        PipelineConfig,
        create_logger,
        ProcessSupervisor,
        FileManager,
        TranslatorService,
        AnalyzerService,
        MergerService,
    )

# Import using helper function
(
    PipelineConfig,
    create_logger,
    ProcessSupervisor,
    FileManager,
    TranslatorService,
    AnalyzerService,
    MergerService,
) = _import_pipeline_services()


class PipelineRunner:
    """
    Runs pipeline stages with retry logic and validation.
    """
    
    def __init__(
        self,
        config: OrchestratorConfig,
        event_bus: EventBus,
        state_manager,
        logger: logging.Logger
    ):
        self.config = config
        self.event_bus = event_bus
        self.state_manager = state_manager
        self.logger = logger
        
        # Create automation pipeline config
        self.pipeline_config = PipelineConfig.from_environment()
        
        # Create services (will be initialized per run)
        self.translator_service: Optional[TranslatorService] = None
        self.analyzer_service: Optional[AnalyzerService] = None
        self.merger_service: Optional[MergerService] = None
    
    async def _initialize_services(self, run_id: str):
        """Initialize pipeline services for a run"""
        # Setup logging
        log_file = self.pipeline_config.logs_dir / f"pipeline_{datetime.now().strftime('%Y%m%d_%H%M%S')}.log"
        service_logger = create_logger("PipelineRunner", log_file, level=logging.INFO)
        
        # Setup event logger (writes to same file as event bus AND publishes to EventBus for real-time updates)
        # Capture event loop reference for cross-thread publishing (services run in asyncio.to_thread)
        event_log_file = self.config.event_logs_dir / f"pipeline_{run_id}.jsonl"
        # Get the running event loop (we're in async context via run_pipeline)
        event_loop = asyncio.get_running_loop()
        event_logger = EventLoggerWithBus(event_log_file, self.event_bus, event_loop=event_loop, logger=service_logger)
        
        # Create services
        process_supervisor = ProcessSupervisor(
            service_logger,
            timeout_seconds=self.pipeline_config.translator_timeout
        )
        file_manager = FileManager(service_logger, lock_timeout=300)
        
        self.translator_service = TranslatorService(
            self.pipeline_config, service_logger, process_supervisor, file_manager, event_logger
        )
        
        self.analyzer_service = AnalyzerService(
            self.pipeline_config, service_logger, process_supervisor, file_manager, event_logger
        )
        
        self.merger_service = MergerService(
            self.pipeline_config, service_logger, process_supervisor, event_logger
        )
    
    async def run_pipeline(self, run_ctx: RunContext) -> RunContext:
        """
        Run the complete pipeline with all stages.
        
        Args:
            run_ctx: Run context
        
        Returns:
            Updated run context
        """
        await self._initialize_services(run_ctx.run_id)
        
        # Get ordered stages
        stages = [
            (PipelineStage.TRANSLATOR, self.config.stages["translator"]),
            (PipelineStage.ANALYZER, self.config.stages["analyzer"]),
            (PipelineStage.MERGER, self.config.stages["merger"]),
        ]
        
        for stage, stage_config in stages:
            # Track stage execution
            run_ctx.stages_executed.append(stage.value)
            
            # Run stage with retry (handles state transitions internally)
            success = await self._run_stage_with_retry(stage, stage_config, run_ctx)
            
            if not success:
                # Track failed stage
                run_ctx.stages_failed.append(stage.value)
                # Permanent failure
                await self.state_manager.transition(
                    PipelineRunState.FAILED,
                    error=f"Stage {stage.value} failed after {stage_config.max_retries} retries"
                )
                return run_ctx
            
            # Stage succeeded, continue to next
        
        # All stages succeeded
        await self.state_manager.transition(PipelineRunState.SUCCESS)
        return run_ctx
    
    async def _run_stage_with_retry(
        self,
        stage: PipelineStage,
        stage_config: StageConfig,
        run_ctx: RunContext
    ) -> bool:
        """
        Run a stage with retry logic.
        
        Event Emission Strategy:
        - Services emit operational events (start, success, failure, log, metric, error) via EventLoggerWithBus
        - Runner emits state transitions (via state_manager.transition()) and validation failures only
        - This prevents duplicate events and ensures clear separation of concerns
        
        Args:
            stage: Stage to run
            stage_config: Stage configuration
            run_ctx: Run context
        
        Returns:
            True if stage succeeded, False if permanently failed
        """
        for attempt in range(stage_config.max_retries + 1):
            if attempt > 0:
                # Retry - transition to RETRYING first, then back to RUNNING
                await self.state_manager.transition(
                    PipelineRunState.RETRYING,
                    stage=stage,
                    metadata={"attempt": attempt, "max_retries": stage_config.max_retries}
                )
                
                # Exponential backoff
                delay = stage_config.retry_delay_sec * (self.config.retry_backoff_multiplier ** (attempt - 1))
                await asyncio.sleep(delay)
                
                # Transition back to running state
                state_enum = getattr(PipelineRunState, f"RUNNING_{stage.name}")
                await self.state_manager.transition(
                    state_enum,
                    stage=stage
                )
            else:
                # First attempt - transition to running state
                state_enum = getattr(PipelineRunState, f"RUNNING_{stage.name}")
                await self.state_manager.transition(
                    state_enum,
                    stage=stage
                )
            
            # Run stage
            # Note: Services emit operational events (start, success, failure) via EventLoggerWithBus.
            # Runner only emits state transitions (via state_manager.transition()) and validation failures.
            success = await self._run_stage(stage, run_ctx)
            
            if success:
                # Validate output
                if await self._validate_stage_output(stage, run_ctx):
                    # Service already emitted success event - no duplicate needed
                    return True
                else:
                    # Validation failed (runner-level validation, not service-level)
                    # Service should emit error event for validation failures
                    # Runner just logs - no duplicate event publish needed
                    self.logger.warning(f"Stage {stage.value} validation failed (attempt {attempt + 1})")
            else:
                # Stage execution failed
                # Service already emitted failure event - no duplicate needed
                pass
        
        # All retries exhausted
        return False
    
    async def _run_stage(self, stage: PipelineStage, run_ctx: RunContext) -> bool:
        """
        Run a single stage (no retry).
        Wraps synchronous service.run() calls in asyncio.to_thread() to avoid blocking.
        Includes timeout protection to prevent indefinite hangs.
        
        Args:
            stage: Stage to run
            run_ctx: Run context
        
        Returns:
            True if stage succeeded, False otherwise
        """
        # Get stage config for timeout (before try block so it's available in except)
        stage_config = self.config.stages[stage.value]
        timeout_sec = stage_config.timeout_sec
        
        try:
            # Run synchronous service methods in thread pool to avoid blocking event loop
            # Add timeout to prevent indefinite hangs
            if stage == PipelineStage.TRANSLATOR:
                result = await asyncio.wait_for(
                    asyncio.to_thread(self.translator_service.run, run_ctx.run_id),
                    timeout=timeout_sec
                )
                # Translator can return "success", "skipped", or "failure"
                # "skipped" is acceptable if no raw files found (already translated or no new data)
                # Only "failure" should stop the pipeline
                if result.status == "skipped":
                    # Check if there are already translated files (validation will check this)
                    # If skipped but files exist, that's fine - proceed to analyzer
                    self.logger.info(f"Translator skipped (no raw files or already translated), proceeding to validation")
                return result.status in ("success", "skipped")  # Both are acceptable
            
            elif stage == PipelineStage.ANALYZER:
                result = await asyncio.wait_for(
                    asyncio.to_thread(self.analyzer_service.run, run_ctx.run_id),
                    timeout=timeout_sec
                )
                # Analyzer can return "success" or "skipped" (if no input files)
                return result.status in ("success", "skipped")
            
            elif stage == PipelineStage.MERGER:
                result = await asyncio.wait_for(
                    asyncio.to_thread(self.merger_service.run, run_ctx.run_id),
                    timeout=timeout_sec
                )
                return result.status == "success"
            
            else:
                self.logger.error(f"Unknown stage: {stage}")
                return False
        except asyncio.TimeoutError:
            self.logger.error(f"Stage {stage.value} timed out after {timeout_sec} seconds")
            # Service should emit timeout error event - runner just logs
            # No duplicate event publish needed
            return False
        
        except Exception as e:
            self.logger.error(f"Stage {stage.value} exception: {e}", exc_info=True)
            # Service should emit error event for exceptions - runner just logs
            # No duplicate event publish needed
            return False
    
    async def _validate_stage_output(self, stage: PipelineStage, run_ctx: RunContext) -> bool:
        """
        Validate stage output.
        
        Args:
            stage: Stage that just completed
            run_ctx: Run context
        
        Returns:
            True if output is valid, False otherwise
        """
        try:
            if stage == PipelineStage.TRANSLATOR:
                # Check for translated files (translator outputs to data_translated)
                # Translator contract: {translated_root}/{instrument}/1m/YYYY/MM/{instrument}_1m_{YYYY-MM-DD}.parquet
                # Use recursive glob to find parquet files in subdirectories
                
                # TRIPWIRE: PipelineConfig.data_translated is now mandatory
                # This fallback should never execute - if it does, it indicates a configuration regression
                # TODO: Remove fallback after confirming it never triggers in production
                translated_root = getattr(self.pipeline_config, 'data_translated', None)
                
                if translated_root is None:
                    # FALLBACK TRIPWIRE: This should NEVER happen now that PipelineConfig.data_translated is mandatory
                    # If this triggers, it indicates a configuration regression or PipelineConfig initialization failure
                    translated_root = qtsw2_root / "data" / "translated"
                    
                    # Emit system-level warning - this is a tripwire, not a crutch
                    await self.event_bus.publish({
                        "run_id": "__system__",
                        "stage": "system",
                        "event": "warning",
                        "timestamp": datetime.now().isoformat(),
                        "msg": "PipelineConfig missing data_translated; fallback path used. This indicates a configuration regression.",
                        "data": {
                            "fallback_path": str(translated_root),
                            "stage": "translator_validation",
                            "severity": "high",
                            "note": "This fallback should never be used. PipelineConfig.data_translated is mandatory. Investigate configuration initialization."
                        }
                    })
                    self.logger.error(
                        f"CONFIGURATION REGRESSION: PipelineConfig missing data_translated attribute. "
                        f"This should never happen. Using fallback path: {translated_root}. "
                        f"Investigate PipelineConfig initialization - data_translated is mandatory."
                    )
                
                try:
                    translated_files = list(Path(translated_root).rglob("*.parquet"))
                except (AttributeError, TypeError) as e:
                    # If path construction fails, log error and return False
                    self.logger.error(f"Failed to construct translated_root path: {e}")
                    # Service should emit error event for validation failures - runner just logs
                    # No duplicate event publish needed
                    return False
                
                return len(translated_files) > 0
            
            elif stage == PipelineStage.ANALYZER:
                # Check for analyzer output specific to this run_id
                # AnalyzerService writes a success marker file: .success_{run_id}.marker
                # This ensures validation checks THIS run's output, not previous runs
                marker_file = self.pipeline_config.analyzer_runs / f".success_{run_ctx.run_id}.marker"
                if marker_file.exists():
                    return True
                
                # Fallback: if marker file doesn't exist, check if analyzer_runs has any files
                # This handles legacy runs that didn't write markers
                # But log a warning since this violates idempotency contract
                analyzer_runs = list(self.pipeline_config.analyzer_runs.glob("*"))
                if len(analyzer_runs) > 0:
                    self.logger.warning(
                        f"Analyzer validation: No marker file for run_id={run_ctx.run_id}, "
                        f"but files exist in analyzer_runs. This violates idempotency contract. "
                        f"Files may be from a previous run."
                    )
                    return True
                
                return False
            
            elif stage == PipelineStage.MERGER:
                # Check for merger completion marker specific to this run_id
                # MergerService writes a completion marker file: .merge_complete_{run_id}.marker
                # This ensures validation checks THIS run completed, not just that merger ran previously
                marker_file = self.pipeline_config.analyzer_runs / f".merge_complete_{run_ctx.run_id}.marker"
                if marker_file.exists():
                    return True
                
                # Fallback: Check if merger processed log exists and was recently updated
                # Merger writes to data/merger_processed.json, but this is time-agnostic
                # So we only use it as a weak fallback with a warning
                merger_log = self.pipeline_config.qtsw2_root / "data" / "merger_processed.json"
                if merger_log.exists():
                    log_mtime = os.path.getmtime(merger_log)
                    # Check if log was modified within last 5 minutes (weak heuristic)
                    if time.time() - log_mtime < 300:
                        self.logger.warning(
                            f"Merger validation: No marker file for run_id={run_ctx.run_id}, "
                            f"but merger_processed.json was recently updated. This violates idempotency contract. "
                            f"Update may be from a previous run."
                        )
                        return True
                
                # No marker file and no recent log update - validation fails
                self.logger.warning(
                    f"Merger validation failed: No marker file for run_id={run_ctx.run_id} "
                    f"and no recent merger_processed.json update. Merger may not have completed successfully."
                )
                return False
            
            return True
        
        except Exception as e:
            self.logger.error(f"Validation error for {stage.value}: {e}")
            return False

