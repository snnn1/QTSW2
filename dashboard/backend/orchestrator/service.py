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
        # Use 'orchestrator' logger name to match main.py configuration
        self.logger = logger or logging.getLogger("orchestrator")
        
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
        self._jsonl_monitor_task: Optional[asyncio.Task] = None
        
        # JSONL monitor state
        self._file_offsets: Dict[str, int] = {}  # file_path -> last_read_offset (bytes)
        self._seen_events: Dict[tuple, bool] = {}  # (run_id, line_index) -> seen
        self._startup_time = datetime.now()  # Track when monitor started (to identify new files)
        self._monitor_start_time = None  # When monitor loop started
        self._iteration_count = 0  # Number of monitor iterations
        self._active_files: set = set()  # Files currently being written to (active)
        self._startup_bootstrap_complete = False  # Whether startup bootstrap is done
        self._ingestion_metrics = {
            "events_ingested": 0,
            "last_reset_time": datetime.now().timestamp()
        }
        # Startup cost tracking
        self._startup_metrics = {
            "files_scanned_total": 0,
            "bytes_read_total": 0,
            "events_published_total": 0,
            "iterations": 0,
            "startup_phase_complete": False,
            "startup_duration_sec": 0.0
        }
        
        # Monitor configuration - BOUNDS AND THROTTLING
        self._monitor_config = {
            "max_startup_files": 10,  # Only process last 10 files on startup
            "startup_time_window_sec": 3600,  # Only process files from last hour
            "max_bytes_per_iteration": 10 * 1024 * 1024,  # 10MB per iteration
            "max_events_per_iteration": 1000,  # Max events per iteration
            "throttle_delay_sec": 0.1,  # Delay between chunks
            "chunk_size_events": 100,  # Process events in chunks
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
        
        self.logger.info("Pipeline Orchestrator started")
        self.logger.info("🚀 JSONL Event Replay Monitor started")
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
    
    async def _process_startup_bootstrap(self):
        """
        Process a small, controlled bootstrap set of files on startup.
        Scans ALL files but only replays events from recent/active files.
        Runs OFF the main asyncio loop using thread pool.
        Only REPLAYS events from:
        - Last N files (by modification time)
        - Files from last hour
        - With bounds and throttling
        """
        import json
        import time
        
        self.logger.info("=" * 80)
        self.logger.info("📦 STARTUP BOOTSTRAP: Scanning all files, replaying only recent")
        self.logger.info("=" * 80)
        
        if not self.config.event_logs_dir.exists():
            self.logger.warning("⚠️  Event logs directory does not exist")
            return
        
        # Get ALL files sorted by modification time (newest first)
        all_files = list(self.config.event_logs_dir.glob("pipeline_*.jsonl"))
        if not all_files:
            self.logger.info("📦 No JSONL files found")
            return
        
        # Sort by modification time (newest first)
        all_files.sort(key=lambda f: f.stat().st_mtime, reverse=True)
        
        self.logger.info(f"📦 Found {len(all_files)} total JSONL files")
        
        # Mark all files as "seen" (register offsets) but don't replay old ones
        cutoff_time = time.time() - self._monitor_config["startup_time_window_sec"]
        bootstrap_files = []  # Files to actually replay events from
        old_files_count = 0
        
        for f in all_files:
            file_key = f.name
            file_mtime = f.stat().st_mtime
            file_size = f.stat().st_size
            
            # Register file offset (mark as seen, but don't replay)
            if file_key not in self._file_offsets:
                self._file_offsets[file_key] = file_size  # Mark old files as fully processed
            
            # Only replay events from recent files
            if file_mtime >= cutoff_time:
                bootstrap_files.append(f)
            elif len(bootstrap_files) < self._monitor_config["max_startup_files"]:
                bootstrap_files.append(f)
            else:
                old_files_count += 1
        
        self.logger.info(f"📦 Will replay events from: {len(bootstrap_files)} recent files")
        self.logger.info(f"📦 Skipping replay for: {old_files_count} old files (already registered)")
        
        # Process bootstrap files in thread pool (OFF main loop)
        total_bytes = 0
        total_events = 0
        
        for idx, jsonl_file in enumerate(bootstrap_files, 1):
            file_key = jsonl_file.name
            file_size = jsonl_file.stat().st_size
            file_mtime = datetime.fromtimestamp(jsonl_file.stat().st_mtime)
            age_sec = (self._startup_time - file_mtime).total_seconds()
            
            self.logger.info(
                f"📦 [{idx}/{len(bootstrap_files)}] {file_key} "
                f"({file_size:,} bytes, age: {age_sec:.0f}s)"
            )
            
            # Process file with bounds and throttling (in thread pool)
            bytes_read, events_published = await self._process_file_bounded(
                jsonl_file, 
                is_startup=True,
                max_bytes=self._monitor_config["max_bytes_per_iteration"],
                max_events=self._monitor_config["max_events_per_iteration"]
            )
            
            total_bytes += bytes_read
            total_events += events_published
            
            # Mark as active if file is still being written to
            if file_mtime >= time.time() - 60:  # Modified in last minute
                self._active_files.add(file_key)
        
        bootstrap_duration = time.time() - self._monitor_start_time
        self.logger.info("=" * 80)
        self.logger.info("✅ STARTUP BOOTSTRAP COMPLETE")
        self.logger.info(f"   Files processed: {len(bootstrap_files)}")
        self.logger.info(f"   Total bytes: {total_bytes:,}")
        self.logger.info(f"   Total events: {total_events}")
        self.logger.info(f"   Duration: {bootstrap_duration:.2f}s")
        self.logger.info(f"   Active files registered: {len(self._active_files)}")
        self.logger.info("=" * 80)
    
    async def _process_file_bounded(
        self, 
        jsonl_file: Path, 
        is_startup: bool = False,
        max_bytes: int = None,
        max_events: int = None
    ) -> tuple[int, int]:
        """
        Process a file with bounds and throttling.
        Returns: (bytes_read, events_published)
        """
        import json
        
        file_key = jsonl_file.name
        last_offset = self._file_offsets.get(file_key, 0)
        current_size = jsonl_file.stat().st_size
        
        if current_size <= last_offset:
            return 0, 0
        
        # Read bounded amount
        bytes_to_read = min(current_size - last_offset, max_bytes or current_size)
        
        # Read in thread pool (non-blocking)
        def read_file_chunk():
            with open(jsonl_file, "rb") as f:
                f.seek(last_offset)
                return f.read(bytes_to_read)
        
        new_data = await asyncio.to_thread(read_file_chunk)
        bytes_read = len(new_data)
        
        if not new_data:
            return 0, 0
        
        # Decode and split into lines (preserve empty lines for line index calculation)
        new_text = new_data.decode("utf-8", errors="ignore")
        all_lines = new_text.split("\n")
        
        # Calculate starting line index
        if last_offset == 0:
            line_index = 0
        else:
            # Count lines before offset
            def count_lines_before():
                with open(jsonl_file, "rb") as f:
                    before_data = f.read(last_offset).decode("utf-8", errors="ignore")
                return before_data.count("\n")
            
            line_index = await asyncio.to_thread(count_lines_before)
            
            # Skip first line if we're in the middle of it
            if len(all_lines) > 0 and not new_text.startswith("\n"):
                # Check if we're mid-line by seeing if before data ends with newline
                with open(jsonl_file, "rb") as f:
                    before_bytes = f.read(last_offset)
                    if before_bytes and not before_bytes.endswith(b"\n"):
                        all_lines = all_lines[1:]  # Skip partial line
                        line_index += 1
        
        # Filter to non-empty lines for processing
        lines_to_process = []
        line_indices = []
        for i, line in enumerate(all_lines):
            if line.strip():
                lines_to_process.append(line)
                line_indices.append(line_index + i)
        
        # Process in chunks with throttling
        events_published = 0
        chunk_size = self._monitor_config["chunk_size_events"]
        
        for chunk_start in range(0, len(lines_to_process), chunk_size):
            chunk_end = min(chunk_start + chunk_size, len(lines_to_process))
            chunk_lines = lines_to_process[chunk_start:chunk_end]
            chunk_indices = line_indices[chunk_start:chunk_end]
            
            # Check bounds
            if max_events and events_published >= max_events:
                break
            
            # Process chunk
            for line, idx in zip(chunk_lines, chunk_indices):
                if max_events and events_published >= max_events:
                    break
                
                try:
                    event = json.loads(line)
                    run_id = event.get("run_id", "unknown")
                    dedup_key = (run_id, idx)
                    
                    if dedup_key in self._seen_events:
                        continue
                    
                    self._seen_events[dedup_key] = True
                    await self.event_bus.publish(event)
                    events_published += 1
                    
                except (json.JSONDecodeError, Exception):
                    continue
            
            # Throttle: delay between chunks
            if chunk_end < len(lines_to_process):
                await asyncio.sleep(self._monitor_config["throttle_delay_sec"])
        
        # Update offset to where we read (even if we hit bounds, we'll continue next iteration)
        # For bounded reads, we only read up to max_bytes, so update accordingly
        if max_bytes and bytes_read >= max_bytes:
            # Hit byte limit - update offset but don't mark as complete
            self._file_offsets[file_key] = last_offset + bytes_read
        else:
            # Processed all available data
            self._file_offsets[file_key] = current_size
        
        return bytes_read, events_published
    
    async def _monitor_jsonl_files(self):
        """
        Monitor JSONL event files from scheduled runs and publish to EventBus.
        
        REDESIGNED TO PREVENT UNBOUNDED HISTORICAL REPLAY:
        - Startup: Only process small bootstrap set (last N files or last hour)
        - Startup processing: Off main loop (thread pool)
        - Bounded: Max bytes/events per iteration
        - Throttled: Process in chunks with delays
        - Live: Only tail active files (files being written to)
        """
        import json
        import time
        
        self._monitor_start_time = time.time()
        self.logger.info("=" * 80)
        self.logger.info("🚀 JSONL Monitor Starting (REDESIGNED - Bounded & Throttled)")
        self.logger.info(f"   Monitor start time: {datetime.now().isoformat()}")
        self.logger.info(f"   Startup bootstrap: Last {self._monitor_config['max_startup_files']} files or last {self._monitor_config['startup_time_window_sec']}s")
        self.logger.info(f"   Bounds: {self._monitor_config['max_bytes_per_iteration']//1024//1024}MB/iter, {self._monitor_config['max_events_per_iteration']} events/iter")
        self.logger.info("=" * 80)
        
        # Process startup bootstrap set OFF the main loop
        if not self._startup_bootstrap_complete:
            await self._process_startup_bootstrap()
            self._startup_bootstrap_complete = True
        
        # Main monitor loop - only tail active files
        while self._running:
            iteration_start = time.time()
            self._iteration_count += 1
            iteration_metrics = {
                "files_scanned": 0,
                "bytes_read": 0,
                "events_published": 0,
                "files_processed": 0,
                "files_skipped": 0,
                "files_new_on_startup": 0
            }
            
            try:
                await asyncio.sleep(2)  # Check every 2 seconds
                
                # Scan for JSONL files
                if not self.config.event_logs_dir.exists():
                    if self._iteration_count == 1:
                        self.logger.warning("⚠️  Event logs directory does not exist")
                    continue
                
                scan_start = time.time()
                jsonl_files = list(self.config.event_logs_dir.glob("pipeline_*.jsonl"))
                scan_duration = time.time() - scan_start
                iteration_metrics["files_scanned"] = len(jsonl_files)
                
                # LIVE MONITOR: Scan all files, but only tail active ones
                # Register all files (so we know about them) but only process active ones
                current_time = time.time()
                active_file_paths = []
                
                for jsonl_file in jsonl_files:
                    file_key = jsonl_file.name
                    file_mtime = jsonl_file.stat().st_mtime
                    
                    # Register file if not seen before
                    if file_key not in self._file_offsets:
                        file_size = jsonl_file.stat().st_size
                        self._file_offsets[file_key] = file_size  # Mark as processed (old file)
                    
                    # Only tail files being actively written to
                    if current_time - file_mtime < 120:  # Modified in last 2 minutes
                        self._active_files.add(file_key)
                        active_file_paths.append(jsonl_file)
                
                if self._iteration_count == 1:
                    self.logger.info(f"📊 LIVE MONITOR: {len(active_file_paths)} active files (from {len(jsonl_files)} total files visible)")
                
                # Process only active files with bounds
                for jsonl_file in active_file_paths:
                    try:
                        file_key = jsonl_file.name
                        current_size = jsonl_file.stat().st_size
                        last_offset = self._file_offsets.get(file_key, 0)
                        
                        # Skip if no new data
                        if current_size <= last_offset:
                            # File is complete and not being written to - remove from active
                            if current_time - jsonl_file.stat().st_mtime > 300:  # Not modified in 5 minutes
                                self._active_files.discard(file_key)
                            iteration_metrics["files_skipped"] += 1
                            continue
                        
                        # Process with bounds (live tailing)
                        bytes_read, events_published = await self._process_file_bounded(
                            jsonl_file,
                            is_startup=False,
                            max_bytes=self._monitor_config["max_bytes_per_iteration"],
                            max_events=self._monitor_config["max_events_per_iteration"]
                        )
                        
                        iteration_metrics["bytes_read"] += bytes_read
                        iteration_metrics["events_published"] += events_published
                        iteration_metrics["files_processed"] += 1
                        
                        if events_published > 0:
                            self._ingestion_metrics["events_ingested"] += events_published
                        
                    except Exception as e:
                        self.logger.debug(f"Error monitoring {jsonl_file}: {e}")
                        continue
                
                # Per-iteration summary
                iteration_duration = time.time() - iteration_start
                self._startup_metrics["iterations"] = self._iteration_count
                self._startup_metrics["bytes_read_total"] += iteration_metrics["bytes_read"]
                self._startup_metrics["events_published_total"] += iteration_metrics["events_published"]
                
                # Detailed iteration logging (especially for startup)
                if self._iteration_count == 1:
                    self.logger.info("=" * 80)
                    self.logger.info(f"📊 ITERATION #{self._iteration_count} SUMMARY:")
                    self.logger.info(f"   Files scanned: {iteration_metrics['files_scanned']}")
                    self.logger.info(f"   Files processed: {iteration_metrics['files_processed']}")
                    self.logger.info(f"   Files skipped: {iteration_metrics['files_skipped']}")
                    self.logger.info(f"   Files new on startup: {iteration_metrics['files_new_on_startup']}")
                    self.logger.info(f"   Bytes read: {iteration_metrics['bytes_read']:,}")
                    self.logger.info(f"   Events published: {iteration_metrics['events_published']}")
                    self.logger.info(f"   Iteration duration: {iteration_duration*1000:.2f}ms")
                    self.logger.info("=" * 80)
                    
                    # Mark startup phase complete after first iteration
                    startup_duration = time.time() - self._monitor_start_time
                    self._startup_metrics["startup_phase_complete"] = True
                    self._startup_metrics["startup_duration_sec"] = startup_duration
                    
                    self.logger.info("=" * 80)
                    self.logger.info("🎯 STARTUP PHASE COMPLETE")
                    self.logger.info(f"   Total startup duration: {startup_duration:.3f}s")
                    self.logger.info(f"   Cumulative bytes read: {self._startup_metrics['bytes_read_total']:,}")
                    self.logger.info(f"   Cumulative events published: {self._startup_metrics['events_published_total']}")
                    self.logger.info(f"   Deduplication cache size: {len(self._seen_events)}")
                    self.logger.info("=" * 80)
                elif self._iteration_count <= 5:
                    # Log first 5 iterations for visibility
                    self.logger.info(
                        f"📊 Iteration #{self._iteration_count}: "
                        f"{iteration_metrics['files_processed']} files, "
                        f"{iteration_metrics['bytes_read']:,} bytes, "
                        f"{iteration_metrics['events_published']} events, "
                        f"{iteration_duration*1000:.2f}ms"
                    )
                
            except asyncio.CancelledError:
                break
            except Exception as e:
                self.logger.error(f"JSONL monitor error: {e}", exc_info=True)
                await asyncio.sleep(2)
    
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

