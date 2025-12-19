"""
EventLoggerWithBus - Extends EventLogger to also publish to EventBus

This ensures real-time updates to the dashboard without relying solely on the JSONL monitor.
"""

import asyncio
import logging
from pathlib import Path
from typing import Optional, Dict
from datetime import datetime
import pytz

from automation.services.event_logger import EventLogger
from .events import EventBus


class EventLoggerWithBus(EventLogger):
    """
    Extends EventLogger to also publish events directly to the EventBus.
    This ensures real-time updates to the dashboard without relying solely on the JSONL monitor.
    """
    
    def __init__(
        self,
        log_file: Path,
        event_bus: EventBus,
        timezone=pytz.timezone("America/Chicago"),
        logger: Optional[logging.Logger] = None,
        event_loop: Optional[asyncio.AbstractEventLoop] = None
    ):
        super().__init__(log_file, timezone, logger)
        self.event_bus = event_bus
        # Store reference to event loop for cross-thread publishing
        # Services run in asyncio.to_thread(), so we need thread-safe scheduling
        # If event_loop is provided, use it; otherwise try to get it
        if event_loop is not None:
            self._event_loop = event_loop
        else:
            try:
                self._event_loop = asyncio.get_running_loop()
            except RuntimeError:
                # No running loop at init time - will try to get it on each emit
                self._event_loop = None
        if logger:
            logger.debug(f"EventLoggerWithBus initialized for {log_file.name} (loop: {self._event_loop is not None})")

    def emit(
        self,
        run_id: str,
        stage: str,
        event: str,
        msg: Optional[str] = None,
        data: Optional[Dict] = None
    ) -> None:
        """
        Emit a structured event to the log file AND EventBus.
        Never raises exceptions - failures are logged but don't crash pipeline.
        """
        # Call parent to write to JSONL first (this always works)
        super().emit(run_id, stage, event, msg, data)
        
        # Log at info level for important events to help diagnose
        if event in ("start", "success", "failure", "error", "state_change"):
            if self.logger:
                self.logger.info(f"EventLoggerWithBus: Emitting {stage}/{event} (run_id: {run_id[:8] if run_id else 'None'}, event_loop={self._event_loop is not None})")
        
        # Also publish to EventBus for real-time updates
        event_obj = {
            "run_id": run_id,
            "stage": stage,
            "event": event,
            "timestamp": datetime.now(self.timezone).isoformat()
        }
        if msg is not None:
            event_obj["msg"] = msg
        if data is not None:
            event_obj["data"] = data

        try:
            # Try to publish to EventBus for real-time updates
            # Services run in asyncio.to_thread() (separate thread), so we need thread-safe scheduling
            loop = None
            
            # Try to get running loop in current thread first (works if in main event loop thread)
            try:
                loop = asyncio.get_running_loop()
            except RuntimeError:
                # No loop in current thread - use stored loop reference if available
                loop = self._event_loop
            
            if loop is not None and loop.is_running():
                # Schedule the publish (thread-safe - works from any thread)
                # Use a simpler approach: create the task directly via call_soon_threadsafe
                async def publish_event():
                    try:
                        await self.event_bus.publish(event_obj)
                    except Exception as e:
                        if self.logger:
                            self.logger.warning(f"EventBus publish failed for {event_obj.get('stage')}/{event_obj.get('event')}: {e}", exc_info=False)
                
                # Schedule the coroutine as a task (thread-safe)
                try:
                    # Create task in the event loop thread
                    def schedule_task():
                        try:
                            task = loop.create_task(publish_event())
                            # Add done callback to catch any errors
                            def log_error(task):
                                try:
                                    task.result()
                                except Exception as e:
                                    if self.logger:
                                        self.logger.warning(f"EventBus publish task error for {event_obj.get('stage')}/{event_obj.get('event')}: {e}", exc_info=False)
                            task.add_done_callback(log_error)
                        except Exception as e:
                            if self.logger:
                                self.logger.warning(f"Failed to create EventBus publish task for {event_obj.get('stage')}/{event_obj.get('event')}: {e}", exc_info=False)
                    
                    loop.call_soon_threadsafe(schedule_task)
                    # Log successful scheduling for important events
                    if event in ("start", "success", "failure", "error") and self.logger:
                        self.logger.info(f"EventBus publish scheduled for {stage}/{event} (run_id: {run_id[:8] if run_id else 'None'})")
                except RuntimeError as e:
                    # Loop might have been closed - log but don't fail
                    if self.logger:
                        self.logger.warning(f"Event loop not available for publishing {stage}/{event} (JSONL fallback active): {e}")
                except Exception as e:
                    if self.logger:
                        self.logger.warning(f"Failed to schedule EventBus publish for {stage}/{event}: {e}", exc_info=False)
            else:
                # No event loop available - events will be picked up by JSONL monitor
                # Log at info level for important events to help diagnose
                if self.logger and event in ("start", "success", "failure", "error"):
                    self.logger.info(f"Event loop not available - {stage}/{event} will be picked up by JSONL monitor (written to {self.log_file.name})")
        except Exception as e:
            # Swallow EventBus failures - never throw from publish
            # Events are still written to JSONL (via super().emit()), so they'll be picked up by the monitor
            # Log at warning level to help diagnose issues
            if self.logger:
                self.logger.warning(f"EventBus publish failed (JSONL fallback active): {e}", exc_info=False)

