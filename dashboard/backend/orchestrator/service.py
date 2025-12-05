"""
Pipeline Orchestrator Service - Main facade
"""

import asyncio
import logging
import uuid
from pathlib import Path
from typing import Optional, Dict, Any
from datetime import datetime

from .config import OrchestratorConfig
from .state import PipelineStateManager, PipelineRunState, PipelineStage, RunContext
from .events import EventBus
from .locks import LockManager
from .runner import PipelineRunner
from .scheduler import Scheduler
from .watchdog import Watchdog


class PipelineOrchestrator:
    """
    Main orchestrator service that coordinates all components.
    """
    
    def __init__(
        self,
        config: OrchestratorConfig,
        schedule_config_path: Path,
        logger: Optional[logging.Logger] = None
    ):
        self.config = config
        self.schedule_config_path = schedule_config_path
        self.logger = logger or logging.getLogger(__name__)
        
        # Initialize components
        self.event_bus = EventBus(
            event_logs_dir=config.event_logs_dir,
            buffer_size=config.event_buffer_size,
            logger=self.logger
        )
        
        self.state_manager = PipelineStateManager(
            event_bus=self.event_bus,
            logger=self.logger
        )
        
        self.lock_manager = LockManager(
            lock_dir=config.lock_dir,
            lock_timeout_sec=config.lock_timeout_sec,
            heartbeat_timeout_sec=config.heartbeat_timeout_sec,
            logger=self.logger
        )
        
        self.runner = PipelineRunner(
            config=config,
            event_bus=self.event_bus,
            state_manager=self.state_manager,
            logger=self.logger
        )
        
        self.scheduler = Scheduler(
            config_path=schedule_config_path,
            orchestrator=self,
            logger=self.logger
        )
        
        self.watchdog = Watchdog(
            orchestrator=self,
            event_bus=self.event_bus,
            state_manager=self.state_manager,
            config=config,
            logger=self.logger
        )
        
        self._running = False
        self._heartbeat_task: Optional[asyncio.Task] = None
    
    async def start(self):
        """Start orchestrator (scheduler and watchdog)"""
        if self._running:
            return
        
        self._running = True
        
        # Start scheduler and watchdog
        await self.scheduler.start()
        await self.watchdog.start()
        
        # Start heartbeat task
        self._heartbeat_task = asyncio.create_task(self._heartbeat_loop())
        
        self.logger.info("Pipeline Orchestrator started")
    
    async def stop(self):
        """Stop orchestrator"""
        self._running = False
        
        # Stop scheduler and watchdog
        await self.scheduler.stop()
        await self.watchdog.stop()
        
        # Stop heartbeat
        if self._heartbeat_task:
            self._heartbeat_task.cancel()
            try:
                await self._heartbeat_task
            except asyncio.CancelledError:
                pass
        
        self.logger.info("Pipeline Orchestrator stopped")
    
    async def _heartbeat_loop(self):
        """Periodic heartbeat for active runs"""
        while self._running:
            try:
                await asyncio.sleep(30)  # Every 30 seconds
                
                status = await self.state_manager.get_state()
                if status and status.state not in {
                    PipelineRunState.IDLE,
                    PipelineRunState.SUCCESS,
                    PipelineRunState.FAILED,
                    PipelineRunState.STOPPED,
                }:
                    # Update lock heartbeat
                    await self.lock_manager.heartbeat(status.run_id)
            
            except asyncio.CancelledError:
                break
            except Exception as e:
                self.logger.error(f"Heartbeat loop error: {e}")
    
    async def start_pipeline(self, manual: bool = False) -> RunContext:
        """
        Start a new pipeline run.
        
        Args:
            manual: True if manually triggered, False if scheduled
        
        Returns:
            RunContext for the new run
        
        Raises:
            ValueError: If pipeline is already running
        """
        # Check if already running
        status = await self.state_manager.get_state()
        if status and status.state not in {
            PipelineRunState.IDLE,
            PipelineRunState.SUCCESS,
            PipelineRunState.FAILED,
            PipelineRunState.STOPPED,
        }:
            raise ValueError(f"Pipeline already running: {status.state.value}")
        
        # Acquire lock
        run_id = str(uuid.uuid4())
        if not await self.lock_manager.acquire(run_id):
            raise ValueError("Failed to acquire lock (pipeline may already be running)")
        
        try:
            # Create run context
            run_ctx = await self.state_manager.create_run(
                run_id=run_id,
                metadata={
                    "manual": manual,
                    "triggered_at": datetime.now().isoformat()
                }
            )
            
            # Transition to starting
            await self.state_manager.transition(
                PipelineRunState.STARTING,
                metadata={"manual": manual}
            )
            
            # Emit start event
            await self.event_bus.publish({
                "run_id": run_id,
                "stage": "pipeline",
                "event": "start",
                "timestamp": datetime.now().isoformat(),
                "msg": "Pipeline run started",
                "data": {"manual": manual}
            })
            
            # Run pipeline in background (state transitions handled by runner)
            asyncio.create_task(self._run_pipeline_background(run_ctx))
            
            return run_ctx
        
        except Exception as e:
            # Release lock on error
            await self.lock_manager.release(run_id)
            raise
    
    async def _run_pipeline_background(self, run_ctx: RunContext):
        """Run pipeline in background task"""
        try:
            # Run pipeline
            await self.runner.run_pipeline(run_ctx)
        except Exception as e:
            self.logger.error(f"Pipeline run error: {e}", exc_info=True)
            await self.state_manager.transition(
                PipelineRunState.FAILED,
                error=str(e)
            )
        finally:
            # Release lock
            await self.lock_manager.release(run_ctx.run_id)
    
    async def stop_pipeline(self) -> RunContext:
        """
        Stop current pipeline run.
        
        Returns:
            Updated RunContext
        """
        status = await self.state_manager.get_state()
        if not status:
            raise ValueError("No active pipeline run")
        
        await self.state_manager.transition(PipelineRunState.STOPPED)
        
        # Release lock
        await self.lock_manager.release(status.run_id)
        
        await self.event_bus.publish({
            "run_id": status.run_id,
            "stage": "pipeline",
            "event": "log",
            "timestamp": datetime.now().isoformat(),
            "msg": "Pipeline run stopped"
        })
        
        return status
    
    async def get_status(self) -> Optional[RunContext]:
        """
        Get current pipeline status.
        
        Returns:
            Current RunContext or None if no active run
        """
        return await self.state_manager.get_state()
    
    async def get_snapshot(self) -> Dict[str, Any]:
        """
        Get current snapshot (status + recent events + metrics).
        
        Returns:
            Dictionary with status, events, and metrics
        """
        status = await self.state_manager.get_state()
        
        # Get recent events
        recent_events = self.event_bus.get_recent_events(limit=100)
        
        # If there's an active run, get events for that run
        run_events = []
        if status:
            run_events = self.event_bus.get_events_for_run(status.run_id, limit=100)
        
        return {
            "status": status.to_dict() if status else None,
            "recent_events": recent_events,
            "run_events": run_events,
            "lock_info": await self.lock_manager.get_lock_info(),
            "next_scheduled_run": self.scheduler.get_next_run_time().isoformat() if self.scheduler.get_next_run_time() else None,
        }
    
    async def run_single_stage(self, stage: PipelineStage) -> RunContext:
        """
        Run a single stage (optional feature).
        
        Args:
            stage: Stage to run
        
        Returns:
            RunContext
        """
        # Check if pipeline can run
        status = await self.state_manager.get_state()
        if status and status.state not in {
            PipelineRunState.IDLE,
            PipelineRunState.SUCCESS,
            PipelineRunState.FAILED,
            PipelineRunState.STOPPED,
        }:
            raise ValueError(f"Pipeline already running: {status.run_id}")
        
        # Create run context
        run_id = str(uuid.uuid4())
        run_ctx = await self.state_manager.create_run(
            run_id=run_id,
            metadata={"single_stage": stage.value}
        )
        
        # Acquire lock
        if not await self.lock_manager.acquire(run_id):
            raise ValueError("Failed to acquire lock")
        
        try:
            # Transition to running stage
            state_enum = getattr(PipelineRunState, f"RUNNING_{stage.name}")
            await self.state_manager.transition(
                state_enum,
                stage=stage
            )
            
            # Run stage
            stage_config = self.config.stages[stage.value]
            success = await self.runner._run_stage_with_retry(stage, stage_config, run_ctx)
            
            if success:
                await self.state_manager.transition(PipelineRunState.SUCCESS)
            else:
                await self.state_manager.transition(
                    PipelineRunState.FAILED,
                    error=f"Stage {stage.value} failed"
                )
            
            return run_ctx
        
        finally:
            await self.lock_manager.release(run_id)

