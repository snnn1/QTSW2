"""
EventLoggerWithBus - Extends EventLogger to also publish to EventBus

This ensures real-time updates to the dashboard without relying solely on the JSONL monitor.
"""

import asyncio
import logging
import sys
from pathlib import Path
from typing import Optional, Dict
from datetime import datetime
import pytz

# Ensure project root is in path for automation imports
qtsw2_root = Path(__file__).parent.parent.parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

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
        # Call parent to write to JSONL
        super().emit(run_id, stage, event, msg, data)
        
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
            loop = self._event_loop
            
            # Try to get running loop in current thread first (works if in main event loop thread)
            try:
                current_loop = asyncio.get_running_loop()
                # If we're in the main event loop thread, use that loop
                loop = current_loop
            except RuntimeError:
                # No loop in current thread - we're in a worker thread
                # Use stored loop reference (captured during initialization in main thread)
                if loop is None:
                    # No event loop available - events will be picked up by JSONL monitor
                    if self.logger:
                        self.logger.debug("No event loop available for EventBus publish (JSONL fallback active)")
                    return
            
            # Check if loop is still valid and running
            # Note: loop.is_running() may not work correctly from a different thread,
            # but call_soon_threadsafe will handle it gracefully
            if loop is not None:
                # Schedule the publish (thread-safe - works from any thread)
                # Use asyncio.run_coroutine_threadsafe for better reliability
                try:
                    # Schedule coroutine directly (more reliable than creating task inside function)
                    future = asyncio.run_coroutine_threadsafe(
                        self.event_bus.publish(event_obj),
                        loop
                    )
                    # Add callback to log errors if publish fails (but don't block)
                    def log_publish_error(fut):
                        try:
                            fut.result()  # This will raise if the coroutine failed
                        except Exception as e:
                            if self.logger:
                                self.logger.debug(f"EventBus publish failed (JSONL fallback active): {e}", exc_info=False)
                    future.add_done_callback(log_publish_error)
                except Exception as e:
                    if self.logger:
                        self.logger.debug(f"Failed to schedule EventBus publish (JSONL fallback active): {e}", exc_info=False)
        except Exception as e:
            # Swallow EventBus failures - never throw from publish
            # Events are still written to JSONL (via super().emit()), so they'll be picked up by the monitor
            # Log at debug level since this is expected in some contexts and JSONL provides fallback
            if self.logger:
                self.logger.debug(f"EventBus publish failed (JSONL fallback active): {e}", exc_info=False)

