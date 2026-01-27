"""
Watchdog Aggregator Service

Main service that coordinates event feed generation, event processing, and state management.
"""
import logging
import asyncio
from typing import Dict, List, Optional
from datetime import datetime, timezone
from collections import defaultdict, deque
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
        
        # In-memory ring buffer for important WebSocket events
        # Buffer size: 200 events (configurable)
        self._important_events_buffer: deque = deque(maxlen=200)
        self._event_seq_counter: int = 0  # Monotonic sequence ID counter
    
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
        import concurrent.futures
        executor = concurrent.futures.ThreadPoolExecutor(max_workers=1)
        
        while self._running:
            try:
                # Generate new events from raw logs (run in thread pool to avoid blocking event loop)
                # This reads large log files synchronously, so we need to offload it
                loop = asyncio.get_event_loop()
                processed_count = await loop.run_in_executor(
                    executor,
                    self._event_feed.process_new_events
                )
                
                # Read and process new events from frontend_feed.jsonl
                # Run in thread pool to avoid blocking event loop (file is large: ~1GB)
                if FRONTEND_FEED_FILE.exists():
                    await loop.run_in_executor(executor, self._process_feed_events_sync)
                
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
    
    def _process_feed_events_sync(self):
        """Process new events from frontend_feed.jsonl (synchronous, runs in thread pool)."""
        try:
            # Load cursor to know where we left off
            cursor = self._cursor_manager.load_cursor()
            
            # Check if state manager is empty - if so, rebuild state from most recent events
            # This handles the case where streams were cleared but events were already processed
            if len(self._state_manager._stream_states) == 0:
                logger.info("State manager is empty - rebuilding stream states from most recent events")
                self._rebuild_stream_states_from_recent_events()
            
            # Normal incremental processing - read new events since cursor
            events = self._read_feed_events_since(cursor)
            
            # Process each event
            for event in events:
                self._event_processor.process_event(event)
                # Add important events to ring buffer for WebSocket streaming
                self._add_to_ring_buffer_if_important(event)
            
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
            # Use utf-8-sig to handle UTF-8 BOM markers
            # Use binary mode to track positions accurately
            with open(FRONTEND_FEED_FILE, 'rb') as f:
                # If we have positions, seek to the minimum position (earliest unread)
                if self._feed_file_positions:
                    min_pos = min(self._feed_file_positions.values())
                    f.seek(min_pos)
                else:
                    # First time: read from beginning
                    f.seek(0)
                
                # Read new lines since last position
                parse_errors = 0
                for line_bytes in f:
                    # Track position before processing line
                    line_start_pos = f.tell() - len(line_bytes)
                    
                    try:
                        line = line_bytes.decode('utf-8-sig').strip()
                    except UnicodeDecodeError:
                        parse_errors += 1
                        continue
                    
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
                        
                        # Update position for this run_id (position after this line)
                        self._feed_file_positions[run_id] = f.tell()
                    
                    except json.JSONDecodeError:
                        # Silently skip malformed JSON lines
                        parse_errors += 1
                        continue
                
                # Log parse errors only once per read, not per line
                if parse_errors > 0:
                    logger.debug(f"Skipped {parse_errors} malformed JSON lines in feed file")
        
        except Exception as e:
            logger.error(f"Error reading feed file: {e}")
        
        return events
    
    def _rebuild_stream_states_from_recent_events(self):
        """
        Rebuild stream states by finding the most recent state for each stream.
        This is more efficient than reprocessing all events - we just find the latest state.
        """
        import json
        from collections import defaultdict
        
        if not FRONTEND_FEED_FILE.exists():
            logger.warning("Feed file does not exist, cannot rebuild stream states")
            return
        
        try:
            # Track the most recent state for each (trading_date, stream) pair
            # Key: (trading_date, stream), Value: (event, timestamp)
            latest_states: Dict[tuple, tuple] = {}
            
            # Read recent events (last 5000 lines should be enough to find current states)
            with open(FRONTEND_FEED_FILE, 'r', encoding='utf-8-sig') as f:
                # Read last N lines
                all_lines = f.readlines()
                recent_lines = all_lines[-5000:] if len(all_lines) > 5000 else all_lines
                
                for line in recent_lines:
                    line = line.strip()
                    if not line:
                        continue
                    
                    try:
                        event = json.loads(line)
                        event_type = event.get("event_type")
                        
                        # Only process STREAM_STATE_TRANSITION events
                        if event_type != "STREAM_STATE_TRANSITION":
                            continue
                        
                        trading_date = event.get("trading_date")
                        stream = event.get("stream")
                        timestamp_str = event.get("timestamp_utc")
                        
                        if not trading_date or not stream or not timestamp_str:
                            continue
                        
                        # Parse timestamp for comparison
                        try:
                            from datetime import datetime
                            timestamp = datetime.fromisoformat(timestamp_str.replace('Z', '+00:00'))
                        except Exception:
                            continue
                        
                        key = (trading_date, stream)
                        
                        # Keep the most recent state for this stream
                        if key not in latest_states or timestamp > latest_states[key][1]:
                            latest_states[key] = (event, timestamp)
                    
                    except json.JSONDecodeError:
                        continue
            
            # Process the most recent state for each stream
            if latest_states:
                logger.info(f"Found {len(latest_states)} streams to rebuild from recent events")
                for (trading_date, stream), (event, timestamp) in latest_states.items():
                    # Process this event to rebuild the stream state
                    self._event_processor.process_event(event)
                
                logger.info(f"Rebuilt {len(latest_states)} stream states from most recent events")
            else:
                logger.info("No stream state transitions found in recent events")
        
        except Exception as e:
            logger.error(f"Error rebuilding stream states from recent events: {e}", exc_info=True)
    
    def get_events_since(self, run_id: str, since_seq: int) -> List[Dict]:
        """Get events since event_seq for a run_id."""
        import json
        
        events = []
        
        if not FRONTEND_FEED_FILE.exists():
            return events
        
        try:
            # Use binary mode to avoid tell() issues
            with open(FRONTEND_FEED_FILE, 'rb') as f:
                for line_bytes in f:
                    try:
                        line = line_bytes.decode('utf-8-sig').strip()
                    except UnicodeDecodeError:
                        continue
                    
                    if not line:
                        continue
                    
                    try:
                        event = json.loads(line)
                        event_run_id = event.get("run_id")
                        event_seq = event.get("event_seq", 0)
                        
                        if event_run_id == run_id and event_seq > since_seq:
                            events.append(event)
                    
                    except json.JSONDecodeError:
                        # Silently skip malformed JSON lines
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
                    
                    state_entry_time_utc = getattr(info, 'state_entry_time_utc', datetime.now(timezone.utc))
                    slot_time_chicago = getattr(info, 'slot_time_chicago', None) or ""
                    # Extract time portion from slot_time_chicago if it's in ISO format
                    # But preserve if already formatted as "HH:MM"
                    if slot_time_chicago and 'T' in slot_time_chicago:
                        try:
                            slot_dt = datetime.fromisoformat(slot_time_chicago.replace('Z', '+00:00'))
                            # Convert to Chicago timezone if needed
                            if slot_dt.tzinfo:
                                slot_dt = slot_dt.astimezone(CHICAGO_TZ)
                            slot_time_chicago = slot_dt.strftime("%H:%M")
                        except Exception:
                            pass  # Keep original if parsing fails
                    # If empty string, set to None so frontend can display "-"
                    if slot_time_chicago == "":
                        slot_time_chicago = None
                    
                    streams.append({
                        "trading_date": trading_date,
                        "stream": stream,
                        "instrument": getattr(info, 'instrument', ''),
                        "session": getattr(info, 'session', None) or "",
                        "state": getattr(info, 'state', ''),
                        "committed": getattr(info, 'committed', False),
                        "commit_reason": getattr(info, 'commit_reason', None),
                        "slot_time_chicago": slot_time_chicago if slot_time_chicago else None,
                        "slot_time_utc": getattr(info, 'slot_time_utc', None) or None,
                        "range_high": getattr(info, 'range_high', None),
                        "range_low": getattr(info, 'range_low', None),
                        "freeze_close": getattr(info, 'freeze_close', None),
                        "range_invalidated": getattr(info, 'range_invalidated', False),
                        "state_entry_time_utc": state_entry_time_utc.isoformat(),
                        # Range lock time: when the range was locked (only for RANGE_LOCKED state)
                        "range_locked_time_utc": (
                            state_entry_time_utc.isoformat()
                            if getattr(info, 'state', '') == "RANGE_LOCKED" else None
                        ),
                        "range_locked_time_chicago": (
                            state_entry_time_utc.astimezone(CHICAGO_TZ).isoformat()
                            if getattr(info, 'state', '') == "RANGE_LOCKED" else None
                        )
                    })
        except Exception as e:
            logger.error(f"Error getting stream states: {e}", exc_info=True)
        return {
            "timestamp_chicago": datetime.now(CHICAGO_TZ).isoformat(),
            "streams": streams
        }
    
    def _add_to_ring_buffer_if_important(self, event: Dict) -> None:
        """
        Add event to ring buffer if it's an important event type.
        
        Selection rule: only event types that affect UI display OR represent anomalies.
        """
        event_type = event.get("event_type", "")
        
        # Canonical list of important event types (only events that exist in feed)
        important_types = {
            "CONNECTION_LOST",
            "CONNECTION_LOST_SUSTAINED",
            "CONNECTION_RECOVERED",
            "ENGINE_TICK_STALL_DETECTED",
            "ENGINE_TICK_STALL_RECOVERED",
            "STREAM_STATE_TRANSITION",
            "DATA_STALL_RECOVERED",  # Actual event from feed
            "IDENTITY_INVARIANTS_STATUS",  # Actual event from feed
            "KILL_SWITCH_ACTIVE",
            "EXECUTION_BLOCKED",
            "EXECUTION_ALLOWED",
            # Note: DATA_STALL_DETECTED, UNPROTECTED_POSITION_DETECTED are computed, not feed events
            # Note: RISK_GATE_CHANGED, TRADING_ALLOWED_CHANGED are derived, can be added later
        }
        
        # Exclude to avoid spam
        excluded_types = {
            "ENGINE_TICK_HEARTBEAT",  # Too frequent
        }
        
        # Check if event is important
        if event_type in excluded_types:
            return
        
        # Special handling for IDENTITY_INVARIANTS_STATUS - only include if violations detected
        if event_type == "IDENTITY_INVARIANTS_STATUS":
            data = event.get("data", {})
            violations = data.get("violations", [])
            if not violations or len(violations) == 0:
                return  # No violations, skip
        
        if event_type in important_types:
            # Increment sequence counter
            self._event_seq_counter += 1
            
            # Build standardized event payload
            ws_event = {
                "seq": self._event_seq_counter,
                "type": event_type,
                "ts_utc": event.get("timestamp_utc", datetime.now(timezone.utc).isoformat()),
                "run_id": event.get("run_id"),
                "stream_id": event.get("stream_id") or event.get("stream"),
                "severity": self._determine_severity(event_type),
            }
            
            # Add event data if present
            if event.get("data"):
                ws_event["data"] = event["data"]
            
            # Add to ring buffer
            self._important_events_buffer.append(ws_event)
    
    def _determine_severity(self, event_type: str) -> Optional[str]:
        """Determine severity level for event type."""
        critical_types = {
            "CONNECTION_LOST",
            "ENGINE_TICK_STALL_DETECTED",
            "IDENTITY_INVARIANT_VIOLATION",
            "KILL_SWITCH_ACTIVE",
            "EXECUTION_BLOCKED",
            "UNPROTECTED_POSITION_DETECTED",
            "DATA_STALL_DETECTED",
        }
        
        warning_types = {
            "CONNECTION_LOST_SUSTAINED",
            "RISK_GATE_CHANGED",
            "TRADING_ALLOWED_CHANGED",
        }
        
        if event_type in critical_types:
            return "critical"
        elif event_type in warning_types:
            return "warning"
        else:
            return "info"
    
    def get_important_events_since(self, seq_id: int) -> List[Dict]:
        """
        Get important events from ring buffer since sequence ID.
        
        This is for WebSocket live event streaming (not REST).
        
        Args:
            seq_id: Sequence ID to start from (exclusive)
        
        Returns:
            List of events with seq > seq_id, ordered by seq
        """
        return [event for event in self._important_events_buffer if event.get("seq", 0) > seq_id]
    
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
