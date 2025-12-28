"""
Pipeline Orchestrator Service - Main facade
"""

import asyncio
import logging
import uuid
import shutil
from pathlib import Path
from typing import Optional, Dict, Any
from datetime import datetime, timedelta, timezone

from .config import OrchestratorConfig
from .state import PipelineStateManager, PipelineRunState, PipelineStage, RunContext
from .events import EventBus
from .locks import LockManager
from .runner import PipelineRunner
from .scheduler import Scheduler
from .watchdog import Watchdog


class PipelineOrchestrator:
    """
    Pipeline Orchestrator - Coordinates pipeline execution and state management.
    
    Execution Authority:
    - Windows Task Scheduler is the execution authority for scheduled runs
    - This orchestrator is advisory only - it coordinates runs, not timing
    - Backend never assumes it controls execution timing
    
    The orchestrator:
    - Manages pipeline state and coordination when runs occur
    - Receives events directly via EventLoggerWithBus → EventBus (live path)
    - Provides manual pipeline triggers via dashboard UI
    - Does NOT schedule or time pipeline executions
    
    Live Event Path:
    - Pipeline → EventLoggerWithBus → EventBus → WebSocket → UI
    - JSONL files are side-effect only (historical storage, not live source)
    
    Scheduled runs originate from Windows Task Scheduler (external system).
    Manual runs originate from dashboard UI requests.
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
            logger=self.logger,
            state_file=config.state_file
        )
        
        self.lock_manager = LockManager(
            lock_dir=config.lock_dir,
            max_runtime_sec=config.lock_timeout_sec,  # Use lock_timeout_sec as max_runtime_sec
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
        self._archive_task: Optional[asyncio.Task] = None
        self._scheduler_health_task: Optional[asyncio.Task] = None
        
        # Background task tracking
        self._active_run_task: Optional[asyncio.Task] = None
    
    async def start(self):
        """Start orchestrator (scheduler and watchdog)"""
        if self._running:
            return
        
        self._running = True
        
        # Start scheduler (non-blocking - doesn't affect readiness)
        await self.scheduler.start()
        self.logger.info("[SUCCESS] Scheduler started")
        
        # Start watchdog (non-blocking - doesn't affect readiness)
        await self.watchdog.start()
        self.logger.info("[SUCCESS] Watchdog started")
        
        # ADDITION 3: Start heartbeat task (emits EventBus heartbeat every 30-60 seconds)
        self._heartbeat_task = asyncio.create_task(self._heartbeat_loop())
        
        # Start scheduler health check task (monitors state but does NOT auto-re-enable)
        self._scheduler_health_task = asyncio.create_task(self._scheduler_health_check_loop())
        
        # Start archiving task (archive old files daily)
        self._archive_task = asyncio.create_task(self._archive_old_files_task())
        
        self.logger.info("Pipeline Orchestrator started and ready")
        self.logger.info("   - Live events: Direct publish via EventLoggerWithBus → EventBus → WebSocket")
        self.logger.info("   - Historical storage: JSONL files (for replay if needed)")
    
    def is_ready(self) -> bool:
        """Check if orchestrator is ready (simplified: instance exists and event bus exists)
        
        Phase-1 always-on: Subsystems (JSONL monitor, watchdog, scheduler) are non-blocking.
        If a subsystem is late, we log a warning and emit an event, but don't block readiness.
        """
        # Simple check: orchestrator instance exists and event bus exists
        return self.event_bus is not None
    
    async def stop(self):
        """Stop orchestrator"""
        self.logger.info("Stopping orchestrator...")
        self._running = False
        
        # Cancel active pipeline task if running
        if self._active_run_task and not self._active_run_task.done():
            self.logger.info("Cancelling active pipeline task...")
            self._active_run_task.cancel()
            try:
                await asyncio.wait_for(self._active_run_task, timeout=5.0)
                self.logger.debug("  [OK] Active pipeline task cancelled")
            except asyncio.CancelledError:
                self.logger.debug("  [OK] Active pipeline task cancelled")
            except asyncio.TimeoutError:
                self.logger.warning("  [WARNING] Active pipeline task cancellation timeout - forcing stop")
            except Exception as e:
                self.logger.error(f"  [ERROR] Error cancelling active pipeline task: {e}")
        
        # Stop all background tasks with timeout
        tasks_to_stop = [
            ("heartbeat", self._heartbeat_task),
            ("scheduler_health", self._scheduler_health_task),
            ("archive", self._archive_task)
        ]
        
        for task_name, task in tasks_to_stop:
            if task and not task.done():
                self.logger.debug(f"  Stopping {task_name} task...")
                self.logger.info(f"Cancelling {task_name} task...")
                task.cancel()
                try:
                    # Wait for task to cancel with timeout
                    await asyncio.wait_for(task, timeout=2.0)
                    self.logger.debug(f"  [OK] {task_name} task stopped")
                except asyncio.CancelledError:
                    self.logger.debug(f"  [OK] {task_name} task cancelled")
                except asyncio.TimeoutError:
                    self.logger.warning(f"  [WARNING] {task_name} task cancellation timeout - forcing stop")
                except Exception as e:
                    self.logger.error(f"  [ERROR] Error stopping {task_name} task: {e}")
        
        # Stop scheduler and watchdog (after background tasks)
        try:
            await self.scheduler.stop()
            await self.watchdog.stop()
        except Exception as e:
            self.logger.error(f"Error stopping scheduler/watchdog: {e}")
        
        self.logger.info("Pipeline Orchestrator stopped")
    
    async def _heartbeat_loop(self):
        """
        ADDITION 3: Event bus heartbeat loop - emit lightweight heartbeat events every 30-60 seconds.
        
        This proves:
        - WebSocket is alive
        - EventBus is alive
        - UI connection is healthy
        
        Prevents "nothing is happening, is it broken?" anxiety.
        
        CRITICAL: No background task is allowed to bring down the WebSocket or backend process.
        All exceptions are caught, logged, error events emitted, and the loop continues.
        """
        heartbeat_interval = 45  # 45 seconds (middle of 30-60 range)
        
        while self._running:
            try:
                await asyncio.sleep(heartbeat_interval)
                
                # Emit heartbeat event
                await self.event_bus.publish({
                    "run_id": "__system__",
                    "stage": "system",
                    "event": "heartbeat",
                    "timestamp": datetime.now(timezone.utc).isoformat(),
                    "msg": "System heartbeat - WebSocket and EventBus are alive",
                    "data": {}
                })
                
                self.logger.debug("[Heartbeat] Emitted system heartbeat")
            
            except asyncio.CancelledError:
                # Orchestrator is shutting down
                break
            except Exception as e:
                # CRITICAL: Catch all exceptions, log, emit error event, continue
                self.logger.error(f"[Heartbeat] Error in heartbeat loop: {e}", exc_info=True)
                try:
                    await self.event_bus.publish({
                        "run_id": "__system__",
                        "stage": "system",
                        "event": "error",
                        "timestamp": datetime.now(timezone.utc).isoformat(),
                        "msg": f"Heartbeat loop error: {e}",
                        "data": {"component": "heartbeat", "error": str(e)}
                    })
                except Exception as publish_error:
                    self.logger.error(f"[Heartbeat] Failed to emit error event: {publish_error}")
                # Continue loop - never crash
                await asyncio.sleep(heartbeat_interval)  # Wait before retrying
    
    async def _scheduler_health_check_loop(self):
        """
        Periodic scheduler health check - monitors state but does NOT auto-re-enable.
        
        Windows Task Scheduler can auto-disable tasks after failures. This loop checks every
        5 minutes and logs mismatches, but does NOT automatically re-enable.
        User must explicitly enable the scheduler via the dashboard.
        
        CRITICAL: No background task is allowed to bring down the WebSocket or backend process.
        All exceptions are caught, logged, and the loop continues.
        """
        check_interval = 300  # Check every 5 minutes (less aggressive)
        
        # Wait a bit before first check (let system stabilize)
        await asyncio.sleep(60)  # Wait 1 minute before first check
        
        while self._running:
            try:
                await asyncio.sleep(check_interval)
                
                # Check scheduler state (this will NOT auto-re-enable, just log mismatches)
                if self.scheduler:
                    try:
                        # Use a timeout to prevent hanging
                        state = await asyncio.wait_for(
                            asyncio.to_thread(self.scheduler.get_state),
                            timeout=30.0
                        )
                        # get_state() now only logs mismatches, does NOT auto-re-enable
                        # Just log for visibility
                        if state.get("scheduler_enabled") and state.get("windows_task_status", {}).get("enabled"):
                            self.logger.debug("[Scheduler Health] Scheduler is enabled and Windows task is enabled")
                        elif state.get("scheduler_enabled") and not state.get("windows_task_status", {}).get("enabled"):
                            self.logger.warning(
                                "[Scheduler Health] State mismatch detected: state file says enabled but Windows task is disabled. "
                                "Use the dashboard to explicitly re-enable the scheduler."
                            )
                    except asyncio.TimeoutError:
                        self.logger.warning("[Scheduler Health] Timeout checking scheduler state (took >30s)")
                    except Exception as e:
                        self.logger.error(f"[Scheduler Health] Error checking scheduler state: {e}", exc_info=True)
            
            except asyncio.CancelledError:
                # Orchestrator is shutting down
                break
            except Exception as e:
                # CRITICAL: Catch all exceptions, log, emit error event, continue
                self.logger.error(f"[Scheduler Health] Error in health check loop: {e}", exc_info=True)
                try:
                    await self.event_bus.publish({
                        "run_id": "__system__",
                        "stage": "system",
                        "event": "error",
                        "timestamp": datetime.now(timezone.utc).isoformat(),
                        "msg": f"Scheduler health check loop error: {e}",
                        "data": {"component": "scheduler_health", "error": str(e)}
                    })
                except Exception as publish_error:
                    self.logger.error(f"[Scheduler Health] Failed to emit error event: {publish_error}")
                # Continue loop - never crash
                await asyncio.sleep(check_interval)  # Wait before retrying
    
    async def _archive_old_files_task(self):
        """Archive files older than 7 days"""
        while self._running:
            try:
                # Run once per day (86400 seconds)
                await asyncio.sleep(86400)
                
                if not self._running:
                    break
                
                cutoff_date = datetime.now(timezone.utc) - timedelta(days=7)  # UTC-aware cutoff
                archive_dir = self.config.event_logs_dir / "archive"
                archive_dir.mkdir(parents=True, exist_ok=True)
                
                archived_count = 0
                archived_size_mb = 0
                
                for jsonl_file in self.config.event_logs_dir.glob("pipeline_*.jsonl"):
                    try:
                        file_date = datetime.fromtimestamp(jsonl_file.stat().st_mtime, tz=timezone.utc)
                        if file_date < cutoff_date:
                            archive_path = archive_dir / jsonl_file.name
                            
                            # Add timestamp if file already exists in archive
                            if archive_path.exists():
                                timestamp = datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")
                                archive_path = archive_dir / f"{jsonl_file.stem}_{timestamp}{jsonl_file.suffix}"
                            
                            file_size_mb = jsonl_file.stat().st_size / (1024 * 1024)
                            shutil.move(str(jsonl_file), str(archive_path))
                            archived_count += 1
                            archived_size_mb += file_size_mb
                    except Exception as e:
                        self.logger.debug(f"Error archiving {jsonl_file.name}: {e}")
                        continue
                
                if archived_count > 0:
                    self.logger.info(
                        f"📁 Archived {archived_count} old file(s) "
                        f"({archived_size_mb:.2f} MB total) to archive/"
                    )
            
            except asyncio.CancelledError:
                break
            except Exception as e:
                # CRITICAL: Catch all exceptions, log, emit error event, continue
                self.logger.error(f"[Archive Task] Archive task error: {e}", exc_info=True)
                try:
                    await self.event_bus.publish({
                        "run_id": "__system__",
                        "stage": "system",
                        "event": "error",
                        "timestamp": datetime.now(timezone.utc).isoformat(),
                        "msg": f"Archive task error: {e}",
                        "data": {"component": "archive_task", "error": str(e)}
                    })
                except Exception as publish_error:
                    self.logger.error(f"[Archive Task] Failed to emit error event: {publish_error}")
                # Wait 1 hour before retrying on error - continue loop, never crash
                await asyncio.sleep(3600)
    
    async def start_pipeline(self, manual: bool = False, run_id: Optional[str] = None) -> RunContext:
        """
        Start a new pipeline run.
        
        Args:
            manual: True if manually triggered, False if scheduled
            run_id: Optional run_id (if provided, pipeline/start event should already be emitted)
        
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
        
        # Generate run_id if not provided (for scheduled runs or direct calls)
        if run_id is None:
            run_id = str(uuid.uuid4())
        
        # Emit pipeline/start event - this is the ONLY place that emits pipeline/start
        # Ensures exactly one pipeline/start per run_id, ever
        await self.event_bus.publish({
            "run_id": run_id,
            "stage": "pipeline",
            "event": "start",
            "timestamp": datetime.now(timezone.utc).isoformat(),
            "msg": "Pipeline run started",
            "data": {"manual": manual}
        })
        
        # Acquire lock
        if not await self.lock_manager.acquire(run_id):
            raise ValueError("Failed to acquire lock (pipeline may already be running)")
        
        try:
            # Create run context
            run_ctx = await self.state_manager.create_run(
                run_id=run_id,
                metadata={
                    "manual": manual,
                    "triggered_at": datetime.now(timezone.utc).isoformat()
                }
            )
            
            # Transition to starting
            await self.state_manager.transition(
                PipelineRunState.STARTING,
                metadata={"manual": manual}
            )
            
            # Emit scheduler event if this is a scheduled run
            if not manual:
                self.logger.info(f"📅 Publishing scheduler/start event for run {run_id[:8]}...")
                await self.event_bus.publish({
                    "run_id": run_id,
                    "stage": "scheduler",
                    "event": "start",
                    "timestamp": datetime.now(timezone.utc).isoformat(),
                    "msg": "Scheduled pipeline run started",
                    "data": {"manual": False}
                })
                self.logger.info(f"[SUCCESS] Scheduler/start event published")
            else:
                self.logger.debug(f"Manual run - skipping scheduler event")
            
            # Emit explicit "manual_requested" event for UI (no delay - pipeline runs immediately)
            if manual:
                await self.event_bus.publish({
                    "run_id": run_id,
                    "stage": "pipeline",
                    "event": "manual_requested",
                    "timestamp": datetime.now(timezone.utc).isoformat(),
                    "msg": "Manual pipeline run requested",
                    "data": {"manual": True}
                })
                # No delay - UI connects when it connects, pipeline runs immediately
                # UI relies on: JSONL for history, snapshot endpoint, EventBus for live tail
            
            # Run pipeline in background (state transitions handled by runner)
            # Track task handle for operational hygiene
            self._active_run_task = asyncio.create_task(self._run_pipeline_background(run_ctx))
            
            # Add done callback to log exceptions
            def log_task_exception(task):
                try:
                    task.result()  # This will raise if task failed
                except Exception as e:
                    self.logger.error(f"Background pipeline task failed: {e}", exc_info=True)
            
            self._active_run_task.add_done_callback(log_task_exception)
            
            return run_ctx
        
        except Exception as e:
            # Release lock on error
            await self.lock_manager.release(run_id)
            raise
    
    async def _run_pipeline_background(self, run_ctx: RunContext):
        """
        Run pipeline in background task.
        
        CRITICAL: No background task is allowed to bring down the WebSocket or backend process.
        All exceptions are caught, logged, error events emitted, and state is properly updated.
        """
        manual = run_ctx.metadata.get("manual", True)
        success = False
        
        try:
            # Run pipeline
            await self.runner.run_pipeline(run_ctx)
            success = True
        except Exception as e:
            # CRITICAL: Catch all exceptions, log, emit error event, update state
            self.logger.error(f"[Pipeline Background] Pipeline run error: {e}", exc_info=True)
            try:
                await self.state_manager.transition(
                    PipelineRunState.FAILED,
                    error=str(e)
                )
                # Emit error event
                await self.event_bus.publish({
                    "run_id": run_ctx.run_id,
                    "stage": "pipeline",
                    "event": "error",
                    "timestamp": datetime.now(timezone.utc).isoformat(),
                    "msg": f"Pipeline run failed: {e}",
                    "data": {"error": str(e), "run_id": run_ctx.run_id}
                })
            except Exception as state_error:
                self.logger.error(f"[Pipeline Background] Failed to update state or emit error event: {state_error}")
            success = False
        finally:
            # Emit completion event based on run type
            if not manual:
                # Scheduled run completion event
                event_type = "success" if success else "failed"
                self.logger.info(f"📅 Publishing scheduler/{event_type} event for run {run_ctx.run_id[:8]}...")
                await self.event_bus.publish({
                    "run_id": run_ctx.run_id,
                    "stage": "scheduler",
                    "event": event_type,
                    "timestamp": datetime.now(timezone.utc).isoformat(),
                    "msg": f"Scheduled pipeline run {'completed successfully' if success else 'failed'}",
                    "data": {"manual": False, "success": success}
                })
                self.logger.info(f"[SUCCESS] Scheduler/{event_type} event published")
            else:
                # Manual run completion event
                event_type = "success" if success else "failed"
                self.logger.info(f"📋 Publishing pipeline/{event_type} event for manual run {run_ctx.run_id[:8]}...")
                await self.event_bus.publish({
                    "run_id": run_ctx.run_id,
                    "stage": "pipeline",
                    "event": event_type,
                    "timestamp": datetime.now(timezone.utc).isoformat(),
                    "msg": f"Manual pipeline run {'completed successfully' if success else 'failed'}",
                    "data": {"manual": True, "success": success}
                })
                self.logger.info(f"[SUCCESS] Pipeline/{event_type} event published for manual run")
            
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
            "timestamp": datetime.now(timezone.utc).isoformat(),
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
        
        # Get recent events from in-memory buffer (includes latest events even if not yet in JSONL)
        recent_events = self.event_bus.get_recent_events(limit=100)
        
        # If there's an active run, get events for that run from JSONL file
        # This ensures we capture all events including early ones like translator/start
        run_events = []
        event_source = "jsonl"
        if status:
            # Get events from JSONL file (authoritative source, includes all events)
            run_events = self.event_bus.get_events_for_run(status.run_id, limit=1000)
            
            # Defensive fallback: If JSONL file is missing/rotated/archived mid-run,
            # fall back to in-memory recent_events for this run_id
            if not run_events and status.run_id:
                recent_for_run = [e for e in recent_events if e.get("run_id") == status.run_id]
                if recent_for_run:
                    run_events = recent_for_run
                    event_source = "memory_fallback"
                    self.logger.warning(
                        f"JSONL file not found for run {status.run_id[:8]}, "
                        f"using in-memory events fallback ({len(run_events)} events)"
                    )
            
            # Also merge in recent events from memory buffer for this run_id (catches very recent events)
            # This handles race condition where event is in memory but not yet written to JSONL
            recent_for_run = [e for e in recent_events if e.get("run_id") == status.run_id]
            
            # Merge, avoiding duplicates (compare by timestamp + stage + event)
            existing_keys = {(e.get("timestamp"), e.get("stage"), e.get("event")) for e in run_events}
            for event in recent_for_run:
                key = (event.get("timestamp"), event.get("stage"), event.get("event"))
                if key not in existing_keys:
                    run_events.append(event)
                    # Update source if we're merging from memory
                    if event_source == "jsonl":
                        event_source = "jsonl+memory"
            
            # Sort by timestamp to maintain chronological order
            run_events.sort(key=lambda e: e.get("timestamp", ""))
        
        return {
            "status": status.to_dict() if status else None,
            "recent_events": recent_events,
            "run_events": run_events,
            "event_source": event_source,  # jsonl | jsonl+memory | memory_fallback
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
            # Initialize services before running stage
            # This is required because _run_stage() accesses translator_service, analyzer_service, and merger_service
            self.runner._initialize_services(run_id)
            
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

