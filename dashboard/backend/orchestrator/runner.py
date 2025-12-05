"""
Pipeline Runner - Stage runners with retry logic
"""

import asyncio
import logging
import sys
from pathlib import Path
from typing import Optional
from datetime import datetime
import time

from .state import PipelineStage, PipelineRunState, RunContext
from .config import OrchestratorConfig, StageConfig
from .events import EventBus

# Import existing pipeline services
# Add parent directories to path to import automation package
import sys
qtsw2_root = Path(__file__).parent.parent.parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

# Import automation modules - ensure project root is in path
try:
    from automation.config import PipelineConfig
    from automation.logging_setup import create_logger
    from automation.services.event_logger import EventLogger
    from automation.services.process_supervisor import ProcessSupervisor
    from automation.services.file_manager import FileManager
    from automation.pipeline.stages.translator import TranslatorService
    from automation.pipeline.stages.analyzer import AnalyzerService
    from automation.pipeline.stages.merger import MergerService
except ImportError:
    # If import fails, ensure project root is in path and try again
    qtsw2_root = Path(__file__).parent.parent.parent.parent
    if str(qtsw2_root) not in sys.path:
        sys.path.insert(0, str(qtsw2_root))
    from automation.config import PipelineConfig
    from automation.logging_setup import create_logger
    from automation.services.event_logger import EventLogger
    from automation.services.process_supervisor import ProcessSupervisor
    from automation.services.file_manager import FileManager
    from automation.pipeline.stages.translator import TranslatorService
    from automation.pipeline.stages.analyzer import AnalyzerService
    from automation.pipeline.stages.merger import MergerService


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
    
    def _initialize_services(self, run_id: str):
        """Initialize pipeline services for a run"""
        # Setup logging
        log_file = self.pipeline_config.logs_dir / f"pipeline_{datetime.now().strftime('%Y%m%d_%H%M%S')}.log"
        service_logger = create_logger("PipelineRunner", log_file, level=logging.INFO)
        
        # Setup event logger (writes to same file as event bus)
        event_log_file = self.config.event_logs_dir / f"pipeline_{run_id}.jsonl"
        event_logger = EventLogger(event_log_file, logger=service_logger)
        
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
        self._initialize_services(run_ctx.run_id)
        
        # Get ordered stages
        stages = [
            (PipelineStage.TRANSLATOR, self.config.stages["translator"]),
            (PipelineStage.ANALYZER, self.config.stages["analyzer"]),
            (PipelineStage.MERGER, self.config.stages["merger"]),
        ]
        
        for stage, stage_config in stages:
            # Run stage with retry (handles state transitions internally)
            success = await self._run_stage_with_retry(stage, stage_config, run_ctx)
            
            if not success:
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
            start_time = time.time()
            success = await self._run_stage(stage, run_ctx)
            duration = time.time() - start_time
            
            if success:
                # Validate output
                if await self._validate_stage_output(stage, run_ctx):
                    await self.event_bus.publish({
                        "run_id": run_ctx.run_id,
                        "stage": stage.value,
                        "event": "success",
                        "timestamp": datetime.now().isoformat(),
                        "msg": f"Stage {stage.value} completed successfully",
                        "data": {
                            "attempt": attempt + 1,
                            "duration_seconds": duration
                        }
                    })
                    return True
                else:
                    # Validation failed
                    await self.event_bus.publish({
                        "run_id": run_ctx.run_id,
                        "stage": stage.value,
                        "event": "error",
                        "timestamp": datetime.now().isoformat(),
                        "msg": f"Stage {stage.value} validation failed",
                        "data": {"attempt": attempt + 1}
                    })
            else:
                # Stage execution failed
                await self.event_bus.publish({
                    "run_id": run_ctx.run_id,
                    "stage": stage.value,
                    "event": "error",
                    "timestamp": datetime.now().isoformat(),
                    "msg": f"Stage {stage.value} execution failed",
                    "data": {"attempt": attempt + 1}
                })
        
        # All retries exhausted
        return False
    
    async def _run_stage(self, stage: PipelineStage, run_ctx: RunContext) -> bool:
        """
        Run a single stage (no retry).
        
        Args:
            stage: Stage to run
            run_ctx: Run context
        
        Returns:
            True if stage succeeded, False otherwise
        """
        try:
            if stage == PipelineStage.TRANSLATOR:
                result = self.translator_service.run(run_ctx.run_id)
                return result.status == "success"
            
            elif stage == PipelineStage.ANALYZER:
                result = self.analyzer_service.run(run_ctx.run_id)
                return result.status == "success"
            
            elif stage == PipelineStage.MERGER:
                result = self.merger_service.run(run_ctx.run_id)
                return result.status == "success"
            
            else:
                self.logger.error(f"Unknown stage: {stage}")
                return False
        
        except Exception as e:
            self.logger.error(f"Stage {stage.value} exception: {e}", exc_info=True)
            await self.event_bus.publish({
                "run_id": run_ctx.run_id,
                "stage": stage.value,
                "event": "error",
                "timestamp": datetime.now().isoformat(),
                "msg": f"Stage {stage.value} exception: {str(e)}",
                "data": {"exception": str(e)}
            })
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
                # Check for processed files
                processed_files = list(self.pipeline_config.data_processed.glob("*.parquet"))
                return len(processed_files) > 0
            
            elif stage == PipelineStage.ANALYZER:
                # Check for analyzer output
                analyzer_runs = list(self.pipeline_config.analyzer_runs.glob("*"))
                return len(analyzer_runs) > 0
            
            elif stage == PipelineStage.MERGER:
                # Merger doesn't produce files, just merges data
                return True
            
            return True
        
        except Exception as e:
            self.logger.error(f"Validation error for {stage.value}: {e}")
            return False

