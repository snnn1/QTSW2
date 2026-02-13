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
from .timetable_poller import TimetablePoller, compute_timetable_trading_date
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
        self._timetable_poller = TimetablePoller()
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
        
        # Initialize state from recent events on startup
        # This ensures all state is set immediately before processing begins
        # CRITICAL: Always rebuild state on startup to ensure it's current
        logger.info("Initializing watchdog state from recent events on startup")
        
        # Always rebuild connection status on startup
        logger.info("Rebuilding connection status from recent events")
        self._rebuild_connection_status_from_recent_events()
        
        # Always initialize engine tick from recent ticks on startup
        logger.info("Initializing engine tick from recent events")
        try:
            recent_ticks = self._read_recent_ticks_from_end(max_events=1)
            if recent_ticks:
                most_recent_tick = recent_ticks[-1]
                self._event_processor.process_event(most_recent_tick)
                logger.info(f"Initialized engine tick from recent events: {most_recent_tick.get('timestamp_utc', '')[:19]}")
            else:
                logger.warning("No recent ticks found for initialization")
        except Exception as e:
            logger.warning(f"Failed to initialize engine tick on startup: {e}", exc_info=True)
        
        # Always initialize bar tracking from recent bars on startup
        logger.info("Initializing bar tracking from recent events")
        try:
            recent_bars = self._read_recent_bar_events_from_end(max_events=50)  # Read more bars to ensure we get recent ones
            bars_processed = 0
            for bar_event in recent_bars:
                try:
                    self._event_processor.process_event(bar_event)
                    bars_processed += 1
                except Exception as e:
                    logger.debug(f"Failed to process bar event during startup init: {e}")
            if bars_processed > 0:
                logger.info(f"Initialized bar tracking from {bars_processed} recent bar events on startup")
            else:
                logger.info("No recent bars found for initialization")
        except Exception as e:
            logger.warning(f"Failed to initialize bar tracking on startup: {e}", exc_info=True)
        
        # Always initialize identity status from recent identity events on startup
        logger.info("Initializing identity status from recent events")
        try:
            # Read recent events and find identity events
            from .config import FRONTEND_FEED_FILE
            import json
            if FRONTEND_FEED_FILE.exists():
                with open(FRONTEND_FEED_FILE, 'r', encoding='utf-8-sig') as f:
                    all_lines = f.readlines()
                    recent_lines = all_lines[-5000:] if len(all_lines) > 5000 else all_lines
                
                latest_identity_event = None
                latest_timestamp = None
                
                for line in recent_lines:
                    if line.strip():
                        try:
                            event = json.loads(line.strip())
                            if event.get('event_type') == 'IDENTITY_INVARIANTS_STATUS':
                                ts_str = event.get('timestamp_utc', '')
                                if ts_str:
                                    try:
                                        from datetime import datetime, timezone
                                        ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                                        if ts.tzinfo is None:
                                            ts = ts.replace(tzinfo=timezone.utc)
                                        if latest_timestamp is None or ts > latest_timestamp:
                                            latest_identity_event = event
                                            latest_timestamp = ts
                                    except:
                                        pass
                        except:
                            continue
                
                if latest_identity_event:
                    self._event_processor.process_event(latest_identity_event)
                    logger.info(f"Initialized identity status from recent event at {latest_timestamp.isoformat()[:19] if latest_timestamp else 'unknown'}")
                else:
                    logger.info("No identity events found for initialization")
        except Exception as e:
            logger.warning(f"Failed to initialize identity status on startup: {e}", exc_info=True)
        
        # Start background task for processing events
        asyncio.create_task(self._process_events_loop())
        
        # Start background task for polling timetable
        asyncio.create_task(self._poll_timetable_loop())
    
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
    
    async def _poll_timetable_loop(self):
        """Poll timetable every 60 seconds. Never throws, never blocks event loop."""
        previous_trading_date = None
        
        while self._running:
            try:
                utc_now = datetime.now(timezone.utc)
                trading_date, enabled_streams_set, timetable_hash, enabled_streams_metadata = self._timetable_poller.poll()
                
                # CRITICAL: Extract trading_date from timetable (fallback to CME rollover if unavailable)
                # Trading date from timetable is authoritative - matches what robot uses
                if trading_date:
                    previous_trading_date = self._state_manager.get_trading_date()
                    
                    # Update state manager (always updates trading_date, conditionally updates enabled_streams)
                    self._state_manager.update_timetable_streams(
                        enabled_streams_set, trading_date, timetable_hash, utc_now,
                        enabled_streams_metadata=enabled_streams_metadata
                    )
                    
                    # CRITICAL FIX: Detect day rollover by TIME comparison, NOT hash change
                    # Hash change ≠ day change. Day rollover happens at 17:00 CT every day,
                    # even if timetable file content is unchanged.
                    if previous_trading_date and previous_trading_date != trading_date:
                        # Refinement 1: Explicit trading_date change event (first-class lifecycle event)
                        logger.info(
                            f"WATCHDOG_TRADING_DATE_CHANGED: old_date={previous_trading_date}, "
                            f"new_date={trading_date}, source=timetable. "
                            f"This triggers stream cleanup and UI reset."
                        )
                        # Day rollover detected - cleanup stale streams aggressively
                        # Use clear_all_for_date=True to ensure all old states are removed immediately
                        streams_before = len(self._state_manager._stream_states)
                        self._state_manager.cleanup_stale_streams(
                            trading_date, utc_now, clear_all_for_date=True
                        )
                        streams_after = len(self._state_manager._stream_states)
                        logger.info(
                            f"TRADING_DAY_ROLLOVER: {previous_trading_date} -> {trading_date}, "
                            f"cleaned up {streams_before - streams_after} stale stream(s)"
                        )
                    
                    # Hash change detection (separate from day rollover)
                    # Only used for enabled_streams updates, not day changes
                    if timetable_hash and timetable_hash != self._state_manager.get_timetable_hash():
                        # Timetable content changed (enabled streams may have changed)
                        # This is logged in timetable_poller.py
                        pass
                    
                    # Note: TIMETABLE_POLL_OK is now logged in timetable_poller.py with trading_date source
                    # Only log here if timetable unavailable (enabled_streams_set is None)
                    if enabled_streams_set is None:
                        logger.warning(
                            f"TIMETABLE_POLL_FAIL: trading_date={trading_date}, "
                            f"but timetable file missing/invalid (fail-open mode)"
                        )
            except Exception as e:
                # Never throw - log and continue
                # Keep last known good enabled_streams on failure
                logger.error(f"TIMETABLE_POLL_FAIL: Unexpected error: {e}", exc_info=True)
            
            await asyncio.sleep(60)
    
    def _cleanup_stale_streams_periodic(self):
        """Periodically clean up stale streams (runs every 60 seconds)."""
        try:
            # Get current trading date from state manager (use getter, not private field)
            current_trading_date = self._state_manager.get_trading_date()
            
            # Fallback: if trading_date not set, use CME rollover helper
            if not current_trading_date:
                chicago_now = datetime.now(CHICAGO_TZ)
                current_trading_date = compute_timetable_trading_date(chicago_now)
                logger.debug(
                    f"_cleanup_stale_streams_periodic: trading_date not set in state manager, "
                    f"using computed fallback: {current_trading_date}"
                )
            
            utc_now = datetime.now(timezone.utc)
            streams_before = len(self._state_manager._stream_states)
            self._state_manager.cleanup_stale_streams(current_trading_date, utc_now)
            streams_after = len(self._state_manager._stream_states)
            if streams_before != streams_after:
                logger.info(
                    f"_cleanup_stale_streams_periodic: Cleaned up {streams_before - streams_after} stream(s) "
                    f"(before: {streams_before}, after: {streams_after}, trading_date: {current_trading_date})"
                )
        except Exception as e:
            logger.warning(f"Error in periodic cleanup: {e}", exc_info=True)
    
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
            
            # Check if connection status needs initialization - rebuild from recent events
            # This handles initialization or state loss scenarios
            # Note: connection_status defaults to "Connected" but may be Unknown if state_manager was reset
            # We rebuild if status is Unknown OR if we've never seen a connection event
            if (self._state_manager._connection_status == "Unknown" or 
                self._state_manager._last_connection_event_utc is None):
                logger.info(
                    f"Connection status needs initialization "
                    f"(status={self._state_manager._connection_status}, "
                    f"last_event={self._state_manager._last_connection_event_utc}) - "
                    f"rebuilding from recent events"
                )
                self._rebuild_connection_status_from_recent_events()
            
            # CRITICAL: Always check for the most recent ENGINE_TICK_CALLSITE event from the end of file
            # This ensures we catch ticks even if cursor is ahead or events are out of order
            # We only need the most recent one to update liveness timestamp
            try:
                recent_ticks = self._read_recent_ticks_from_end(max_events=1)
                if recent_ticks:
                    # Process the most recent tick to update liveness immediately
                    most_recent_tick = recent_ticks[-1]  # Get the newest one
                    tick_timestamp_str = most_recent_tick.get("timestamp_utc")
                    if tick_timestamp_str:
                        try:
                            from datetime import datetime, timezone
                            tick_timestamp = datetime.fromisoformat(tick_timestamp_str.replace('Z', '+00:00'))
                            if tick_timestamp.tzinfo is None:
                                tick_timestamp = tick_timestamp.replace(tzinfo=timezone.utc)
                            
                            # Check if this is newer than what we already have
                            current_tick_utc = self._state_manager._last_engine_tick_utc
                            if not current_tick_utc or tick_timestamp > current_tick_utc:
                                # Process it to update state manager
                                self._event_processor.process_event(most_recent_tick)
                                logger.info(
                                    f"✅ Updated liveness from end-of-file: tick_timestamp={tick_timestamp.isoformat()}, "
                                    f"previous_tick={current_tick_utc.isoformat() if current_tick_utc else 'None'}, "
                                    f"tick_age={(datetime.now(timezone.utc) - tick_timestamp).total_seconds():.1f}s"
                                )
                            else:
                                logger.debug(
                                    f"End-of-file tick not newer: tick={tick_timestamp.isoformat()}, "
                                    f"current={current_tick_utc.isoformat() if current_tick_utc else 'None'}"
                                )
                        except Exception as e:
                            logger.warning(f"Failed to parse tick timestamp for end-of-file check: {e}", exc_info=True)
                else:
                    # No ticks found - this is a problem if engine should be running
                    current_tick_utc = self._state_manager._last_engine_tick_utc
                    if current_tick_utc:
                        tick_age = (datetime.now(timezone.utc) - current_tick_utc).total_seconds()
                        from .config import ENGINE_TICK_STALL_THRESHOLD_SECONDS
                        logger.warning(
                            f"⚠️ No ENGINE_TICK_CALLSITE events found in feed file. "
                            f"Current tick age: {tick_age:.1f}s, threshold: {ENGINE_TICK_STALL_THRESHOLD_SECONDS}s"
                        )
                    else:
                        # No ticks ever received - log this as a warning
                        logger.warning(
                            f"⚠️ No ENGINE_TICK_CALLSITE events found in feed file and no previous ticks. "
                            f"This may indicate events aren't being processed or feed file is empty."
                        )
            except Exception as e:
                logger.error(f"Error reading ticks from end of file: {e}", exc_info=True)
            
            # CRITICAL: Also check for recent bar events from the end of file
            # This ensures bar tracking stays current even if cursor is ahead
            try:
                recent_bars = self._read_recent_bar_events_from_end(max_events=10)
                if recent_bars:
                    from datetime import datetime, timezone
                    now = datetime.now(timezone.utc)
                    bars_processed = 0
                    for bar_event in recent_bars:
                        try:
                            # Process bar event to update bar tracking
                            self._event_processor.process_event(bar_event)
                            bars_processed += 1
                        except Exception as e:
                            logger.warning(f"Failed to process bar event from end-of-file: {e}", exc_info=True)
                    
                    if bars_processed > 0:
                        logger.info(f"✅ Processed {bars_processed} recent bar event(s) from end-of-file for bar tracking")
                    else:
                        logger.warning(f"⚠️ Found {len(recent_bars)} bar events but failed to process them")
                else:
                    # Check if we have any bars tracked at all
                    if not self._state_manager._last_bar_utc_by_execution_instrument:
                        logger.debug("No bar events found in feed and no bars tracked yet")
            except Exception as e:
                logger.error(f"Error reading bar events from end of file: {e}", exc_info=True)
            
            # Normal incremental processing - read new events since cursor
            # CRITICAL: This now reads from end of file (last 5000 lines) to catch all recent events
            # including new run_ids that aren't in cursor yet
            events = self._read_feed_events_since(cursor)
            
            # Process each event
            tick_events_count = 0
            latest_tick_timestamp = None
            for event in events:
                event_type = event.get("event_type", "")
                if event_type in ("ENGINE_TICK_CALLSITE", "ENGINE_ALIVE"):
                    tick_events_count += 1
                    timestamp_utc = event.get("timestamp_utc")
                    if timestamp_utc:
                        latest_tick_timestamp = timestamp_utc
                self._event_processor.process_event(event)
                # Add important events to ring buffer for WebSocket streaming
                self._add_to_ring_buffer_if_important(event)
            
            # Diagnostic: Log when tick/heartbeat events are read from feed
            if tick_events_count > 0:
                logger.info(
                    f"Processed {tick_events_count} tick/heartbeat event(s) from feed. "
                    f"Latest tick timestamp: {latest_tick_timestamp}"
                )
            elif len(events) > 0:
                # No tick events in this batch - log what we did get
                event_types = [e.get("event_type", "UNKNOWN") for e in events[:5]]
                logger.debug(f"Processed {len(events)} events (no tick/heartbeat): {event_types}")
            
            # Update cursor for all run_ids that had events
            if events:
                # Group events by run_id and update cursor for each
                run_ids_seen = {}
                for event in events:
                    run_id = event.get("run_id")
                    event_seq = event.get("event_seq", 0)
                    if run_id:
                        # Keep the highest seq for each run_id
                        if run_id not in run_ids_seen or event_seq > run_ids_seen[run_id]:
                            run_ids_seen[run_id] = event_seq
                
                # Update cursor for all run_ids
                for run_id, last_seq in run_ids_seen.items():
                    cursor[run_id] = last_seq
                
                if run_ids_seen:
                    self._cursor_manager.save_cursor(cursor)
                    logger.debug(f"Updated cursor for {len(run_ids_seen)} run_id(s): {list(run_ids_seen.keys())[:3]}...")
        
        except Exception as e:
            logger.error(f"Error processing feed events: {e}", exc_info=True)
    
    def _read_recent_bar_events_from_end(self, max_events: int = 10) -> List[Dict]:
        """Read recent bar events from the end of frontend_feed.jsonl."""
        import json
        
        bar_events = []
        if not FRONTEND_FEED_FILE.exists():
            return bar_events
        
        try:
            # CRITICAL FIX: Use same simple approach as _read_feed_events_since
            # Read file normally and take last N lines - more reliable than backwards reading
            with open(FRONTEND_FEED_FILE, 'r', encoding='utf-8-sig') as f:
                all_lines = f.readlines()
                # Read last 5000 lines to ensure we catch bar events (bars may be less frequent)
                lines_to_read = 5000
                lines = all_lines[-lines_to_read:] if len(all_lines) > lines_to_read else all_lines
            
            # Process lines in reverse (newest first)
            for line in reversed(lines):
                if len(bar_events) >= max_events:
                    break
                try:
                    event = json.loads(line.strip())
                    event_type = event.get("event_type", "")
                    if event_type in ("BAR_RECEIVED_NO_STREAMS", "BAR_ACCEPTED", "ONBARUPDATE_CALLED"):
                        bar_events.append(event)
                except json.JSONDecodeError:
                    continue
        except Exception as e:
            logger.debug(f"Error reading bar events from end of file: {e}", exc_info=True)
        
        return bar_events
    
    def _read_recent_ticks_from_end(self, max_events: int = 10) -> List[Dict]:
        """
        Read the most recent ENGINE_TICK_CALLSITE or ENGINE_ALIVE events from the end of the feed file.
        ENGINE_ALIVE is used as fallback when ENGINE_TICK_CALLSITE is not emitted (e.g. older DLL).
        """
        import json
        
        if not FRONTEND_FEED_FILE.exists():
            return []
        
        ticks = []
        try:
            # Read file in reverse to find most recent ticks
            with open(FRONTEND_FEED_FILE, 'rb') as f:
                # Seek to end
                f.seek(0, 2)  # 2 = SEEK_END
                file_size = f.tell()
                
                # Read backwards in chunks
                chunk_size = 8192  # 8KB chunks
                buffer = b''
                position = file_size
                
                while position > 0 and len(ticks) < max_events:
                    # Read chunk
                    read_size = min(chunk_size, position)
                    position -= read_size
                    f.seek(position)
                    chunk = f.read(read_size)
                    buffer = chunk + buffer
                    
                    # Process complete lines from buffer (in reverse order)
                    while b'\n' in buffer and len(ticks) < max_events:
                        # Extract last complete line
                        last_newline = buffer.rfind(b'\n')
                        if last_newline == -1:
                            break
                        
                        line_bytes = buffer[last_newline+1:]
                        buffer = buffer[:last_newline]
                        
                        if not line_bytes.strip():
                            continue
                        
                        try:
                            line = line_bytes.decode('utf-8-sig').strip()
                            if not line:
                                continue
                            
                            event = json.loads(line)
                            event_type = event.get("event_type")
                            if event_type in ("ENGINE_TICK_CALLSITE", "ENGINE_ALIVE"):
                                ticks.append(event)
                                if len(ticks) >= max_events:
                                    break
                        except (json.JSONDecodeError, UnicodeDecodeError) as e:
                            logger.debug(f"Skipping malformed JSON line (reading from end of file, position ~{position}): {e}")
                            continue
                
                # Process remaining buffer if we haven't found enough ticks
                if len(ticks) < max_events and buffer.strip():
                    try:
                        line = buffer.decode('utf-8-sig').strip()
                        if line:
                            event = json.loads(line)
                            if event.get("event_type") in ("ENGINE_TICK_CALLSITE", "ENGINE_ALIVE"):
                                ticks.append(event)
                    except (json.JSONDecodeError, UnicodeDecodeError) as e:
                        logger.debug(f"Skipping malformed JSON in buffer: {e}")
                        pass
            
            # Reverse to get chronological order (oldest to newest)
            ticks.reverse()
            
            # Diagnostic logging
            if ticks:
                latest_tick = ticks[-1]
                logger.info(
                    f"Found {len(ticks)} tick/heartbeat event(s) from end of file. "
                    f"Latest: timestamp={latest_tick.get('timestamp_utc')}, "
                    f"event_seq={latest_tick.get('event_seq')}, run_id={latest_tick.get('run_id')}"
                )
            else:
                logger.debug("No ENGINE_TICK_CALLSITE events found when reading from end of file")
            
        except Exception as e:
            logger.error(f"Error reading recent ticks from end of file: {e}", exc_info=True)
        
        return ticks
    
    def _read_feed_events_since(self, cursor: Dict[str, int]) -> List[Dict]:
        """Read events from frontend_feed.jsonl since cursor position."""
        import json
        
        events = []
        
        if not FRONTEND_FEED_FILE.exists():
            return events
        
        try:
            # CRITICAL FIX: Always read from the end of file for recent events
            # The old byte-position tracking was causing issues where we'd read from old positions
            # and miss new run_ids. Instead, read the last N lines to catch recent events.
            # This ensures we always process the most recent events regardless of cursor position.
            
            # Read last 5000 lines to ensure we catch all recent events
            # This is efficient and ensures we don't miss new run_ids
            MAX_LINES_TO_READ = 5000
            
            # CRITICAL FIX: Read file normally from end, but use a simpler approach
            # Read the entire file and take last N lines - this is more reliable than backwards reading
            with open(FRONTEND_FEED_FILE, 'r', encoding='utf-8-sig') as f:
                all_lines = f.readlines()
                # Take last N lines
                lines = all_lines[-MAX_LINES_TO_READ:] if len(all_lines) > MAX_LINES_TO_READ else all_lines
            
                # Process lines
                parse_errors = 0
                for line_str in lines:
                    line = line_str.strip()
                    if not line:
                        continue
                    
                    try:
                        event = json.loads(line)
                        run_id = event.get("run_id")
                        event_seq = event.get("event_seq", 0)
                        event_type = event.get("event_type", "")
                        
                        if not run_id:
                            continue
                        
                        # Check if we should include this event
                        last_seq = cursor.get(run_id, 0)
                        # Always include bar events and tick events for state tracking (even if cursor is ahead)
                        # This ensures bar tracking and engine liveness stay current
                        is_bar_event = event_type in ("BAR_RECEIVED_NO_STREAMS", "BAR_ACCEPTED", "ONBARUPDATE_CALLED")
                        is_tick_event = event_type == "ENGINE_TICK_CALLSITE"
                        
                        # CRITICAL: Always include tick/bar events for state tracking
                        # Also include events that are newer than cursor position
                        should_include = False
                        if is_bar_event or is_tick_event:
                            # Always process tick/bar events for state tracking
                            should_include = True
                            if event_seq <= last_seq:
                                logger.debug(
                                    f"{event_type} included despite cursor: event_seq={event_seq} <= cursor[{run_id}]={last_seq} "
                                    f"(bar/tick events always processed for state tracking)"
                                )
                        elif event_seq > last_seq:
                            # Include events newer than cursor
                            should_include = True
                        
                        if should_include:
                            events.append(event)
                    
                    except json.JSONDecodeError:
                        # Silently skip malformed JSON lines
                        parse_errors += 1
                        continue
                
                # Log parse errors only once per read, not per line
                if parse_errors > 0:
                    logger.debug(f"Skipped {parse_errors} malformed JSON lines in feed file")
                
                # Sort events by timestamp to ensure chronological processing
                # This is critical when reading from end of file - events may be out of order
                events.sort(key=lambda e: e.get('timestamp_utc', ''))
        
        except Exception as e:
            logger.error(f"Error reading feed file: {e}", exc_info=True)
        
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
    
    def _rebuild_connection_status_from_recent_events(self):
        """
        Rebuild connection status by finding the most recent connection event.
        This initializes connection status when state_manager starts or connection status is Unknown.
        """
        import json
        from datetime import datetime, timezone
        
        if not FRONTEND_FEED_FILE.exists():
            logger.warning("Feed file does not exist, cannot rebuild connection status")
            return
        
        try:
            # Track the most recent connection event
            # Connection event types: CONNECTION_LOST, CONNECTION_LOST_SUSTAINED, CONNECTION_RECOVERED, CONNECTION_RECOVERED_NOTIFICATION
            latest_connection_event = None
            latest_timestamp = None
            
            # Read recent events (last 5000 lines should be enough to find current connection status)
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
                        
                        # Only process connection events
                        if event_type not in ("CONNECTION_LOST", "CONNECTION_LOST_SUSTAINED", 
                                             "CONNECTION_RECOVERED", "CONNECTION_RECOVERED_NOTIFICATION"):
                            continue
                        
                        timestamp_str = event.get("timestamp_utc")
                        if not timestamp_str:
                            continue
                        
                        # Parse timestamp for comparison
                        try:
                            timestamp = datetime.fromisoformat(timestamp_str.replace('Z', '+00:00'))
                            if timestamp.tzinfo is None:
                                timestamp = timestamp.replace(tzinfo=timezone.utc)
                        except Exception:
                            continue
                        
                        # Keep the most recent connection event
                        if latest_timestamp is None or timestamp > latest_timestamp:
                            latest_connection_event = event
                            latest_timestamp = timestamp
                    
                    except json.JSONDecodeError:
                        continue
            
            # Process the most recent connection event to rebuild connection status
            if latest_connection_event:
                logger.info(
                    f"Found most recent connection event: {latest_connection_event.get('event_type')} "
                    f"at {latest_timestamp.isoformat() if latest_timestamp else 'unknown'}"
                )
                # Process this event to rebuild the connection status
                self._event_processor.process_event(latest_connection_event)
                logger.info(
                    f"Rebuilt connection status from recent event: "
                    f"status={self._state_manager._connection_status}, "
                    f"last_event_utc={self._state_manager._last_connection_event_utc.isoformat() if self._state_manager._last_connection_event_utc else None}"
                )
            else:
                logger.info("No connection events found in recent feed - connection status remains Unknown")
                # Default to Connected if no connection events found (assume connected state)
                # This is safer than Unknown for monitoring purposes
                if self._state_manager._connection_status == "Unknown":
                    self._state_manager._connection_status = "Connected"
                    logger.info("Set connection status to Connected (default - no connection events found)")
        
        except Exception as e:
            logger.error(f"Error rebuilding connection status from recent events: {e}", exc_info=True)
    
    def get_events_since(self, run_id: str, since_seq: int) -> List[Dict]:
        """
        Get events since event_seq for a run_id.
        
        Optimized: Reads forward but stops early when possible.
        For very large files, consider using cursor-based incremental reading.
        """
        import json
        
        events = []
        
        if not FRONTEND_FEED_FILE.exists():
            return events
        
        try:
            # Use binary mode to avoid tell() issues
            with open(FRONTEND_FEED_FILE, 'rb') as f:
                # Track if we've seen events for this run_id to optimize early stopping
                seen_run_id_events = False
                consecutive_non_matching = 0
                max_consecutive_skip = 1000  # Stop if we see 1000 non-matching events in a row
                
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
                        
                        if event_run_id == run_id:
                            seen_run_id_events = True
                            consecutive_non_matching = 0
                            if event_seq > since_seq:
                                events.append(event)
                            elif event_seq <= since_seq and seen_run_id_events:
                                # We've found matching run_id but seq is <= since_seq
                                # If we've already collected events, we can stop (events are sequential)
                                if events:
                                    break
                        else:
                            # Not matching run_id
                            if seen_run_id_events:
                                # We've seen events for this run_id, but now seeing different run_id
                                # This could mean we've moved to a different run_id section
                                consecutive_non_matching += 1
                                if consecutive_non_matching > max_consecutive_skip:
                                    # Likely moved past this run_id's events, stop
                                    break
                    
                    except json.JSONDecodeError:
                        # Silently skip malformed JSON lines
                        continue
        
        except Exception as e:
            logger.error(f"Error reading feed file: {e}", exc_info=True)
        
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
        """
        Get current stream states, merging timetable data with watchdog state data.
        
        Returns all enabled streams from timetable, with watchdog state data merged in.
        If timetable unavailable, falls back to showing only streams with watchdog state.
        """
        streams = []
        timetable_unavailable = False
        
        try:
            # CRITICAL: Use getter, never access private fields directly
            current_trading_date = self._state_manager.get_trading_date()
            if not current_trading_date:
                # Fallback: compute using CME rollover (timetable unavailable)
                chicago_now = datetime.now(CHICAGO_TZ)
                current_trading_date = compute_timetable_trading_date(chicago_now)
                logger.debug(
                    f"get_stream_states: trading_date not set in state manager, "
                    f"using computed fallback: {current_trading_date}"
                )
            
            # Get timetable stream metadata (instrument, session, slot_time)
            timetable_streams_metadata = self._state_manager.get_timetable_streams_metadata()
            
            # CRITICAL: Use getter, never access _enabled_streams directly
            enabled_streams = self._state_manager.get_enabled_streams()
            if enabled_streams is None:
                timetable_unavailable = True  # Flag for UI warning
            
            # Get watchdog state data
            stream_states_dict = getattr(self._state_manager, '_stream_states', {})
            
            # If timetable metadata is available, use it as the source of truth for enabled streams
            if timetable_streams_metadata and enabled_streams:
                # Merge timetable data with watchdog state data
                for stream_id in enabled_streams:
                    timetable_meta = timetable_streams_metadata.get(stream_id, {})
                    
                    # Get watchdog state if available
                    watchdog_key = (current_trading_date, stream_id)
                    watchdog_info = stream_states_dict.get(watchdog_key)
                    
                    # Use timetable data for: stream, instrument, session, slot_time
                    instrument = timetable_meta.get('instrument', '')
                    session = timetable_meta.get('session', '')
                    slot_time = timetable_meta.get('slot_time', '')
                    
                    # Format slot_time as "HH:MM" if it's not already formatted
                    slot_time_chicago = None
                    if slot_time:
                        # Already in "HH:MM" format from timetable
                        slot_time_chicago = slot_time
                    
                    # Use watchdog data for: state, time_in_state, range, commit, issues
                    # CRITICAL: watchdog_key = (current_trading_date, stream_id) ensures we only get states for current date
                    # Double-check trading_date to prevent showing ranges from yesterday's session
                    if watchdog_info:
                        # Verify trading_date matches (defensive check in case cleanup hasn't run)
                        watchdog_trading_date = getattr(watchdog_info, 'trading_date', None)
                        if watchdog_trading_date and watchdog_trading_date != current_trading_date:
                            logger.warning(
                                f"get_stream_states: Found watchdog state for stream {stream_id} with wrong trading_date: "
                                f"{watchdog_trading_date} (expected {current_trading_date}). Skipping watchdog data. "
                                f"This indicates cleanup may not have run yet."
                            )
                            # Treat as if no watchdog state exists - use timetable defaults
                            watchdog_info = None
                        else:
                            # CRITICAL: Also verify ranges are None if state is not RANGE_LOCKED
                            # This prevents showing stale ranges from previous sessions
                            watchdog_state = getattr(watchdog_info, 'state', '')
                            if watchdog_state != "RANGE_LOCKED":
                                # If state is not RANGE_LOCKED, ranges should be None
                                # Clear them defensively to prevent stale data
                                if getattr(watchdog_info, 'range_high', None) is not None or \
                                   getattr(watchdog_info, 'range_low', None) is not None:
                                    logger.warning(
                                        f"get_stream_states: Found non-RANGE_LOCKED stream {stream_id} ({current_trading_date}) "
                                        f"with ranges (state: {watchdog_state}). Clearing ranges to prevent stale data."
                                    )
                                    # Clear ranges to prevent stale data display
                                    watchdog_info.range_high = None
                                    watchdog_info.range_low = None
                                    watchdog_info.freeze_close = None
                    
                    if watchdog_info:
                        # Watchdog state exists - merge with timetable data
                        state_entry_time_utc = getattr(watchdog_info, 'state_entry_time_utc', datetime.now(timezone.utc))
                        watchdog_slot_time = getattr(watchdog_info, 'slot_time_chicago', None) or ""
                        
                        # Prefer watchdog slot_time if available (more recent), otherwise use timetable
                        if watchdog_slot_time and watchdog_slot_time != "":
                            if 'T' in watchdog_slot_time:
                                try:
                                    slot_dt = datetime.fromisoformat(watchdog_slot_time.replace('Z', '+00:00'))
                                    if slot_dt.tzinfo:
                                        slot_dt = slot_dt.astimezone(CHICAGO_TZ)
                                    slot_time_chicago = slot_dt.strftime("%H:%M")
                                except Exception:
                                    pass
                            else:
                                slot_time_chicago = watchdog_slot_time
                        
                        streams.append({
                            "trading_date": current_trading_date,
                            "stream": stream_id,
                            "instrument": getattr(watchdog_info, 'instrument', None) or instrument,  # Canonical instrument (DO NOT CHANGE)
                            "execution_instrument": getattr(watchdog_info, 'execution_instrument', None),  # Full contract name (e.g., "MES 03-26")
                            "session": getattr(watchdog_info, 'session', None) or session,
                            "state": getattr(watchdog_info, 'state', ''),
                            "committed": getattr(watchdog_info, 'committed', False),
                            "commit_reason": getattr(watchdog_info, 'commit_reason', None),
                            "slot_time_chicago": slot_time_chicago,
                            "slot_time_utc": getattr(watchdog_info, 'slot_time_utc', None) or None,
                            "range_high": getattr(watchdog_info, 'range_high', None),
                            "range_low": getattr(watchdog_info, 'range_low', None),
                            "freeze_close": getattr(watchdog_info, 'freeze_close', None),
                            "range_invalidated": getattr(watchdog_info, 'range_invalidated', False),
                            "state_entry_time_utc": state_entry_time_utc.isoformat(),
                            "range_locked_time_utc": (
                                state_entry_time_utc.isoformat()
                                if getattr(watchdog_info, 'state', '') == "RANGE_LOCKED" else None
                            ),
                            "range_locked_time_chicago": (
                                state_entry_time_utc.astimezone(CHICAGO_TZ).isoformat()
                                if getattr(watchdog_info, 'state', '') == "RANGE_LOCKED" else None
                            )
                        })
                    else:
                        # No watchdog state - use timetable data with defaults for watchdog fields
                        # CRITICAL: Ensure ranges are None (not from yesterday)
                        streams.append({
                            "trading_date": current_trading_date,
                            "stream": stream_id,
                            "instrument": instrument,  # Canonical instrument (DO NOT CHANGE)
                            "execution_instrument": None,  # Not available from timetable (will be set from events)
                            "session": session,
                            "state": "",  # No state yet
                            "committed": False,
                            "commit_reason": None,
                            "slot_time_chicago": slot_time_chicago,
                            "slot_time_utc": None,
                            "range_high": None,  # Explicitly None - no ranges from previous sessions
                            "range_low": None,   # Explicitly None - no ranges from previous sessions
                            "freeze_close": None, # Explicitly None - no ranges from previous sessions
                            "range_invalidated": False,
                            "state_entry_time_utc": datetime.now(timezone.utc).isoformat(),  # Use current time for time_in_state calculation
                            "range_locked_time_utc": None,
                            "range_locked_time_chicago": None
                        })
                
                logger.info(
                    f"get_stream_states: Returning {len(streams)} streams from timetable "
                    f"(enabled_streams: {len(enabled_streams)}, "
                    f"with_watchdog_state: {sum(1 for s in streams if s.get('state'))}, "
                    f"without_watchdog_state: {sum(1 for s in streams if not s.get('state'))})"
                )
            else:
                # Fallback: Timetable unavailable - show only streams with watchdog state
                logger.debug(
                    f"get_stream_states: Timetable unavailable, falling back to watchdog-only streams"
                )
                
                filtered_by_date = 0
                filtered_by_enabled = 0
                for (trading_date, stream), info in stream_states_dict.items():
                    # Only include streams from current trading date
                    if trading_date != current_trading_date:
                        filtered_by_date += 1
                        continue
                    
                    # If enabled_streams is available, filter by it
                    if enabled_streams is not None:
                        if stream not in enabled_streams:
                            filtered_by_enabled += 1
                            continue
                    
                    # Build stream data from watchdog state only
                    state_entry_time_utc = getattr(info, 'state_entry_time_utc', datetime.now(timezone.utc))
                    slot_time_chicago = getattr(info, 'slot_time_chicago', None) or ""
                    if slot_time_chicago and 'T' in slot_time_chicago:
                        try:
                            slot_dt = datetime.fromisoformat(slot_time_chicago.replace('Z', '+00:00'))
                            if slot_dt.tzinfo:
                                slot_dt = slot_dt.astimezone(CHICAGO_TZ)
                            slot_time_chicago = slot_dt.strftime("%H:%M")
                        except Exception:
                            pass
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
                        "range_locked_time_utc": (
                            state_entry_time_utc.isoformat()
                            if getattr(info, 'state', '') == "RANGE_LOCKED" else None
                        ),
                        "range_locked_time_chicago": (
                            state_entry_time_utc.astimezone(CHICAGO_TZ).isoformat()
                            if getattr(info, 'state', '') == "RANGE_LOCKED" else None
                        )
                    })
                
                logger.info(
                    f"get_stream_states: Returning {len(streams)} streams (fallback mode - watchdog only), "
                    f"filtered_by_date: {filtered_by_date}, filtered_by_enabled: {filtered_by_enabled}"
                )
            
        except Exception as e:
            logger.error(f"Error getting stream states: {e}", exc_info=True)
        return {
            "timestamp_chicago": datetime.now(CHICAGO_TZ).isoformat(),
            "streams": streams,
            "timetable_unavailable": timetable_unavailable  # Flag for UI warning banner
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
            "CONNECTION_RECOVERED_NOTIFICATION",  # Recovery notification after sustained disconnect
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
            "ENGINE_TICK_CALLSITE",  # Diagnostic event, used for backend liveness only
            "ENGINE_ALIVE",  # Strategy heartbeat, used for backend liveness only
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
            "CONNECTION_RECOVERED_NOTIFICATION",  # Recovery notification (informational)
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
