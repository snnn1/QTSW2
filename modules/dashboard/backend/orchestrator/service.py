"""
Pipeline Orchestrator Service - Main facade
"""

import asyncio
import logging
import uuid
import shutil
import hashlib
import json
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
    Pipeline Orchestrator - Coordinates pipeline execution and state management.
    
    Execution Authority:
    - Windows Task Scheduler is the execution authority for scheduled runs
    - This orchestrator is advisory only - it coordinates runs, not timing
    - Backend never assumes it controls execution timing
    
    The orchestrator:
    - Manages pipeline state and coordination when runs occur
    - Observes scheduled runs via JSONL event file monitoring
    - Provides manual pipeline triggers via dashboard UI
    - Does NOT schedule or time pipeline executions
    
    Scheduled runs originate from Windows Task Scheduler (external system).
    Manual runs originate from dashboard UI requests.
    """
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
        self._archive_task: Optional[asyncio.Task] = None
        
        # JSONL monitor state
        self._file_offsets: Dict[str, Dict] = {}  # file_key -> {"offset": int, "size": int, "mtime": float, "sealed": bool}
        self._seen_events: Dict[tuple, datetime] = {}  # (file_key, event_hash) -> timestamp (last-resort deduplication guard, TTL 1 hour)
        self._startup_time = datetime.now()  # Track when monitor started (to identify new files)
        self._ingestion_metrics = {
            "events_ingested": 0,
            "last_reset_time": datetime.now().timestamp()
        }
        
        # Offset persistence file
        self._offsets_file = config.event_logs_dir / "jsonl_offsets.json"
        self._load_offsets()
    
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
        # Ensure we're in the right event loop context
        loop = asyncio.get_running_loop()
        self.logger.info(f"Creating JSONL monitor task - loop is running: {loop.is_running()}")
        print(f"[DEBUG] Creating JSONL monitor task - loop is running: {loop.is_running()}")
        
        self._jsonl_monitor_task = loop.create_task(self._monitor_jsonl_files())
        self.logger.info(f"JSONL Monitor task created: {self._jsonl_monitor_task} (done: {self._jsonl_monitor_task.done()})")
        print(f"🚀 JSONL Monitor task created (task object: {self._jsonl_monitor_task}) - watching: {self.config.event_logs_dir}")
        print(f"[DEBUG] Task created - done: {self._jsonl_monitor_task.done()}, cancelled: {self._jsonl_monitor_task.cancelled()}")
        
        # Yield control to event loop to allow task to start executing
        # This ensures the coroutine begins running immediately
        await asyncio.sleep(0)
        
        # Verify task started (should not be done yet if it's running)
        self.logger.info(f"After yield - JSONL Monitor task done: {self._jsonl_monitor_task.done()}, cancelled: {self._jsonl_monitor_task.cancelled()}")
        print(f"[DEBUG] After yield - Task done: {self._jsonl_monitor_task.done()}, cancelled: {self._jsonl_monitor_task.cancelled()}")
        
        # Start archiving task (archive old files daily)
        self._archive_task = asyncio.create_task(self._archive_old_files_task())
        
        self.logger.info("Pipeline Orchestrator started")
        self.logger.info(f"🚀 JSONL Event Replay Monitor task created - watching: {self.config.event_logs_dir}")
        self.logger.info("   - Subsystem: Real-time scheduled event ingestion")
        self.logger.info("   - Check interval: 2 seconds")
        self.logger.info("   - Tracking: Offset-based (bytes)")
        self.logger.info("   - Deduplication: (file_key, event_hash) - last-resort guard only")
        self.logger.info(f"   - Monitoring: {self.config.event_logs_dir}")
    
    async def stop(self):
        """Stop orchestrator"""
        print("Stopping orchestrator...")
        self.logger.info("Stopping orchestrator...")
        self._running = False
        
        # Stop scheduler and watchdog
        try:
            await self.scheduler.stop()
            await self.watchdog.stop()
        except Exception as e:
            self.logger.error(f"Error stopping scheduler/watchdog: {e}")
        
        # Stop all background tasks with timeout
        tasks_to_stop = [
            ("heartbeat", self._heartbeat_task),
            ("jsonl_monitor", self._jsonl_monitor_task),
            ("archive", self._archive_task)
        ]
        
        for task_name, task in tasks_to_stop:
            if task and not task.done():
                print(f"  Stopping {task_name} task...")
                self.logger.info(f"Cancelling {task_name} task...")
                task.cancel()
                try:
                    # Wait for task to cancel with timeout
                    await asyncio.wait_for(task, timeout=2.0)
                    print(f"  ✓ {task_name} task stopped")
                except asyncio.CancelledError:
                    print(f"  ✓ {task_name} task cancelled")
                except asyncio.TimeoutError:
                    print(f"  ⚠ {task_name} task cancellation timeout - forcing stop")
                    self.logger.warning(f"{task_name} task did not stop within timeout")
                except Exception as e:
                    print(f"  ✗ Error stopping {task_name} task: {e}")
                    self.logger.error(f"Error stopping {task_name} task: {e}")
        
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
    
    def _load_offsets(self):
        """Load persisted file offsets from disk"""
        import json
        
        if not self._offsets_file.exists():
            self.logger.info(f"[JSONL Monitor] No existing offsets file, starting fresh: {self._offsets_file}")
            return
        
        try:
            with open(self._offsets_file, 'r', encoding='utf-8') as f:
                data = json.load(f)
            
            # Validate and load offsets
            loaded_count = 0
            for file_key, offset_data in data.items():
                # Validate structure - support both new format (dict) and legacy (int)
                if isinstance(offset_data, dict) and "offset" in offset_data:
                    self._file_offsets[file_key] = offset_data
                    loaded_count += 1
                elif isinstance(offset_data, int):
                    # Legacy format - convert to new format
                    self._file_offsets[file_key] = {
                        "offset": offset_data,
                        "size": offset_data,  # Assume complete if only offset stored
                        "mtime": 0,  # Unknown
                        "sealed": False
                    }
                    loaded_count += 1
            
            self.logger.info(f"[JSONL Monitor] Loaded {loaded_count} file offsets from {self._offsets_file}")
        except Exception as e:
            self.logger.warning(f"[JSONL Monitor] Failed to load offsets file: {e}, starting fresh")
            self._file_offsets = {}
    
    def _save_offsets(self):
        """Persist file offsets to disk"""
        import json
        
        try:
            # Create parent directory if needed
            self._offsets_file.parent.mkdir(parents=True, exist_ok=True)
            
            # Write offsets to file
            with open(self._offsets_file, 'w', encoding='utf-8') as f:
                json.dump(self._file_offsets, f, indent=2)
        except Exception as e:
            self.logger.warning(f"[JSONL Monitor] Failed to save offsets file: {e}")
    
    async def _monitor_jsonl_files(self):
        """
        Monitor JSONL event files from scheduled runs and publish to EventBus.
        
        MODE: Live tailing only (NOT historical replay)
        - Only processes files modified recently (within last hour)
        - Only publishes events within LIVE_EVENT_WINDOW (EventBus enforces this)
        - Skips sealed (old, complete) files
        - Persists offsets across restarts
        
        ARCHITECTURE BOUNDARY:
        - JSONL is authoritative historical store (all events)
        - EventBus is live channel only (recent events within window)
        - Historical events available via snapshot endpoints, NOT via EventBus
        """
        # Hard try/except wrapper to catch any startup or runtime errors
        try:
            import json
            import time
            import os
            
            # Get live event window from EventBus (architectural constant)
            live_window_minutes = self.event_bus.LIVE_EVENT_WINDOW_MINUTES
            
            # Count eligible files (not sealed, modified recently)
            jsonl_files = list(self.config.event_logs_dir.glob("pipeline_*.jsonl")) if self.config.event_logs_dir.exists() else []
            eligible_files = 0
            sealed_files = 0
            for f in jsonl_files:
                offset_data = self._file_offsets.get(f.name, {})
                if offset_data.get("sealed", False):
                    sealed_files += 1
                else:
                    file_mtime = datetime.fromtimestamp(f.stat().st_mtime)
                    file_age_minutes = (datetime.now() - file_mtime.replace(tzinfo=None)).total_seconds() / 60
                    if file_age_minutes <= 60:  # Only files modified in last hour
                        eligible_files += 1
            
            # STARTUP LOG: Architecture confirmation
            self.logger.info("=" * 70)
            self.logger.info("[JSONL Monitor] ARCHITECTURE CONFIRMATION:")
            self.logger.info(f"  - Live event window: {live_window_minutes} minutes")
            self.logger.info(f"  - Total JSONL files: {len(jsonl_files)} ({sealed_files} sealed, {eligible_files} eligible)")
            self.logger.info(f"  - EventBus publish filter: ACTIVE (only events within window)")
            self.logger.info(f"  - Offset persistence: ENABLED ({self._offsets_file})")
            self.logger.info(f"  - Eligible JSONL files: {eligible_files} (not sealed, modified < 1h ago)")
            self.logger.info(f"  - Mode: Live tailing only (historical events via snapshot, not EventBus)")
            self.logger.info("=" * 70)
            
            self.logger.info("[JSONL Monitor] Monitor loop STARTING - will check for new events every 2 seconds")
            print("[JSONL Monitor] Monitor loop STARTING - will check for new events every 2 seconds")
            
            iteration = 0
            self.logger.info("[JSONL Monitor] Monitor loop STARTED - entering main loop")
            print("[JSONL Monitor] Monitor loop STARTED - entering main loop")
            
            while self._running:
                try:
                    await asyncio.sleep(2)  # Check every 2 seconds
                    iteration += 1
                    
                    # Check if we should stop after sleep
                    if not self._running:
                        break
                    
                    # Scan for JSONL files
                    if not self.config.event_logs_dir.exists():
                        if iteration % 15 == 0:  # Log every 30 seconds if directory doesn't exist
                            self.logger.warning(f"[JSONL Monitor] Event logs directory does not exist: {self.config.event_logs_dir}")
                        continue
                    
                    jsonl_files = list(self.config.event_logs_dir.glob("pipeline_*.jsonl"))
                    
                    # Log periodic status (every 30 iterations = 1 minute)
                    if iteration % 30 == 0:
                        self.logger.info(f"[JSONL Monitor] Iteration {iteration}: Scanning {len(jsonl_files)} JSONL files in {self.config.event_logs_dir}")
                        
                        # Log recently modified files
                        recent_files = sorted(jsonl_files, key=lambda f: f.stat().st_mtime, reverse=True)[:5]
                        if recent_files:
                            self.logger.info(
                                f"[JSONL Monitor] Recent files: "
                                + ", ".join([f"{f.name} ({datetime.fromtimestamp(f.stat().st_mtime).strftime('%H:%M:%S')})" for f in recent_files])
                            )
                        
                        # Log file offset tracking status
                        files_with_new_data = sum(1 for f in jsonl_files 
                                                 if not self._file_offsets.get(f.name, {}).get("sealed", False) and
                                                    f.stat().st_size > self._file_offsets.get(f.name, {}).get("offset", 0))
                        if files_with_new_data > 0:
                            self.logger.info(f"[JSONL Monitor] {files_with_new_data} file(s) have new data to process")
                    
                    # Check if we should stop before processing files
                    if not self._running:
                        break
                    
                    for jsonl_file in jsonl_files:
                        # Check _running periodically during file processing
                        if not self._running:
                            break
                        
                        try:
                            file_key = jsonl_file.name
                            current_size = jsonl_file.stat().st_size
                            file_mtime = jsonl_file.stat().st_mtime
                            file_mtime_dt = datetime.fromtimestamp(file_mtime)
                            
                            # Get persisted offset data
                            offset_data = self._file_offsets.get(file_key)
                            if offset_data is None:
                                offset_data = {"offset": 0, "size": 0, "mtime": 0, "sealed": False}
                                self._file_offsets[file_key] = offset_data
                            
                            # Check if file is sealed (old, complete, immutable)
                            if offset_data.get("sealed", False):
                                continue  # Skip sealed files entirely
                            
                            # Seal files that are old AND complete AND not growing
                            file_age_minutes = (datetime.now() - file_mtime_dt.replace(tzinfo=None)).total_seconds() / 60
                            last_offset = offset_data.get("offset", 0)
                            last_size = offset_data.get("size", 0)
                            
                            # Seal if: old (>1 hour) AND complete (offset == size) AND not growing (size unchanged)
                            if (file_age_minutes > 60 and 
                                last_offset >= last_size and 
                                current_size == last_size):
                                offset_data["sealed"] = True
                                offset_data["offset"] = current_size
                                offset_data["size"] = current_size
                                offset_data["mtime"] = file_mtime
                                self.logger.debug(
                                    f"🔒 [JSONL Monitor] Sealed file {file_key} "
                                    f"(age: {file_age_minutes:.0f}m, size: {current_size:,} bytes) - will skip in future"
                                )
                                self._save_offsets()  # Persist sealed status
                                continue
                            
                            # Skip files that are too large (>50 MB) - mark as sealed
                            file_size_mb = current_size / (1024 * 1024)
                            if file_size_mb > 50:
                                offset_data["sealed"] = True
                                offset_data["offset"] = current_size
                                offset_data["size"] = current_size
                                offset_data["mtime"] = file_mtime
                                self._save_offsets()
                                continue
                            
                            # Validate persisted offset against current file state
                            # ONLY reset offset if file shrinks or mtime goes backwards (file corruption/restore)
                            persisted_mtime = offset_data.get("mtime", 0)
                            if current_size < last_offset:
                                # File shrunk - reset offset (file may have been truncated/restored)
                                self.logger.warning(
                                    f"[JSONL Monitor] File {file_key} shrunk (size {current_size} < offset {last_offset}), resetting offset"
                                )
                                last_offset = 0
                                offset_data["offset"] = 0
                                offset_data["size"] = current_size
                                offset_data["mtime"] = file_mtime
                                self._save_offsets()
                            elif file_mtime < persisted_mtime:
                                # File mtime went backwards - reset offset (file may have been restored)
                                self.logger.warning(
                                    f"[JSONL Monitor] File {file_key} mtime went backwards, resetting offset"
                                )
                                last_offset = 0
                                offset_data["offset"] = 0
                                offset_data["size"] = current_size
                                offset_data["mtime"] = file_mtime
                                self._save_offsets()
                            
                            # Skip if no new data (normal case - file is complete and we've processed it)
                            if current_size <= last_offset:
                                continue
                            
                            # STRICT OFFSET-BASED TAILING
                            # Read only new data from last_offset to current_size
                            # Process in chunks and update offset immediately after each chunk
                            
                            # Read all new data at once
                            with open(jsonl_file, "rb") as f:
                                f.seek(last_offset)
                                new_data = f.read(current_size - last_offset)
                            
                            if not new_data:
                                continue
                            
                            # STRICT OFFSET-BASED TAILING
                            # Process data in chunks, updating offset immediately after each chunk
                            # Once offset advances, those bytes are NEVER reprocessed
                            
                            # Decode all new data and split into lines
                            new_text = new_data.decode("utf-8", errors="ignore")
                            all_lines = new_text.split("\n")
                            
                            # If last character wasn't newline, last line is incomplete (file still growing)
                            # Don't process incomplete line - it will be picked up on next iteration
                            incomplete_last_line = False
                            if new_data and new_data[-1] != ord('\n'):
                                incomplete_last_line = True
                                all_lines = all_lines[:-1]  # Remove incomplete last line
                            
                            # Process complete lines in chunks
                            chunk_size = 100  # Process 100 events at a time
                            ingested_count = 0
                            current_offset = last_offset
                            
                            for chunk_start in range(0, len(all_lines), chunk_size):
                                if not self._running:
                                    break
                                
                                chunk_lines = all_lines[chunk_start:chunk_start + chunk_size]
                                
                                # Process each line in chunk
                                for line in chunk_lines:
                                    if not line.strip():
                                        continue
                                    
                                    try:
                                        event = json.loads(line)
                                        
                                        # Filter by live window (EventBus will also filter, but this saves work)
                                        try:
                                            event_timestamp_str = event.get("timestamp")
                                            if event_timestamp_str:
                                                event_time = datetime.fromisoformat(event_timestamp_str.replace("Z", "+00:00"))
                                                if event_time.tzinfo is None:
                                                    from datetime import timezone
                                                    event_time = event_time.replace(tzinfo=timezone.utc)
                                                
                                                from datetime import timezone
                                                now = datetime.now(timezone.utc)
                                                age_minutes = (now - event_time).total_seconds() / 60
                                                
                                                if age_minutes > self.event_bus.LIVE_EVENT_WINDOW_MINUTES:
                                                    continue
                                        except (ValueError, AttributeError, TypeError):
                                            continue
                                        
                                        # Last-resort deduplication guard (protects against rare race conditions)
                                        # Use stable hash of event content: run_id + stage + event + timestamp + data
                                        event_hash_input = f"{event.get('run_id', '')}|{event.get('stage', '')}|{event.get('event', '')}|{event.get('timestamp', '')}|{json.dumps(event.get('data', {}), sort_keys=True)}"
                                        event_hash = hashlib.md5(event_hash_input.encode()).hexdigest()
                                        dedup_key = (file_key, event_hash)
                                        
                                        # Skip if already seen (rare case - protects against race conditions)
                                        if dedup_key in self._seen_events:
                                            continue
                                        
                                        # Mark as seen (with TTL)
                                        self._seen_events[dedup_key] = datetime.now()
                                        
                                        # Publish to EventBus
                                        try:
                                            await self.event_bus.publish(event)
                                            ingested_count += 1
                                        except Exception as e:
                                            self.logger.warning(f"[JSONL Monitor] Failed to publish event from {file_key} to EventBus: {e}")
                                            continue
                                        
                                    except json.JSONDecodeError:
                                        # Skip invalid JSON lines
                                        continue
                                    except Exception as e:
                                        self.logger.debug(f"Error processing event line: {e}")
                                        continue
                                
                                # Calculate bytes consumed by complete lines processed so far
                                # Find the byte position of the end of the last processed line in original data
                                lines_processed = chunk_start + len(chunk_lines)
                                if lines_processed > 0:
                                    # Reconstruct text up to end of last processed line
                                    processed_lines = all_lines[:lines_processed]
                                    processed_text = "\n".join(processed_lines)
                                    if lines_processed < len(all_lines) or not incomplete_last_line:
                                        processed_text += "\n"  # Include newline after last processed line
                                    
                                    # Calculate exact byte position in original data
                                    # Use the original encoding to match file exactly
                                    bytes_processed = len(processed_text.encode("utf-8"))
                                    new_offset = last_offset + bytes_processed
                                    
                                    # Debug log when offset moves forward
                                    if new_offset > current_offset:
                                        self.logger.debug(
                                            f"[JSONL Monitor] Offset advanced: {file_key} "
                                            f"old={current_offset} new={new_offset} bytes_consumed={bytes_processed}"
                                        )
                                    
                                    # Update offset immediately after processing chunk
                                    # This is the single source of truth - once offset advances, bytes are never reprocessed
                                    offset_data["offset"] = new_offset
                                    offset_data["size"] = current_size
                                    offset_data["mtime"] = file_mtime
                                    self._save_offsets()  # Persist immediately
                                    
                                    # Update current_offset for next chunk
                                    current_offset = new_offset
                                
                                # Yield control after each chunk
                                await asyncio.sleep(0.01)
                            
                            # Update metrics
                            if ingested_count > 0:
                                self._ingestion_metrics["events_ingested"] += ingested_count
                                self.logger.info(
                                    f"✅ [JSONL Monitor] Ingested {ingested_count} new events from {file_key} "
                                    f"(offset: {offset_data.get('offset', last_offset)})"
                                )
                            
                            # Cleanup deduplication cache (expire entries older than 1 hour)
                            now = datetime.now()
                            expired_keys = [
                                key for key, timestamp in self._seen_events.items()
                                if isinstance(timestamp, datetime) and (now - timestamp).total_seconds() > 3600
                            ]
                            for key in expired_keys:
                                del self._seen_events[key]
                            
                            # Also limit total cache size (keep last 50000 entries as fallback)
                            if len(self._seen_events) > 50000:
                                items = list(self._seen_events.items())
                                self._seen_events = dict(items[-25000:])
                            
                        except Exception as e:
                            self.logger.debug(f"Error monitoring {jsonl_file}: {e}")
                            continue
                
                except asyncio.CancelledError:
                    self.logger.info("[JSONL Monitor] Monitor cancelled")
                    print("[JSONL Monitor] Monitor cancelled")
                    break
                except Exception as e:
                    self.logger.error(f"[JSONL Monitor] Monitor loop error: {e}", exc_info=True)
                    print(f"[JSONL Monitor] ERROR in loop: {e}")
                    import traceback
                    print(f"[JSONL Monitor] Traceback: {traceback.format_exc()}")
                    # Small delay before retrying on error
                    await asyncio.sleep(2)
        except Exception as e:
            # Hard catch-all for any fatal errors (startup, imports, etc.)
            self.logger.exception("[JSONL Monitor] FATAL ERROR - Monitor failed to start or crashed")
            print(f"[JSONL Monitor] FATAL ERROR - Monitor failed: {e}")
            import traceback
            print(f"[JSONL Monitor] FATAL Traceback:\n{traceback.format_exc()}")
            # Don't re-raise - we want the orchestrator to continue even if monitor fails
    
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
            # Emit start event here for scheduled runs or direct calls
            await self.event_bus.publish({
                "run_id": run_id,
                "stage": "pipeline",
                "event": "start",
                "timestamp": datetime.now().isoformat(),
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
                    "triggered_at": datetime.now().isoformat()
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
                    "timestamp": datetime.now().isoformat(),
                    "msg": "Scheduled pipeline run started",
                    "data": {"manual": False}
                })
                self.logger.info(f"✅ Scheduler/start event published")
            else:
                self.logger.debug(f"Manual run - skipping scheduler event")
            
            # Small delay to allow frontend to connect WebSocket (only for manual runs)
            if manual:
                await asyncio.sleep(0.1)  # 100ms delay - enough for WebSocket handshake
            
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
                self.logger.info(f"✅ Scheduler/{event_type} event published")
            
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

