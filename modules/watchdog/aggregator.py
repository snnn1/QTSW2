"""
Watchdog Aggregator Service

Main service that coordinates event feed generation, event processing, and state management.
"""
import logging
import asyncio
from typing import Dict, List, Optional
from datetime import datetime, timezone
from collections import defaultdict
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
    
    def get_stream_pnl(
        self,
        trading_date: str,
        stream: Optional[str] = None
    ) -> Dict:
        """
        Get realized P&L for stream(s).
        
        DO NOT inject into get_stream_states() - this is a separate endpoint.
        
        Args:
            trading_date: Trading date (YYYY-MM-DD)
            stream: Optional stream filter
        
        Returns:
            If stream specified: Single stream P&L dict
            If stream None: Dict with "trading_date" and "streams" list
        """
        try:
            from .pnl.ledger_builder import LedgerBuilder
            from .pnl.pnl_calculator import compute_intent_realized_pnl, aggregate_stream_pnl
            
            ledger_builder = LedgerBuilder()
            
            # Build ledger rows
            ledger_rows = ledger_builder.build_ledger_rows(trading_date, stream)
            
            # Calculate P&L for each intent
            for row in ledger_rows:
                compute_intent_realized_pnl(row)
            
            # PHASE 2: Import canonicalization helpers
            from .pnl.ledger_builder import canonicalize_stream, get_canonical_instrument
            
            if stream:
                # PHASE 3: Stream parameter should already be canonical, but defensive canonicalization
                # Ledger rows already have canonical stream IDs from _build_ledger_row()
                execution_instrument = ledger_rows[0].get("execution_instrument", "") if ledger_rows else ""
                if execution_instrument:
                    canonical_stream = canonicalize_stream(stream, execution_instrument)
                else:
                    canonical_stream = stream
                return aggregate_stream_pnl(ledger_rows, canonical_stream)
            else:
                # PHASE 3: Aggregate by canonical stream ID
                # Ledger rows already have canonical stream IDs from _build_ledger_row()
                streams_dict = defaultdict(list)
                for row in ledger_rows:
                    stream_id = row.get("stream", "")  # Already canonicalized in _build_ledger_row()
                    if stream_id:
                        streams_dict[stream_id].append(row)
                
                aggregated_streams = [
                    aggregate_stream_pnl(rows, stream_id)
                    for stream_id, rows in streams_dict.items()
                ]
                
                return {
                    "trading_date": trading_date,
                    "streams": aggregated_streams
                }
        except Exception as e:
            logger.error(f"Error computing stream P&L: {e}", exc_info=True)
            # Return safe defaults
            if stream:
                return {
                    "trading_date": trading_date,
                    "stream": stream,
                    "realized_pnl": 0.0,
                    "open_positions": 0,
                    "total_costs_realized": 0.0,
                    "intent_count": 0,
                    "closed_count": 0,
                    "partial_count": 0,
                    "open_count": 0,
                    "pnl_confidence": "LOW"
                }
            else:
                return {
                    "trading_date": trading_date,
                    "streams": []
                }
    
    async def _process_events_loop(self):
        """Background loop to process new events."""
        cleanup_counter = 0
        while self._running:
            try:
                # Generate new events from raw logs
                processed_count = self._event_feed.process_new_events()
                
                # Read and process new events from frontend_feed.jsonl
                if FRONTEND_FEED_FILE.exists():
                    await self._process_feed_events()
                
                # Periodic cleanup: every 60 seconds, remove stale streams
                cleanup_counter += 1
                if cleanup_counter >= 60:  # Every 60 seconds
                    cleanup_counter = 0
                    self._cleanup_stale_streams_periodic()
                
                # Sleep before next iteration
                await asyncio.sleep(1)  # Process every second
                
            except Exception as e:
                logger.error(f"Error in event processing loop: {e}", exc_info=True)
                await asyncio.sleep(5)  # Wait longer on error
    
    def _cleanup_stale_streams_periodic(self):
        """Periodically clean up stale streams (runs every 60 seconds)."""
        try:
            # Get current trading date from state manager
            current_trading_date = self._state_manager._trading_date
            
            # Fallback: if trading_date not set, use today's date (Chicago timezone)
            if not current_trading_date:
                chicago_now = datetime.now(CHICAGO_TZ)
                current_trading_date = chicago_now.strftime("%Y-%m-%d")
                logger.debug(f"Trading date not set, using today's date: {current_trading_date}")
            
            utc_now = datetime.now(timezone.utc)
            self._state_manager.cleanup_stale_streams(current_trading_date, utc_now)
        except Exception as e:
            logger.warning(f"Error in periodic cleanup: {e}")
    
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
            # Use byte-offset tracking for incremental reading (similar to event_feed.py)
            # Store last read position per run_id to avoid re-reading entire file
            if not hasattr(self, '_feed_file_positions'):
                self._feed_file_positions: Dict[str, int] = {}  # run_id -> byte position
            
            # For each run_id in cursor, read from last known position
            # If run_id not in positions, start from beginning
            run_ids_to_track = set(cursor.keys())
            
            # Also track any new run_ids we encounter
            with open(FRONTEND_FEED_FILE, 'r', encoding='utf-8') as f:
                # If we have positions, seek to the minimum position (earliest unread)
                if self._feed_file_positions:
                    min_pos = min(self._feed_file_positions.values())
                    f.seek(min_pos)
                else:
                    # First time: read from beginning
                    f.seek(0)
                
                # Read new lines since last position
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    
                    try:
                        event = json.loads(line)
                        run_id = event.get("run_id")
                        event_seq = event.get("event_seq", 0)
                        
                        if not run_id:
                            continue
                        
                        # Check if we should include this event
                        last_seq = cursor.get(run_id, 0)
                        if event_seq > last_seq:
                            events.append(event)
                            # Track this run_id
                            run_ids_to_track.add(run_id)
                    
                    except json.JSONDecodeError as e:
                        logger.warning(f"Failed to parse JSON line: {e}")
                        continue
                
                # Update file positions for all tracked run_ids
                current_pos = f.tell()
                for run_id in run_ids_to_track:
                    self._feed_file_positions[run_id] = current_pos
        
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
            # Compute market_open even in error case for consistent UI
            try:
                from .market_session import is_market_open
                chicago_now = datetime.now(CHICAGO_TZ)
                market_open = is_market_open(chicago_now)
            except Exception:
                market_open = False  # Safe default if market session check fails
            
            return {
                "timestamp_chicago": datetime.now(CHICAGO_TZ).isoformat(),
                "engine_alive": False,
                "engine_activity_state": "STALLED",
                "last_engine_tick_chicago": None,
                "engine_tick_stall_detected": True,
                "recovery_state": "UNKNOWN",
                "kill_switch_active": False,
                "connection_status": "Unknown",
                "last_connection_event_chicago": None,
                "stuck_streams": [],
                "execution_blocked_count": 0,
                "protective_failures_count": 0,
                "data_stall_detected": {},
                "market_open": market_open
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
            # Get current trading date (filter out stale streams from previous days)
            current_trading_date = self._state_manager._trading_date
            if not current_trading_date:
                # Fallback: use today's date
                chicago_now = datetime.now(CHICAGO_TZ)
                current_trading_date = chicago_now.strftime("%Y-%m-%d")
            
            if hasattr(self._state_manager, '_stream_states'):
                for (trading_date, stream), info in self._state_manager._stream_states.items():
                    # Only include streams from current trading date
                    if trading_date != current_trading_date:
                        continue
                    
                    streams.append({
                        "trading_date": trading_date,
                        "stream": stream,
                        "instrument": getattr(info, 'instrument', ''),
                        "session": getattr(info, 'session', None) or "",
                        "state": getattr(info, 'state', ''),
                        "committed": getattr(info, 'committed', False),
                        "commit_reason": getattr(info, 'commit_reason', None),
                        "slot_time_chicago": getattr(info, 'slot_time_chicago', None) or "",
                        "slot_time_utc": getattr(info, 'slot_time_utc', None) or "",
                        "range_high": getattr(info, 'range_high', None),
                        "range_low": getattr(info, 'range_low', None),
                        "freeze_close": getattr(info, 'freeze_close', None),
                        "range_invalidated": getattr(info, 'range_invalidated', False),
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
