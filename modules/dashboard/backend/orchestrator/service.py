"""
Pipeline Orchestrator Service - Main facade
"""

import asyncio
import logging
import uuid
import shutil
from pathlib import Path
from typing import Optional, Dict, Any
from datetime import datetime, timedelta

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
        self._jsonl_monitor_task: Optional[asyncio.Task] = None
        self._archive_task: Optional[asyncio.Task] = None
        
        # JSONL monitor state
        self._file_offsets: Dict[str, int] = {}  # file_path -> last_read_offset (bytes)
        self._seen_events: Dict[tuple, bool] = {}  # (run_id, line_index) -> seen
        self._startup_time = datetime.now()  # Track when monitor started (to identify new files)
        self._ingestion_metrics = {
            "events_ingested": 0,
            "last_reset_time": datetime.now().timestamp()
        }
    
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
        
        # Start JSONL monitor for scheduled runs
        self._jsonl_monitor_task = asyncio.create_task(self._monitor_jsonl_files())
        
        # Start archiving task (archive old files daily)
        self._archive_task = asyncio.create_task(self._archive_old_files_task())
        
        self.logger.info("Pipeline Orchestrator started")
        self.logger.info("[JSONL] JSONL Event Replay Monitor started")
        self.logger.info("   - Subsystem: Real-time scheduled event ingestion")
        self.logger.info("   - Check interval: 2 seconds")
        self.logger.info("   - Tracking: Offset-based (bytes)")
        self.logger.info("   - Deduplication: (run_id, line_index)")
        self.logger.info(f"   - Monitoring: {self.config.event_logs_dir}")
    
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
        
        # Stop JSONL monitor
        if self._jsonl_monitor_task:
            self._jsonl_monitor_task.cancel()
            try:
                await self._jsonl_monitor_task
            except asyncio.CancelledError:
                pass
        
        # Stop archiving task
        if self._archive_task:
            self._archive_task.cancel()
            try:
                await self._archive_task
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
                    # LockManager uses file timestamps, no heartbeat needed
                    # Lock staleness is checked by file age in acquire()
                    pass
            
            except asyncio.CancelledError:
                break
            except Exception as e:
                self.logger.error(f"Heartbeat loop error: {e}")
    
    async def _monitor_jsonl_files(self):
        """
        Monitor JSONL event files from scheduled runs and publish to EventBus.
        
        This reads events written by standalone pipeline runs (Windows Task Scheduler)
        and makes them available in the dashboard via EventBus/WebSocket.
        """
        import json
        import time
        
        while self._running:
            try:
                await asyncio.sleep(2)  # Check every 2 seconds
                
                # Scan for JSONL files
                if not self.config.event_logs_dir.exists():
                    continue
                
                jsonl_files = list(self.config.event_logs_dir.glob("pipeline_*.jsonl"))
                
                for jsonl_file in jsonl_files:
                    try:
                        # Skip files that are already too large (>50 MB)
                        file_size_mb = jsonl_file.stat().st_size / (1024 * 1024)
                        if file_size_mb > 50:
                            self.logger.debug(
                                f"[SKIP] Skipping large file {jsonl_file.name} "
                                f"({file_size_mb:.2f} MB) - already processed or too large"
                            )
                            # Mark as processed to avoid checking again
                            file_key = jsonl_file.name
                            if file_key not in self._file_offsets:
                                self._file_offsets[file_key] = jsonl_file.stat().st_size
                            continue
                        
                        # Get file key (just the filename)
                        file_key = jsonl_file.name
                        
                        # Get last offset
                        last_offset = self._file_offsets.get(file_key, 0)
                        current_size = jsonl_file.stat().st_size
                        
                        # Get file modification time
                        file_mtime = datetime.fromtimestamp(jsonl_file.stat().st_mtime)
                        
                        # Skip if no new data (normal case - file is complete and we've processed it)
                        if current_size <= last_offset:
                            continue
                        
                        # On startup: Only process complete files if they're NEW (created/modified after startup)
                        # OR if we haven't seen events from this run_id before (deduplication will handle it)
                        if last_offset == 0 and current_size > 0:
                            # First time seeing this file
                            # Only process if file was created/modified recently (within last hour) OR
                            # if we haven't seen any events from this run_id
                            time_since_mod = (self._startup_time - file_mtime).total_seconds()
                            
                            # Extract run_id from filename
                            run_id_from_file = file_key.replace("pipeline_", "").replace(".jsonl", "")
                            
                            # Check if we've seen events from this run_id
                            seen_this_run = any(
                                run_id == run_id_from_file 
                                for run_id, _ in self._seen_events.keys()
                            )
                            
                            # Only process if:
                            # 1. File is recent (created/modified within last hour), OR
                            # 2. We haven't seen events from this run_id (new run)
                            if time_since_mod > 3600 and seen_this_run:
                                # Old file and we've already seen events from this run - skip
                                self._file_offsets[file_key] = current_size  # Mark as processed
                                self.logger.debug(f"[SKIP] Skipping old complete file {file_key} (already processed)")
                                continue
                        
                        # Read only new data
                        with open(jsonl_file, "rb") as f:
                            f.seek(last_offset)
                            new_data = f.read()
                        
                        if not new_data:
                            continue
                        
                        # Decode and split into lines
                        new_text = new_data.decode("utf-8", errors="ignore")
                        lines = new_text.split("\n")
                        
                        # Calculate starting line index
                        if last_offset == 0:
                            line_index = 0
                        else:
                            # Count lines before offset
                            with open(jsonl_file, "rb") as f:
                                before_data = f.read(last_offset).decode("utf-8", errors="ignore")
                            line_index = before_data.count("\n")
                            
                            # Skip first line if we're in the middle of it
                            if not before_data.endswith("\n") and len(lines) > 0:
                                lines = lines[1:]  # Skip partial line
                                line_index += 1
                        
                        # Process new lines IN CHUNKS with yields to avoid blocking
                        ingested_count = 0
                        chunk_size = 100  # Process 100 events at a time
                        
                        for chunk_start in range(0, len(lines), chunk_size):
                            chunk = lines[chunk_start:chunk_start + chunk_size]
                            
                            for line in chunk:
                                if not line.strip():
                                    line_index += 1
                                    continue
                                
                                try:
                                    event = json.loads(line)
                                    run_id = event.get("run_id", "unknown")
                                    
                                    # Deduplication key
                                    dedup_key = (run_id, line_index)
                                    
                                    # Skip if already seen
                                    if dedup_key in self._seen_events:
                                        line_index += 1
                                        continue
                                    
                                    # Note: We used to skip events < 5 seconds old, assuming EventLoggerWithBus published them.
                                    # However, EventLoggerWithBus can fail silently (worker threads, event loop issues),
                                    # so we now process ALL events from JSONL files to ensure nothing is missed.
                                    # Deduplication (via _seen_events) prevents double-publishing if EventLoggerWithBus succeeded.
                                    
                                    # Mark as seen
                                    self._seen_events[dedup_key] = True
                                    
                                    # Publish to EventBus (this will broadcast to WebSocket subscribers)
                                    await self.event_bus.publish(event)
                                    ingested_count += 1
                                    
                                    # Log important events
                                    stage = event.get("stage", "")
                                    event_type = event.get("event", "")
                                    if event_type in ["start", "success", "failed", "state_change"]:
                                        self.logger.debug(
                                            f"📡 Ingested: {stage}/{event_type} "
                                            f"(run: {run_id[:8]}, line: {line_index})"
                                        )
                                    
                                except json.JSONDecodeError:
                                    # Skip invalid JSON lines
                                    pass
                                except Exception as e:
                                    self.logger.debug(f"Error processing event line: {e}")
                                
                                line_index += 1
                            
                            # YIELD CONTROL AFTER EACH CHUNK
                            # This allows HTTP requests to be processed
                            await asyncio.sleep(0.01)  # 10ms pause between chunks
                        
                        # Update offset
                        self._file_offsets[file_key] = current_size
                        
                        # Update metrics
                        if ingested_count > 0:
                            self._ingestion_metrics["events_ingested"] += ingested_count
                            self.logger.debug(
                                f"[SUCCESS] Ingested {ingested_count} new events from {file_key} "
                                f"(offset: {last_offset} → {current_size})"
                            )
                        
                        # Cleanup deduplication cache (keep last 10000 entries)
                        if len(self._seen_events) > 10000:
                            # Keep most recent 5000
                            items = list(self._seen_events.items())
                            self._seen_events = dict(items[-5000:])
                        
                    except Exception as e:
                        self.logger.debug(f"Error monitoring {jsonl_file}: {e}")
                        continue
                
            except asyncio.CancelledError:
                break
            except Exception as e:
                self.logger.error(f"JSONL monitor error: {e}", exc_info=True)
                await asyncio.sleep(2)
    
    async def _archive_old_files_task(self):
        """Archive files older than 7 days"""
        while self._running:
            try:
                # Run once per day (86400 seconds)
                await asyncio.sleep(86400)
                
                if not self._running:
                    break
                
                cutoff_date = datetime.now() - timedelta(days=7)
                archive_dir = self.config.event_logs_dir / "archive"
                archive_dir.mkdir(parents=True, exist_ok=True)
                
                archived_count = 0
                archived_size_mb = 0
                
                for jsonl_file in self.config.event_logs_dir.glob("pipeline_*.jsonl"):
                    try:
                        file_date = datetime.fromtimestamp(jsonl_file.stat().st_mtime)
                        if file_date < cutoff_date:
                            archive_path = archive_dir / jsonl_file.name
                            
                            # Add timestamp if file already exists in archive
                            if archive_path.exists():
                                timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
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
                self.logger.error(f"Archive task error: {e}", exc_info=True)
                # Wait 1 hour before retrying on error
                await asyncio.sleep(3600)
    
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
            
            # Emit scheduler event if this is a scheduled run
            if not manual:
                self.logger.info(f"📅 Publishing scheduler/start event for run {run_id[:8]}...")
                await self.event_bus.publish({
                    "run_id": run_id,
                    "stage": "scheduler",
                    "event": "start",
                    "timestamp": datetime.now().isoformat(),
                    "msg": "Scheduled pipeline run started",
                    "data": {"manual": False}
                })
                self.logger.info(f"[SUCCESS] Scheduler/start event published")
            else:
                self.logger.debug(f"Manual run - skipping scheduler event")
            
            # Run pipeline in background (state transitions handled by runner)
            asyncio.create_task(self._run_pipeline_background(run_ctx))
            
            return run_ctx
        
        except Exception as e:
            # Release lock on error
            await self.lock_manager.release(run_id)
            raise
    
    async def _run_pipeline_background(self, run_ctx: RunContext):
        """Run pipeline in background task"""
        manual = run_ctx.metadata.get("manual", True)
        success = False
        
        try:
            # Run pipeline
            await self.runner.run_pipeline(run_ctx)
            success = True
        except Exception as e:
            self.logger.error(f"Pipeline run error: {e}", exc_info=True)
            await self.state_manager.transition(
                PipelineRunState.FAILED,
                error=str(e)
            )
            success = False
        finally:
            # Emit scheduler completion event if this was a scheduled run
            if not manual:
                event_type = "success" if success else "failed"
                self.logger.info(f"📅 Publishing scheduler/{event_type} event for run {run_ctx.run_id[:8]}...")
                await self.event_bus.publish({
                    "run_id": run_ctx.run_id,
                    "stage": "scheduler",
                    "event": event_type,
                    "timestamp": datetime.now().isoformat(),
                    "msg": f"Scheduled pipeline run {'completed successfully' if success else 'failed'}",
                    "data": {"manual": False, "success": success}
                })
                self.logger.info(f"[SUCCESS] Scheduler/{event_type} event published")
            
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

