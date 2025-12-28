"""
Event Bus - Centralized event publishing and subscription
"""

import asyncio
import json
import logging
from pathlib import Path
from typing import Dict, List, Optional, AsyncIterator
from datetime import datetime
from collections import deque


class EventBus:
    """
    Event bus for publishing and subscribing to pipeline events.
    
    ARCHITECTURE: EventBus is a LIVE CHANNEL, not a historical replay system.
    - Only publishes events within the LIVE_EVENT_WINDOW (15 minutes)
    - Older events exist in JSONL but are NEVER published to EventBus
    - JSONL is the authoritative historical store
    - EventBus is for real-time monitoring only
    
    Features:
    - Publish events (writes to JSONL + broadcasts to subscribers IF within live window)
    - Subscribe to events (async iterator for WebSocket - live stream only)
    - Ring buffer of recent events (for fast snapshot)
    - JSONL file writing (persistent, survives restarts - all events)
    - Historical events via load_jsonl_events_since() utility (NOT via EventBus)
    """
    
    # Events to skip from file logging (too frequent, not needed for history)
    # These are still broadcast to WebSocket for real-time monitoring
    VERBOSE_EVENTS = {
        'metric',           # Progress updates (every few seconds)
        'progress',         # Progress indicators
        'heartbeat',        # Keepalive messages
        'file_start',       # Individual file processing starts (too many files)
        'file_finish',      # Individual file processing finishes (too many files)
    }
    
    # Events to always keep in file (important for history)
    IMPORTANT_EVENTS = {
        'start',            # Stage/pipeline started
        'success',          # Stage/pipeline completed
        'failed',           # Stage/pipeline failed
        'error',            # Errors occurred
        'state_change',     # State transitions
        # 'log' - Only keep log events for pipeline/scheduler stages
    }
    
    # Stages to always log (even if event type is normally filtered)
    ALWAYS_LOG_STAGES = {'pipeline', 'scheduler'}
    
    # LIVE EVENT WINDOW: Events older than this are NEVER published to EventBus
    # This is an architectural boundary, not a workaround
    # JSONL remains the authoritative historical store
    LIVE_EVENT_WINDOW_MINUTES = 15  # Maximum age for live events (15 minutes)
    
    # Event types allowed on EventBus (live channel only - excludes verbose events)
    # Progress, metrics, file-level, translator/analyzer internals must never go to EventBus
    LIVE_EVENT_TYPES = {
        "scheduler/start",
        "scheduler/success", 
        "scheduler/failed",
        "pipeline/start",
        "pipeline/state_change",
        "pipeline/success",
        "pipeline/failed",
        "translator/start",
        "translator/success",
        "translator/failed",
        "analyzer/start",
        "analyzer/success",
        "analyzer/failed",
        "merger/start",
        "merger/success",
        "merger/failed",
        "error",  # Critical errors (stage-agnostic)
        # Note: system/heartbeat removed - heartbeats are for backend health only, not displayed in UI
    }
    
    # File size limits
    MAX_FILE_SIZE_MB = 100  # Rotate files at 100 MB
    MAX_FILE_SIZE_BYTES = MAX_FILE_SIZE_MB * 1024 * 1024
    
    def __init__(
        self,
        event_logs_dir: Path,
        buffer_size: int = 1000,
        logger: Optional[logging.Logger] = None
    ):
        self.event_logs_dir = event_logs_dir
        self.buffer_size = buffer_size
        self.logger = logger or logging.getLogger(__name__)
        
        # Ring buffer for recent events
        self._recent_events: deque = deque(maxlen=buffer_size)
        
        # Subscribers (queues for each subscriber)
        self._subscribers: List[asyncio.Queue] = []
        self._subscriber_lock = asyncio.Lock()
        
        # Ensure event logs directory exists
        self.event_logs_dir.mkdir(parents=True, exist_ok=True)
        
        # Archive directory for rotated files
        self.archive_dir = self.event_logs_dir / "archive"
        self.archive_dir.mkdir(parents=True, exist_ok=True)
    
    async def publish(self, event: Dict):
        """
        Publish an event to all subscribers and write to file.
        
        ARCHITECTURE ENFORCEMENT:
        - EventBus is a LIVE CHANNEL only (events older than LIVE_EVENT_WINDOW are rejected)
        - All events are written to JSONL (historical storage)
        - Only recent events within live window are broadcast to subscribers
        - Only specific event types are allowed on EventBus (excludes verbose events)
        
        Args:
            event: Event dictionary with run_id, stage, event, timestamp, msg, data
        
        Raises:
            ValueError: If run_id is missing or invalid
        """
        # Validation: run_id is required, but allow None for system-level events (convert to "__system__")
        run_id = event.get("run_id")
        if run_id is None:
            # System-level events (scheduler lifecycle, etc.) use "__system__" as run_id
            run_id = "__system__"
            event["run_id"] = run_id
        elif not run_id or run_id == "unknown":
            self.logger.warning(
                f"Event published without valid run_id: {event.get('stage', 'unknown')}/{event.get('event', 'unknown')}. "
                f"This violates strict run-bound logging. Event will be rejected."
            )
            raise ValueError("Event must include a valid run_id. No logs without active run.")
        
        # Ensure timestamp
        if "timestamp" not in event:
            event["timestamp"] = datetime.now().isoformat()
        
        # ARCHITECTURE BOUNDARY: Check if event is within live window
        # Exception: scheduler events are always included (they're important for UI visibility)
        event_stage = event.get("stage", "")
        is_scheduler_event = event_stage == "scheduler"
        
        if not is_scheduler_event:
            # For non-scheduler events, apply live window filter
            try:
                event_timestamp_str = event.get("timestamp")
                if event_timestamp_str:
                    event_time = datetime.fromisoformat(event_timestamp_str.replace("Z", "+00:00"))
                    # Handle timezone-aware vs naive
                    if event_time.tzinfo is None:
                        event_time = event_time.replace(tzinfo=timezone.utc)
                    
                    # Calculate age
                    now = datetime.now(event_time.tzinfo) if event_time.tzinfo else datetime.now()
                    if event_time.tzinfo:
                        from datetime import timezone
                        now = datetime.now(timezone.utc)
                    age_minutes = (now - event_time).total_seconds() / 60
                    
                    # REJECT events outside live window
                    if age_minutes > self.LIVE_EVENT_WINDOW_MINUTES:
                        # Event is too old for live channel - write to JSONL only (historical storage)
                        # This is the architectural boundary: history goes to JSONL, not EventBus
                        self._write_to_jsonl_only(event)
                        return  # Do NOT broadcast to subscribers
            except (ValueError, AttributeError, TypeError) as e:
                # If timestamp parsing fails, reject the event (safer to drop than flood)
                self.logger.warning(f"Event has invalid timestamp, rejecting from EventBus: {e}")
                self._write_to_jsonl_only(event)
                return
        # Scheduler events bypass age filter (always included for UI visibility)
        
        # ARCHITECTURE BOUNDARY: Only allow specific event types on EventBus
        # CRITICAL: Scheduler events bypass ALL filtering - they are control-plane events
        stage = event.get("stage", "")
        event_type = event.get("event", "")
        event_key = f"{stage}/{event_type}"
        
        # CRITICAL: Scheduler events are ALWAYS allowed - no filtering, no exceptions
        # They are control-plane events and must appear in real-time
        if is_scheduler_event:
            # Scheduler events bypass all filters - always broadcast
            is_allowed = True
        else:
            # Special case: "error" events are stage-agnostic and allowed from any stage
            # Check both stage-specific and stage-agnostic patterns
            is_allowed = (
                event_key in self.LIVE_EVENT_TYPES or
                (event_type == "error" and "error" in self.LIVE_EVENT_TYPES)
            )
        
        if not is_allowed:
            # Event type not allowed on live channel - write to JSONL only
            self._write_to_jsonl_only(event)
            return  # Do NOT broadcast to subscribers
        
        # Event passes all filters - add to ring buffer and broadcast
        # Add to ring buffer (only live events - for WebSocket snapshot)
        self._recent_events.append(event)
        
        # Broadcast to all subscribers (live channel only)
        async with self._subscriber_lock:
            disconnected = []
            for queue in self._subscribers:
                try:
                    queue.put_nowait(event)
                except asyncio.QueueFull:
                    # Queue full - drop oldest and add new
                    try:
                        queue.get_nowait()
                        queue.put_nowait(event)
                    except asyncio.QueueEmpty:
                        pass
                except Exception:
                    disconnected.append(queue)
            
            # Remove disconnected subscribers
            for queue in disconnected:
                    if queue in self._subscribers:
                        self._subscribers.remove(queue)
        
        # Write to JSONL file (historical storage - all events that pass filters)
        self._write_to_jsonl_file(event, run_id, stage, event_type)
    
    def _write_to_jsonl_only(self, event: Dict):
        """
        Write event to JSONL file only (historical storage), NOT to EventBus.
        
        This is used for events that are:
        - Outside the live window (too old)
        - Not allowed on EventBus (verbose events)
        - Historical only (not live-relevant)
        """
        run_id = event.get("run_id", "__system__")
        stage = event.get("stage", "")
        event_type = event.get("event", "")
        self._write_to_jsonl_file(event, run_id, stage, event_type)
    
    def _should_log_event(self, stage: str, event_type: str) -> bool:
        """
        Determine if an event should be logged to JSONL file.
        
        Rules (in order of precedence):
        1. Always log pipeline/scheduler stage events (regardless of type)
        2. Always log important events (start, success, failed, error, state_change)
        3. Never log 'log' events for non-pipeline/scheduler stages
        4. Never log verbose events (metric, progress, heartbeat, file_start, file_finish)
        
        Args:
            stage: Event stage (e.g., 'pipeline', 'translator', 'scheduler')
            event_type: Event type (e.g., 'start', 'success', 'metric', 'log')
        
        Returns:
            True if event should be logged, False otherwise
        """
        # Rule 1: Always log pipeline/scheduler events
        if stage in self.ALWAYS_LOG_STAGES:
            return True
        
        # Rule 2: Log important events
        if event_type in self.IMPORTANT_EVENTS:
            return True
        
        # Rule 3: Never log 'log' events for other stages
        if event_type == 'log':
            return False
        
        # Rule 4: Never log verbose events
        if event_type in self.VERBOSE_EVENTS:
            return False
        
        # Default: don't log
        return False
    
    def _write_to_jsonl_file(self, event: Dict, run_id: str, stage: str, event_type: str):
        """
        Write event to JSONL file (historical storage).
        
        This is the authoritative historical store - all loggable events go here,
        regardless of whether they were published to EventBus or not.
        """
        # Use simplified filtering logic
        if not self._should_log_event(stage, event_type):
            return
        
        # Write to JSONL file
        event_log_file = self.event_logs_dir / f"pipeline_{run_id}.jsonl"
        
        # Check file size and rotate if needed
        if event_log_file.exists():
            current_size = event_log_file.stat().st_size
            if current_size >= self.MAX_FILE_SIZE_BYTES:
                # Rotate to archive
                timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
                archive_path = self.archive_dir / f"pipeline_{run_id}_{timestamp}.jsonl"
                try:
                    event_log_file.rename(archive_path)
                    self.logger.info(
                        f"📁 Rotated large file: {event_log_file.name} "
                        f"({current_size / (1024*1024):.2f} MB) → archive/"
                    )
                    # Create new file for this run
                    event_log_file = self.event_logs_dir / f"pipeline_{run_id}.jsonl"
                except Exception as e:
                    self.logger.error(f"Failed to rotate file: {e}")
        
        try:
            with open(event_log_file, "a", encoding="utf-8") as f:
                f.write(json.dumps(event) + "\n")
        except Exception as e:
            self.logger.error(f"Failed to write event to file: {e}")
    
    async def subscribe(self) -> AsyncIterator[Dict]:
        """
        Subscribe to events.
        
        Returns:
            Async iterator of events
        """
        queue = asyncio.Queue(maxsize=100)
        
        async with self._subscriber_lock:
            self._subscribers.append(queue)
        
        try:
            # Send recent events first
            for event in list(self._recent_events):
                try:
                    queue.put_nowait(event)
                except asyncio.QueueFull:
                    break
            
            # Stream new events
            # CRITICAL: No background task is allowed to bring down the WebSocket or backend process.
            # All exceptions are caught, logged, and the loop continues.
            while True:
                try:
                    event = await queue.get()
                    yield event
                except asyncio.CancelledError:
                    break
                except Exception as e:
                    # CRITICAL: Catch all exceptions, log, continue - never crash WebSocket connection
                    self.logger.error(f"[EventBus] Error in subscribe loop: {e}", exc_info=True)
                    # Continue loop - don't break, don't crash
                    # Note: Can't emit error event here (would create infinite loop)
                    # Just log and continue - wait for next event
                    continue
        finally:
            # Cleanup
            async with self._subscriber_lock:
                if queue in self._subscribers:
                    self._subscribers.remove(queue)
    
    def get_recent_events(self, limit: int = 100) -> List[Dict]:
        """
        Get recent events (for snapshots/reconnects).
        
        Args:
            limit: Maximum number of events to return
        
        Returns:
            List of recent events (most recent first)
        """
        return list(self._recent_events)[-limit:]
    
    def get_events_for_run(self, run_id: str, limit: int = 1000) -> List[Dict]:
        """
        Get events for a specific run from file.
        
        Args:
            run_id: Run ID
            limit: Maximum number of events to return
        
        Returns:
            List of events for the run
        """
        event_log_file = self.event_logs_dir / f"pipeline_{run_id}.jsonl"
        
        if not event_log_file.exists():
            return []
        
        events = []
        try:
            with open(event_log_file, "r", encoding="utf-8") as f:
                lines = f.readlines()
                # Get last N lines
                for line in lines[-limit:]:
                    if line.strip():
                        try:
                            event = json.loads(line)
                            events.append(event)
                        except json.JSONDecodeError:
                            continue
        except Exception as e:
            self.logger.error(f"Failed to read events for run {run_id}: {e}")
        
        return events
    
    def load_jsonl_events_since(self, hours: int = 24, max_events: int = 10000, exclude_verbose: bool = True) -> List[Dict]:
        """
        Utility method for reading JSONL history. Not part of live EventBus semantics.
        
        Load all events from JSONL files within the last N hours.
        This scans all pipeline_*.jsonl files in event_logs_dir, parses timestamps,
        filters by time window, and returns events sorted chronologically.
        
        This is a utility method for snapshot loading (e.g., WebSocket 24-hour snapshot).
        It does NOT interact with the live EventBus subscription system.
        
        Args:
            hours: Number of hours to look back (default: 24)
            max_events: Maximum number of events to return (safety limit, default: 10000)
            exclude_verbose: If True, exclude verbose events (metric, progress, etc.) from snapshot (default: True)
        
        Returns:
            List of events sorted chronologically (oldest first)
        """
        from datetime import datetime, timedelta, timezone
        
        # Safety: snapshot loading must never allocate unbounded memory
        # We cap at max_events and return early if limit is reached
        cutoff_time = datetime.now(timezone.utc) - timedelta(hours=hours)
        all_events = []
        
        # Scan all JSONL files matching pattern
        jsonl_files = list(self.event_logs_dir.glob("pipeline_*.jsonl"))
        
        # Performance optimization: Sort by modification time (newest first) and limit file scanning
        # This way we can stop early if we've found enough recent events
        jsonl_files.sort(key=lambda f: f.stat().st_mtime, reverse=True)
        
        # Limit to checking most recent 50 files for performance (if there are many files)
        # Most recent events are in the newest files, so this is safe
        if len(jsonl_files) > 50:
            jsonl_files = jsonl_files[:50]
            self.logger.debug(f"Loading events from last {hours} hours - scanning {len(jsonl_files)} most recent JSONL files (out of {len(list(self.event_logs_dir.glob('pipeline_*.jsonl')))})")
        else:
            self.logger.debug(f"Loading events from last {hours} hours - scanning {len(jsonl_files)} JSONL files")
        
        for jsonl_file in jsonl_files:
            try:
                # Skip archive directory files
                if "archive" in str(jsonl_file):
                    continue
                
                # Read file line by line (handles large files efficiently)
                with open(jsonl_file, "r", encoding="utf-8") as f:
                    for line_num, line in enumerate(f, start=1):
                        if not line.strip():
                            continue
                        
                        try:
                            event = json.loads(line)
                            
                            # Skip verbose events in snapshots (makes UI cleaner)
                            # These events are still available in live streaming
                            if exclude_verbose and event.get("event") in self.VERBOSE_EVENTS:
                                # Check if this is an important stage (always include scheduler/pipeline events)
                                if event.get("stage") not in self.ALWAYS_LOG_STAGES:
                                    continue
                            
                            # Parse timestamp
                            timestamp_str = event.get("timestamp")
                            if not timestamp_str:
                                continue
                            
                            # Parse ISO timestamp (handle both with and without timezone)
                            try:
                                # Convert Z suffix to +00:00 for fromisoformat
                                if timestamp_str.endswith("Z"):
                                    timestamp_str = timestamp_str[:-1] + "+00:00"
                                
                                event_time = datetime.fromisoformat(timestamp_str)
                                
                                # Ensure timezone-aware
                                if event_time.tzinfo is None:
                                    # Assume UTC if no timezone
                                    event_time = event_time.replace(tzinfo=timezone.utc)
                                
                                # Filter by time window
                                if event_time >= cutoff_time:
                                    all_events.append(event)
                                    
                                    # Safety limit check
                                    if len(all_events) >= max_events:
                                        self.logger.warning(
                                            f"Event snapshot hit safety limit ({max_events} events). "
                                            f"Consider reducing time window or increasing limit."
                                        )
                                        # Sort and return early if limit reached
                                        all_events.sort(key=lambda e: e.get("timestamp", ""))
                                        return all_events
                                        
                            except (ValueError, AttributeError) as e:
                                # Skip events with unparseable timestamps
                                self.logger.debug(f"Skipping event with invalid timestamp in {jsonl_file.name} line {line_num}: {e}")
                                continue
                                
                        except json.JSONDecodeError as e:
                            # Skip malformed JSON lines (log warning, continue)
                            self.logger.warning(f"Malformed JSON in {jsonl_file.name} line {line_num}: {e}")
                            continue
                        except Exception as e:
                            # Skip other errors (file partially written, etc.)
                            self.logger.debug(f"Error parsing event in {jsonl_file.name} line {line_num}: {e}")
                            continue
                            
            except FileNotFoundError:
                # File was deleted/moved while reading - skip it
                continue
            except Exception as e:
                # File read error (permission, etc.) - log and continue
                self.logger.warning(f"Error reading {jsonl_file.name}: {e}")
                continue
        
        # Sort events chronologically (oldest first)
        all_events.sort(key=lambda e: e.get("timestamp", ""))
        
        self.logger.info(f"Loaded {len(all_events)} events from last {hours} hours")
        return all_events