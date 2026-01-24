"""
Watchdog Aggregator Service

Main service that coordinates event feed generation, event processing, and state management.
"""
import logging
import asyncio
from typing import Dict, List, Optional
from datetime import datetime, timezone
import pytz

from .event_feed import EventFeedGenerator
from .event_processor import EventProcessor
from .state_manager import WatchdogStateManager, CursorManager
from .config import FRONTEND_FEED_FILE

logger = logging.getLogger(__name__)

CHICAGO_TZ = pytz.timezone("America/Chicago")


class WatchdogAggregator:
    """Main aggregator service."""
    
    def __init__(self):
        self._event_feed = EventFeedGenerator()
        self._state_manager = WatchdogStateManager()
        self._event_processor = EventProcessor(self._state_manager)
        self._cursor_manager = CursorManager()
        self._running = False
    
    async def start(self):
        """Start the aggregator service."""
        self._running = True
        logger.info("Watchdog aggregator started")
        
        # Load cursor state
        cursor = self._cursor_manager.load_cursor()
        logger.info(f"Loaded cursor state: {cursor}")
        
        # Start background task for processing events
        asyncio.create_task(self._process_events_loop())
    
    async def stop(self):
        """Stop the aggregator service."""
        self._running = False
        logger.info("Watchdog aggregator stopped")
    
    async def _process_events_loop(self):
        """Background loop to process new events."""
        while self._running:
            try:
                # Generate new events from raw logs
                processed_count = self._event_feed.process_new_events()
                
                # Read and process new events from frontend_feed.jsonl
                if FRONTEND_FEED_FILE.exists():
                    await self._process_feed_events()
                
                # Sleep before next iteration
                await asyncio.sleep(1)  # Process every second
                
            except Exception as e:
                logger.error(f"Error in event processing loop: {e}", exc_info=True)
                await asyncio.sleep(5)  # Wait longer on error
    
    async def _process_feed_events(self):
        """Process new events from frontend_feed.jsonl."""
        try:
            # Load cursor to know where we left off
            cursor = self._cursor_manager.load_cursor()
            
            # Read events from feed file
            events = self._read_feed_events_since(cursor)
            
            # Process each event
            for event in events:
                self._event_processor.process_event(event)
            
            # Update cursor
            if events:
                last_run_id = events[-1].get("run_id")
                last_seq = events[-1].get("event_seq", 0)
                if last_run_id:
                    cursor[last_run_id] = last_seq
                    self._cursor_manager.save_cursor(cursor)
        
        except Exception as e:
            logger.error(f"Error processing feed events: {e}", exc_info=True)
    
    def _read_feed_events_since(self, cursor: Dict[str, int]) -> List[Dict]:
        """Read events from frontend_feed.jsonl since cursor position."""
        import json
        
        events = []
        
        if not FRONTEND_FEED_FILE.exists():
            return events
        
        try:
            with open(FRONTEND_FEED_FILE, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    
                    try:
                        event = json.loads(line)
                        run_id = event.get("run_id")
                        event_seq = event.get("event_seq", 0)
                        
                        # Check if we should include this event
                        last_seq = cursor.get(run_id, 0)
                        if event_seq > last_seq:
                            events.append(event)
                    
                    except json.JSONDecodeError as e:
                        logger.warning(f"Failed to parse JSON line: {e}")
                        continue
        
        except Exception as e:
            logger.error(f"Error reading feed file: {e}")
        
        return events
    
    def get_events_since(self, run_id: str, since_seq: int) -> List[Dict]:
        """Get events since event_seq for a run_id."""
        import json
        
        events = []
        
        if not FRONTEND_FEED_FILE.exists():
            return events
        
        try:
            with open(FRONTEND_FEED_FILE, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    
                    try:
                        event = json.loads(line)
                        event_run_id = event.get("run_id")
                        event_seq = event.get("event_seq", 0)
                        
                        if event_run_id == run_id and event_seq > since_seq:
                            events.append(event)
                    
                    except json.JSONDecodeError:
                        continue
        
        except Exception as e:
            logger.error(f"Error reading feed file: {e}")
        
        return events
    
    def get_watchdog_status(self) -> Dict:
        """Get current watchdog status."""
        try:
            status = self._state_manager.compute_watchdog_status()
            status["timestamp_chicago"] = datetime.now(CHICAGO_TZ).isoformat()
            return status
        except Exception as e:
            logger.error(f"Error computing watchdog status: {e}", exc_info=True)
            # Return minimal safe status
            return {
                "timestamp_chicago": datetime.now(CHICAGO_TZ).isoformat(),
                "engine_alive": False,
                "last_engine_tick_chicago": None,
                "engine_tick_stall_detected": True,
                "recovery_state": "UNKNOWN",
                "kill_switch_active": False,
                "connection_status": "Unknown",
                "last_connection_event_chicago": None,
                "stuck_streams": [],
                "execution_blocked_count": 0,
                "protective_failures_count": 0,
                "data_stall_detected": {}
            }
    
    def get_risk_gate_status(self) -> Dict:
        """Get current risk gate status."""
        try:
            status = self._state_manager.compute_risk_gate_status()
            status["timestamp_chicago"] = datetime.now(CHICAGO_TZ).isoformat()
            return status
        except Exception as e:
            logger.error(f"Error computing risk gate status: {e}", exc_info=True)
            # Return minimal safe status
            return {
                "timestamp_chicago": datetime.now(CHICAGO_TZ).isoformat(),
                "recovery_state_allowed": False,
                "kill_switch_allowed": False,
                "timetable_validated": False,
                "stream_armed": [],
                "session_slot_time_valid": False,
                "trading_date_set": False
            }
    
    def get_unprotected_positions(self) -> Dict:
        """Get current unprotected positions."""
        try:
            positions = self._state_manager.compute_unprotected_positions()
            return {
                "timestamp_chicago": datetime.now(CHICAGO_TZ).isoformat(),
                "unprotected_positions": positions
            }
        except Exception as e:
            logger.error(f"Error computing unprotected positions: {e}", exc_info=True)
            return {
                "timestamp_chicago": datetime.now(CHICAGO_TZ).isoformat(),
                "unprotected_positions": []
            }
    
    def get_current_run_id(self) -> Optional[str]:
        """Get current engine run_id."""
        run_id = self._event_feed.get_current_run_id()
        if not run_id:
            # Try to get from cursor
            cursor = self._cursor_manager.load_cursor()
            if cursor:
                # Return run_id with highest seq (most recent)
                run_id = max(cursor.items(), key=lambda x: x[1])[0] if cursor else None
        return run_id
    
    def get_stream_states(self) -> Dict:
        """Get current stream states."""
        streams = []
        try:
            if hasattr(self._state_manager, '_stream_states'):
                for (trading_date, stream), info in self._state_manager._stream_states.items():
                    streams.append({
                        "trading_date": trading_date,
                        "stream": stream,
                        "instrument": getattr(info, 'instrument', ''),
                        "session": "",  # TODO: Extract from events
                        "state": getattr(info, 'state', ''),
                        "committed": getattr(info, 'committed', False),
                        "commit_reason": getattr(info, 'commit_reason', None),
                        "slot_time_chicago": "",  # TODO: Extract from events
                        "slot_time_utc": "",  # TODO: Extract from events
                        "range_high": None,  # TODO: Extract from events
                        "range_low": None,  # TODO: Extract from events
                        "freeze_close": None,  # TODO: Extract from events
                        "range_invalidated": False,  # TODO: Extract from events
                        "state_entry_time_utc": getattr(info, 'state_entry_time_utc', datetime.now(timezone.utc)).isoformat()
                    })
        except Exception as e:
            logger.error(f"Error getting stream states: {e}", exc_info=True)
        return {
            "timestamp_chicago": datetime.now(CHICAGO_TZ).isoformat(),
            "streams": streams
        }
    
    def get_active_intents(self) -> Dict:
        """Get current active intents."""
        intents = []
        try:
            if hasattr(self._state_manager, '_intent_exposures'):
                for intent_id, exposure in self._state_manager._intent_exposures.items():
                    if getattr(exposure, 'state', '') == "ACTIVE":
                        entry_filled_qty = getattr(exposure, 'entry_filled_qty', 0)
                        exit_filled_qty = getattr(exposure, 'exit_filled_qty', 0)
                        entry_filled_at_utc = getattr(exposure, 'entry_filled_at_utc', None)
                        intents.append({
                            "intent_id": intent_id,
                            "stream_id": getattr(exposure, 'stream_id', ''),
                            "instrument": getattr(exposure, 'instrument', ''),
                            "direction": getattr(exposure, 'direction', ''),
                            "quantity": entry_filled_qty + exit_filled_qty,  # Total quantity
                            "entry_filled_qty": entry_filled_qty,
                            "exit_filled_qty": exit_filled_qty,
                            "remaining_exposure": entry_filled_qty - exit_filled_qty,
                            "state": getattr(exposure, 'state', ''),
                            "entry_filled_at_chicago": (
                                entry_filled_at_utc.astimezone(CHICAGO_TZ).isoformat()
                                if entry_filled_at_utc else None
                            )
                        })
        except Exception as e:
            logger.error(f"Error getting active intents: {e}", exc_info=True)
        return {
            "timestamp_chicago": datetime.now(CHICAGO_TZ).isoformat(),
            "intents": intents
        }
