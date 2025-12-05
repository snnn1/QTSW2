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
    In-process event bus with:
    - Publish events
    - Subscribe to events (async iterator)
    - Ring buffer of recent events
    - JSONL file writing
    - WebSocket broadcasting
    """
    
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
    
    async def publish(self, event: Dict):
        """
        Publish an event to all subscribers and write to file.
        
        Args:
            event: Event dictionary with run_id, stage, event, timestamp, msg, data
        """
        # Ensure timestamp
        if "timestamp" not in event:
            event["timestamp"] = datetime.now().isoformat()
        
        # Add to ring buffer
        self._recent_events.append(event)
        
        # Write to JSONL file
        run_id = event.get("run_id", "unknown")
        event_log_file = self.event_logs_dir / f"pipeline_{run_id}.jsonl"
        
        try:
            with open(event_log_file, "a", encoding="utf-8") as f:
                f.write(json.dumps(event) + "\n")
        except Exception as e:
            self.logger.error(f"Failed to write event to file: {e}")
        
        # Broadcast to all subscribers
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
            while True:
                try:
                    event = await queue.get()
                    yield event
                except asyncio.CancelledError:
                    break
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

