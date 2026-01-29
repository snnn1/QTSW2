"""
Event Processor

Processes events from frontend_feed.jsonl and updates state manager.
"""
import json
import logging
import re
from pathlib import Path
from typing import Dict, List, Optional
from datetime import datetime, timezone
import pytz

from .config import FRONTEND_FEED_FILE
from .state_manager import WatchdogStateManager

logger = logging.getLogger(__name__)


def get_canonical_instrument(instrument: str) -> str:
    """
    PHASE 2: Map execution instrument to canonical instrument.
    Maps micro futures (MES, MNQ, etc.) to their base instruments (ES, NQ, etc.).
    Returns execution instrument unchanged if not a micro or if lookup fails.
    """
    try:
        from modules.analyzer.logic.instrument_logic import InstrumentManager
        mgr = InstrumentManager()
        if mgr.is_micro_future(instrument):
            return mgr.get_base_instrument(instrument)
        return instrument
    except Exception as e:
        logger.warning(f"Failed to canonicalize instrument '{instrument}': {e}, using as-is")
        return instrument


def canonicalize_stream(stream_id: str, execution_instrument: str) -> str:
    """
    PHASE 2: Map stream ID to canonical stream ID.
    Replaces execution instrument in stream ID with canonical instrument.
    e.g., "MES1" -> "ES1"
    """
    canonical_instrument = get_canonical_instrument(execution_instrument)
    if execution_instrument and execution_instrument.upper() in stream_id.upper():
        # Case-insensitive replacement
        import re
        pattern = re.compile(re.escape(execution_instrument), re.IGNORECASE)
        return pattern.sub(canonical_instrument, stream_id)
    return stream_id


class EventProcessor:
    """Processes events and updates state manager."""
    
    def __init__(self, state_manager: WatchdogStateManager):
        self._state_manager = state_manager
        self._last_processed_seq: Dict[str, int] = {}  # run_id -> event_seq
    
    def _parse_timestamp(self, timestamp_str: str) -> Optional[datetime]:
        """Parse ISO 8601 timestamp string to datetime."""
        try:
            dt = datetime.fromisoformat(timestamp_str.replace('Z', '+00:00'))
            if dt.tzinfo is None:
                dt = dt.replace(tzinfo=timezone.utc)
            return dt
        except Exception as e:
            logger.warning(f"Failed to parse timestamp {timestamp_str}: {e}")
            return None
    
    def process_event(self, event: Dict):
        """Process a single event and update state manager."""
        event_type = event.get("event_type", "")
        run_id = event.get("run_id", "")
        timestamp_utc_str = event.get("timestamp_utc", "")
        data = event.get("data", {})
        
        timestamp_utc = self._parse_timestamp(timestamp_utc_str)
        if not timestamp_utc:
            return
        
        # Update last processed seq for this run_id
        event_seq = event.get("event_seq", 0)
        if run_id:
            self._last_processed_seq[run_id] = max(
                self._last_processed_seq.get(run_id, 0),
                event_seq
            )
        
        # Process event by type
        if event_type == "ENGINE_START":
            # Initialize engine tick timestamp on start so watchdog knows engine is alive from the beginning
            self._state_manager.update_engine_tick(timestamp_utc)
            # Clear ALL streams on engine start (new run) - they will be re-initialized
            # This prevents showing stale ARMED/RANGE_BUILDING states from previous runs
            # CRITICAL: Use timetable's trading_date (authoritative), not event's trading_date
            trading_date = self._state_manager.get_trading_date()
            if trading_date:
                self._state_manager.cleanup_stale_streams(trading_date, timestamp_utc, clear_all_for_date=True)
            else:
                # If timetable not loaded yet, use computed fallback
                from .timetable_poller import compute_timetable_trading_date
                import pytz
                chicago_tz = pytz.timezone("America/Chicago")
                chicago_now = datetime.now(chicago_tz)
                trading_date = compute_timetable_trading_date(chicago_now)
                self._state_manager.cleanup_stale_streams(trading_date, timestamp_utc, clear_all_for_date=True)
        
        elif event_type == "ENGINE_HEARTBEAT":
            # DEPRECATED: ENGINE_HEARTBEAT removed from critical events (only works if AddOn/Strategy installed)
            # Keep handler for backward compatibility with old events, but new events won't reach here
            # Use ENGINE_TICK_CALLSITE (rate-limited) instead for reliable liveness monitoring
            self._state_manager.update_engine_tick(timestamp_utc)
        
        elif event_type == "ENGINE_TICK_HEARTBEAT":
            # PATTERN 1: Update engine tick timestamp AND track last bar time per instrument
            # Note: ENGINE_TICK_HEARTBEAT is bar-driven and tracks bar processing
            # Use ENGINE_TICK_CALLSITE (rate-limited) for primary Tick() liveness signal
            self._state_manager.update_engine_tick(timestamp_utc)
            
            # Extract instrument and bar_time_utc from heartbeat payload
            instrument = data.get("instrument") or event.get("instrument")
            bar_time_utc_str = data.get("bar_time_utc")
            
            if instrument and bar_time_utc_str:
                bar_time_utc = self._parse_timestamp(bar_time_utc_str)
                if bar_time_utc:
                    # Update per-instrument last bar time from heartbeat payload
                    self._state_manager.update_last_bar(instrument, bar_time_utc)
        
        elif event_type == "ONBARUPDATE_CALLED":
            # OnBarUpdate is called constantly while engine is running (when bars arrive)
            # Use this as a reliable heartbeat indicator for engine liveness
            # Confirms NinjaTrader is calling OnBarUpdate (bar-dependent)
            self._state_manager.update_engine_tick(timestamp_utc)
            
            # Also track last bar time per instrument if available
            instrument = data.get("instrument") or event.get("instrument")
            if instrument:
                # Use event timestamp as bar time proxy (OnBarUpdate is called when bar arrives)
                self._state_manager.update_last_bar(instrument, timestamp_utc)
        
        elif event_type == "ENGINE_TICK_CALLSITE":
            # ENGINE_TICK_CALLSITE is emitted every time Tick() is called (very frequent, rate-limited in feed)
            # This is the primary heartbeat indicator for engine liveness
            # Rate-limited to every 5 seconds in event feed to reduce log volume
            # This ensures engine_alive stays true as long as Tick() is being called
            # Diagnostic: Log when ENGINE_TICK_CALLSITE is processed (rate-limited)
            if not hasattr(self, '_last_tick_process_log_utc'):
                self._last_tick_process_log_utc = None
            now = datetime.now(timezone.utc)
            if self._last_tick_process_log_utc is None or (now - self._last_tick_process_log_utc).total_seconds() >= 30:
                self._last_tick_process_log_utc = now
                logger.info(f"ENGINE_TICK_CALLSITE processed: timestamp_utc={timestamp_utc.isoformat()}")
            self._state_manager.update_engine_tick(timestamp_utc)
        
        elif event_type == "IDENTITY_INVARIANTS_STATUS":
            # PHASE 3.1: Update identity invariants status
            pass_value = data.get("pass", False)
            violations = data.get("violations", [])
            canonical_instrument = data.get("canonical_instrument", "")
            execution_instrument = data.get("execution_instrument", "")
            stream_ids = data.get("stream_ids", [])
            checked_at_utc_str = data.get("checked_at_utc", "")
            
            checked_at_utc = self._parse_timestamp(checked_at_utc_str) if checked_at_utc_str else timestamp_utc
            
            self._state_manager.update_identity_invariants(
                pass_value=pass_value,
                violations=violations,
                canonical_instrument=canonical_instrument,
                execution_instrument=execution_instrument,
                stream_ids=stream_ids,
                checked_at_utc=checked_at_utc
            )
        
        elif event_type == "ENGINE_TICK_STALL_DETECTED":
            # Engine stall detected - state manager will compute engine_alive as False
            pass
        
        elif event_type == "ENGINE_TICK_STALL_RECOVERED":
            self._state_manager.update_engine_tick(timestamp_utc)
        
        elif event_type in ("DISCONNECT_FAIL_CLOSED_ENTERED", "DISCONNECT_RECOVERY_STARTED",
                           "DISCONNECT_RECOVERY_COMPLETE", "DISCONNECT_RECOVERY_ABORTED"):
            self._state_manager.update_recovery_state(event_type, timestamp_utc)
        
        elif event_type in ("CONNECTION_LOST", "CONNECTION_LOST_SUSTAINED", "CONNECTION_RECOVERED"):
            connection_status = "ConnectionLost" if "LOST" in event_type else "Connected"
            self._state_manager.update_connection_status(connection_status, timestamp_utc)
        
        elif event_type == "KILL_SWITCH_ACTIVE":
            self._state_manager.update_kill_switch(True)
        
        elif event_type == "STREAM_STATE_TRANSITION":
            # CRITICAL: Use timetable's trading_date (authoritative), not event's trading_date
            # The timetable is the single source of truth for trading_date
            trading_date = self._state_manager.get_trading_date()
            if not trading_date:
                # Fallback: If timetable not loaded yet, skip this event (will be processed on next poll)
                logger.debug(
                    f"STREAM_STATE_TRANSITION skipped: timetable trading_date not set yet "
                    f"(event trading_date: {event.get('trading_date')}, stream: {event.get('stream')})"
                )
                return
            
            stream = event.get("stream")
            instrument = event.get("instrument")
            execution_instrument = event.get("execution_instrument")  # PHASE 3: Robot may emit both
            canonical_instrument_field = event.get("canonical_instrument")  # PHASE 3: Robot may emit both
            session = event.get("session")
            slot_time_chicago = event.get("slot_time_chicago") or data.get("slot_time_chicago")
            previous_state = data.get("previous_state")
            new_state = data.get("new_state")
            state_entry_time_utc_str = data.get("state_entry_time_utc")
            
            # PHASE 3: Trust robot canonical fields if present, otherwise canonicalize
            if canonical_instrument_field:
                # Robot already emitted canonical instrument - trust it
                canonical_instrument = canonical_instrument_field
                canonical_stream = stream  # Stream should already be canonical from robot
                if not execution_instrument:
                    # Fallback: if execution_instrument missing, try to infer from instrument field
                    execution_instrument = instrument if instrument != canonical_instrument else instrument
            elif execution_instrument:
                # Robot emitted execution instrument - canonicalize it
                canonical_instrument = get_canonical_instrument(execution_instrument)
                canonical_stream = canonicalize_stream(stream, execution_instrument) if stream else stream
            elif instrument:
                # Legacy: only instrument field present - canonicalize it
                canonical_instrument = get_canonical_instrument(instrument)
                canonical_stream = canonicalize_stream(stream, instrument) if stream else stream
                execution_instrument = instrument  # Assume execution if not specified
            else:
                # No instrument fields - use as-is (should not happen)
                canonical_instrument = instrument or ""
                canonical_stream = stream or ""
                execution_instrument = instrument or ""
            
            if canonical_stream and new_state:
                # Parse state_entry_time_utc from event, but validate it's not too old
                state_entry_time_utc = self._parse_timestamp(state_entry_time_utc_str) if state_entry_time_utc_str else timestamp_utc
                
                # Safety check: If state_entry_time_utc is more than 5 minutes old, use current time instead
                # This prevents streams from showing incorrect "time in state" due to old event timestamps
                if state_entry_time_utc:
                    age_seconds = (timestamp_utc - state_entry_time_utc).total_seconds()
                    if age_seconds > 300:  # More than 5 minutes old
                        logger.warning(
                            f"Stream state transition has old timestamp (age: {age_seconds:.0f}s), using current time: "
                            f"{canonical_stream} ({trading_date}) {previous_state} -> {new_state}"
                        )
                        state_entry_time_utc = timestamp_utc
                
                # Log state transition for debugging
                logger.info(
                    f"✅ Stream state transition: {canonical_stream} ({trading_date}) "
                    f"{previous_state} -> {new_state} (execution_instrument={execution_instrument}, "
                    f"canonical_instrument={canonical_instrument}, session={session}, slot={slot_time_chicago})"
                )
                
                # PHASE 2: Use canonical stream ID for state management
                self._state_manager.update_stream_state(
                    trading_date, canonical_stream, new_state,
                    state_entry_time_utc=state_entry_time_utc
                )
                
                # Diagnostic: Log stream count after update
                stream_count = len(self._state_manager._stream_states)
                logger.debug(
                    f"Stream state updated: {canonical_stream} ({trading_date}) -> {new_state}. "
                    f"Total streams in state manager: {stream_count}"
                )
                # Update instrument, session, slot_time_chicago, and range data if available
                key = (trading_date, canonical_stream)
                if key in self._state_manager._stream_states:
                    info = self._state_manager._stream_states[key]
                    if canonical_instrument:
                        info.instrument = canonical_instrument
                    if session:
                        info.session = session
                    if slot_time_chicago:
                        info.slot_time_chicago = slot_time_chicago
                    
                    # CRITICAL: Clear ranges when transitioning away from RANGE_LOCKED
                    # This prevents old ranges from persisting when streams restart or transition to new states
                    if info.state == "RANGE_LOCKED" and new_state != "RANGE_LOCKED":
                        logger.debug(
                            f"Clearing ranges for stream {canonical_stream} ({trading_date}): "
                            f"transitioning from RANGE_LOCKED to {new_state}"
                        )
                        info.range_high = None
                        info.range_low = None
                        info.freeze_close = None
                        info.range_invalidated = False
                    
                    # Extract range values from data dict if transitioning to RANGE_LOCKED
                    if new_state == "RANGE_LOCKED":
                        # Range data might be in data directly, or nested in extra_data (dict or string)
                        extra_data = data.get("extra_data", {})
                        range_high = data.get("range_high")
                        range_low = data.get("range_low")
                        freeze_close = data.get("freeze_close")
                        
                        # If range values not found, try to extract from extra_data
                        if (range_high is None or range_low is None) and extra_data:
                            if isinstance(extra_data, dict):
                                # extra_data is a dict - extract directly
                                range_high = range_high or extra_data.get("range_high")
                                range_low = range_low or extra_data.get("range_low")
                                freeze_close = freeze_close or extra_data.get("freeze_close")
                            elif isinstance(extra_data, str):
                                # extra_data is a string (C# anonymous object serialized)
                                # Parse format: "{ range_high = 49564, range_low = 49090, ... }"
                                try:
                                    # Extract range_high
                                    high_match = re.search(r'range_high\s*=\s*([0-9.]+)', extra_data)
                                    if high_match and range_high is None:
                                        range_high = float(high_match.group(1))
                                    # Extract range_low
                                    low_match = re.search(r'range_low\s*=\s*([0-9.]+)', extra_data)
                                    if low_match and range_low is None:
                                        range_low = float(low_match.group(1))
                                    # Extract freeze_close
                                    freeze_match = re.search(r'freeze_close\s*=\s*([0-9.]+)', extra_data)
                                    if freeze_match and freeze_close is None:
                                        freeze_close = float(freeze_match.group(1))
                                except Exception as e:
                                    logger.debug(f"Failed to parse extra_data string in STREAM_STATE_TRANSITION: {e}")
                                    pass
                        slot_time_utc_str = event.get("slot_time_utc") or data.get("slot_time_utc")
                        
                        if slot_time_utc_str:
                            info.slot_time_utc = slot_time_utc_str
                        if range_high is not None:
                            info.range_high = float(range_high) if range_high is not None else None
                        if range_low is not None:
                            info.range_low = float(range_low) if range_low is not None else None
                        if freeze_close is not None:
                            info.freeze_close = float(freeze_close) if freeze_close is not None else None
        
        elif event_type in ("STREAM_STAND_DOWN", "MARKET_CLOSE_NO_TRADE"):
            # Standardized fields are now always at top level (plan requirement #1)
            # MARKET_CLOSE_NO_TRADE is treated the same as STREAM_STAND_DOWN
            # CRITICAL: Use timetable's trading_date (authoritative), not event's trading_date
            trading_date = self._state_manager.get_trading_date()
            if not trading_date:
                logger.debug(f"{event_type} skipped: timetable trading_date not set yet")
                return
            stream = event.get("stream")
            instrument = event.get("instrument")
            execution_instrument = event.get("execution_instrument")  # PHASE 3: Robot may emit both
            canonical_instrument_field = event.get("canonical_instrument")  # PHASE 3: Robot may emit both
            
            # PHASE 3: Trust robot canonical fields if present, otherwise canonicalize
            if canonical_instrument_field:
                canonical_instrument = canonical_instrument_field
                canonical_stream = stream
                if not execution_instrument:
                    execution_instrument = instrument if instrument != canonical_instrument else instrument
            elif execution_instrument:
                canonical_instrument = get_canonical_instrument(execution_instrument)
                canonical_stream = canonicalize_stream(stream, execution_instrument) if stream else stream
            elif instrument:
                canonical_instrument = get_canonical_instrument(instrument)
                canonical_stream = canonicalize_stream(stream, instrument) if stream else stream
                execution_instrument = instrument
            else:
                canonical_instrument = instrument or ""
                canonical_stream = stream or ""
                execution_instrument = instrument or ""
            
            if canonical_stream:
                # PHASE 2: Use canonical stream ID for state management
                # For MARKET_CLOSE_NO_TRADE, use commit_reason from data, or default to event_type
                commit_reason = data.get("commit_reason") or data.get("reason") or event_type
                self._state_manager.update_stream_state(
                    trading_date, canonical_stream, "DONE", committed=True,
                    commit_reason=commit_reason
                )
                # Update instrument info if available
                if canonical_instrument:
                    key = (trading_date, canonical_stream)
                    if key in self._state_manager._stream_states:
                        self._state_manager._stream_states[key].instrument = canonical_instrument
        
        elif event_type == "RANGE_INVALIDATED":
            # Standardized fields are now always at top level (plan requirement #1)
            # CRITICAL: Use timetable's trading_date (authoritative), not event's trading_date
            trading_date = self._state_manager.get_trading_date()
            if not trading_date:
                logger.debug(f"RANGE_INVALIDATED skipped: timetable trading_date not set yet")
                return
            stream = event.get("stream")
            instrument = event.get("instrument")
            execution_instrument = event.get("execution_instrument")  # PHASE 3: Robot may emit both
            canonical_instrument_field = event.get("canonical_instrument")  # PHASE 3: Robot may emit both
            
            # PHASE 3: Trust robot canonical fields if present, otherwise canonicalize
            if canonical_instrument_field:
                canonical_instrument = canonical_instrument_field
                canonical_stream = stream
                if not execution_instrument:
                    execution_instrument = instrument if instrument != canonical_instrument else instrument
            elif execution_instrument:
                canonical_instrument = get_canonical_instrument(execution_instrument)
                canonical_stream = canonicalize_stream(stream, execution_instrument) if stream else stream
            elif instrument:
                canonical_instrument = get_canonical_instrument(instrument)
                canonical_stream = canonicalize_stream(stream, instrument) if stream else stream
                execution_instrument = instrument
            else:
                canonical_instrument = instrument or ""
                canonical_stream = stream or ""
                execution_instrument = instrument or ""
            
            if canonical_stream:
                # PHASE 2: Use canonical stream ID for state management
                self._state_manager.update_stream_state(
                    trading_date, canonical_stream, "DONE", committed=True,
                    commit_reason="RANGE_INVALIDATED"
                )
                # Update instrument info and mark range as invalidated
                key = (trading_date, canonical_stream)
                if key in self._state_manager._stream_states:
                    info = self._state_manager._stream_states[key]
                    if canonical_instrument:
                        info.instrument = canonical_instrument
                    info.range_invalidated = True
        
        elif event_type == "RANGE_LOCKED":
            # Standardized fields are now always at top level (plan requirement #1)
            # CRITICAL: Use timetable's trading_date (authoritative), not event's trading_date
            trading_date = self._state_manager.get_trading_date()
            if not trading_date:
                logger.debug(f"RANGE_LOCKED skipped: timetable trading_date not set yet")
                return
            stream = event.get("stream")
            instrument = event.get("instrument")
            execution_instrument = event.get("execution_instrument")  # PHASE 3: Robot may emit both
            canonical_instrument_field = event.get("canonical_instrument")  # PHASE 3: Robot may emit both
            session = event.get("session")
            slot_time_chicago = event.get("slot_time_chicago") or data.get("slot_time_chicago")
            slot_time_utc_str = event.get("slot_time_utc") or data.get("slot_time_utc")
            
            # PHASE 3: Trust robot canonical fields if present, otherwise canonicalize
            if canonical_instrument_field:
                canonical_instrument = canonical_instrument_field
                canonical_stream = stream
                if not execution_instrument:
                    execution_instrument = instrument if instrument != canonical_instrument else instrument
            elif execution_instrument:
                canonical_instrument = get_canonical_instrument(execution_instrument)
                canonical_stream = canonicalize_stream(stream, execution_instrument) if stream else stream
            elif instrument:
                canonical_instrument = get_canonical_instrument(instrument)
                canonical_stream = canonicalize_stream(stream, instrument) if stream else stream
                execution_instrument = instrument
            else:
                canonical_instrument = instrument or ""
                canonical_stream = stream or ""
                execution_instrument = instrument or ""
            
            # Extract range values from data dict
            # Range data might be in data directly, or nested in extra_data
            # extra_data can be a dict OR a string (C# anonymous object serialized as string)
            extra_data = data.get("extra_data", {})
            range_high = data.get("range_high")
            range_low = data.get("range_low")
            freeze_close = data.get("freeze_close")
            
            # If range values not found, try to extract from extra_data
            if (range_high is None or range_low is None) and extra_data:
                if isinstance(extra_data, dict):
                    # extra_data is a dict - extract directly
                    range_high = range_high or extra_data.get("range_high")
                    range_low = range_low or extra_data.get("range_low")
                    freeze_close = freeze_close or extra_data.get("freeze_close")
                elif isinstance(extra_data, str):
                    # extra_data is a string (C# anonymous object serialized)
                    # Parse format: "{ range_high = 49564, range_low = 49090, ... }"
                    try:
                        # Extract range_high
                        high_match = re.search(r'range_high\s*=\s*([0-9.]+)', extra_data)
                        if high_match and range_high is None:
                            range_high = float(high_match.group(1))
                        # Extract range_low
                        low_match = re.search(r'range_low\s*=\s*([0-9.]+)', extra_data)
                        if low_match and range_low is None:
                            range_low = float(low_match.group(1))
                        # Extract freeze_close
                        freeze_match = re.search(r'freeze_close\s*=\s*([0-9.]+)', extra_data)
                        if freeze_match and freeze_close is None:
                            freeze_close = float(freeze_match.group(1))
                    except Exception as e:
                        logger.debug(f"Failed to parse extra_data string: {e}")
                        pass
            
            if canonical_stream:
                # PHASE 2: Use canonical stream ID for state management
                # Pass event timestamp so state_entry_time_utc reflects when range was actually locked
                self._state_manager.update_stream_state(
                    trading_date, canonical_stream, "RANGE_LOCKED",
                    state_entry_time_utc=timestamp_utc
                )
                # Update instrument, session, slot_time, and range values
                key = (trading_date, canonical_stream)
                if key in self._state_manager._stream_states:
                    info = self._state_manager._stream_states[key]
                    if canonical_instrument:
                        info.instrument = canonical_instrument
                    if session:
                        info.session = session
                    if slot_time_chicago:
                        info.slot_time_chicago = slot_time_chicago
                    if slot_time_utc_str:
                        info.slot_time_utc = slot_time_utc_str
                    # Range values can be None (nullable decimals in C#)
                    if range_high is not None:
                        info.range_high = float(range_high) if range_high is not None else None
                    if range_low is not None:
                        info.range_low = float(range_low) if range_low is not None else None
                    if freeze_close is not None:
                        info.freeze_close = float(freeze_close) if freeze_close is not None else None
        
        elif event_type == "RANGE_LOCK_SNAPSHOT":
            # Handle RANGE_LOCK_SNAPSHOT events as fallback/update source for range data
            # This ensures range data is captured even if RANGE_LOCKED event doesn't have all fields
            # CRITICAL: Use timetable's trading_date (authoritative), not event's trading_date
            trading_date = self._state_manager.get_trading_date()
            if not trading_date:
                logger.debug(f"RANGE_LOCK_SNAPSHOT skipped: timetable trading_date not set yet")
                return
            stream = event.get("stream")
            instrument = event.get("instrument")
            execution_instrument = event.get("execution_instrument")
            canonical_instrument_field = event.get("canonical_instrument")
            session = event.get("session")
            slot_time_chicago = event.get("slot_time_chicago") or data.get("slot_time_chicago")
            slot_time_utc_str = event.get("slot_time_utc") or data.get("slot_time_utc")
            
            # PHASE 3: Trust robot canonical fields if present, otherwise canonicalize
            if canonical_instrument_field:
                canonical_instrument = canonical_instrument_field
                canonical_stream = stream
                if not execution_instrument:
                    execution_instrument = instrument if instrument != canonical_instrument else instrument
            elif execution_instrument:
                canonical_instrument = get_canonical_instrument(execution_instrument)
                canonical_stream = canonicalize_stream(stream, execution_instrument) if stream else stream
            elif instrument:
                canonical_instrument = get_canonical_instrument(instrument)
                canonical_stream = canonicalize_stream(stream, instrument) if stream else stream
                execution_instrument = instrument
            else:
                canonical_instrument = instrument or ""
                canonical_stream = stream or ""
                execution_instrument = instrument or ""
            
            # Extract range values from data dict
            range_high = data.get("range_high")
            range_low = data.get("range_low")
            freeze_close = data.get("freeze_close")
            
            if canonical_stream:
                # Update existing stream state if it exists, or create new one
                key = (trading_date, canonical_stream)
                if key in self._state_manager._stream_states:
                    info = self._state_manager._stream_states[key]
                    # Only update if state is RANGE_LOCKED or we're creating it
                    if info.state == "RANGE_LOCKED" or info.state == "":
                        if canonical_instrument:
                            info.instrument = canonical_instrument
                        if session:
                            info.session = session
                        if slot_time_chicago:
                            info.slot_time_chicago = slot_time_chicago
                        if slot_time_utc_str:
                            info.slot_time_utc = slot_time_utc_str
                        # Range values can be None (nullable decimals in C#)
                        if range_high is not None:
                            info.range_high = float(range_high) if range_high is not None else None
                        if range_low is not None:
                            info.range_low = float(range_low) if range_low is not None else None
                        if freeze_close is not None:
                            info.freeze_close = float(freeze_close) if freeze_close is not None else None
                else:
                    # Create new stream state if it doesn't exist
                    self._state_manager.update_stream_state(
                        trading_date, canonical_stream, "RANGE_LOCKED",
                        state_entry_time_utc=timestamp_utc
                    )
                    if key in self._state_manager._stream_states:
                        info = self._state_manager._stream_states[key]
                        if canonical_instrument:
                            info.instrument = canonical_instrument
                        if session:
                            info.session = session
                        if slot_time_chicago:
                            info.slot_time_chicago = slot_time_chicago
                        if slot_time_utc_str:
                            info.slot_time_utc = slot_time_utc_str
                        if range_high is not None:
                            info.range_high = float(range_high) if range_high is not None else None
                        if range_low is not None:
                            info.range_low = float(range_low) if range_low is not None else None
                        if freeze_close is not None:
                            info.freeze_close = float(freeze_close) if freeze_close is not None else None
        
        elif event_type == "EXECUTION_BLOCKED":
            self._state_manager.record_execution_blocked(timestamp_utc)
        
        elif event_type == "PROTECTIVE_ORDERS_FAILED_FLATTENED":
            self._state_manager.record_protective_failure(timestamp_utc)
        
        elif event_type == "PROTECTIVE_ORDERS_SUBMITTED":
            intent_id = data.get("intent_id")
            if intent_id:
                self._state_manager.record_protective_order_submitted(intent_id, timestamp_utc)
        
        elif event_type == "INTENT_EXPOSURE_REGISTERED":
            intent_id = data.get("intent_id")
            # Standardized fields are now always at top level (plan requirement #1)
            # stream_id may be in data for execution events, but stream should be at top level
            stream_id = event.get("stream") or data.get("stream_id")
            instrument = event.get("instrument")
            execution_instrument = event.get("execution_instrument")  # PHASE 3: Robot may emit both
            canonical_instrument_field = event.get("canonical_instrument")  # PHASE 3: Robot may emit both
            direction = data.get("direction")
            entry_filled_qty = data.get("entry_filled_qty", 0)
            
            # PHASE 3: Trust robot canonical fields if present, otherwise canonicalize
            if canonical_instrument_field and stream_id:
                canonical_instrument = canonical_instrument_field
                canonical_stream_id = stream_id  # Stream should already be canonical
                if not execution_instrument:
                    execution_instrument = instrument if instrument != canonical_instrument else instrument
            elif execution_instrument and stream_id:
                canonical_instrument = get_canonical_instrument(execution_instrument)
                canonical_stream_id = canonicalize_stream(stream_id, execution_instrument)
            elif instrument and stream_id:
                canonical_instrument = get_canonical_instrument(instrument)
                canonical_stream_id = canonicalize_stream(stream_id, instrument)
                execution_instrument = instrument
            else:
                canonical_instrument = instrument or ""
                canonical_stream_id = stream_id or ""
                execution_instrument = instrument or ""
            
            if intent_id and canonical_stream_id and canonical_instrument and direction:
                # PHASE 2: Use canonical stream ID and instrument for intent exposure
                self._state_manager.update_intent_exposure(
                    intent_id, canonical_stream_id, canonical_instrument, direction,
                    entry_filled_qty=entry_filled_qty,
                    state="ACTIVE",
                    entry_filled_at_utc=timestamp_utc
                )
        
        elif event_type == "INTENT_EXPOSURE_CLOSED":
            intent_id = data.get("intent_id")
            if intent_id and intent_id in self._state_manager._intent_exposures:
                exposure = self._state_manager._intent_exposures[intent_id]
                exposure.state = "CLOSED"
        
        elif event_type == "INTENT_EXIT_FILL":
            intent_id = data.get("intent_id")
            exit_filled_qty = data.get("exit_filled_qty", 0)
            if intent_id and intent_id in self._state_manager._intent_exposures:
                exposure = self._state_manager._intent_exposures[intent_id]
                exposure.exit_filled_qty = exit_filled_qty
                if exposure.exit_filled_qty >= exposure.entry_filled_qty:
                    exposure.state = "CLOSED"
        
        elif event_type == "BAR_ACCEPTED":
            # Standardized fields are now always at top level (plan requirement #1)
            instrument = event.get("instrument")
            if instrument:
                self._state_manager.update_last_bar(instrument, timestamp_utc)
        
        elif event_type == "DATA_LOSS_DETECTED":
            instrument = event.get("instrument")
            if instrument:
                self._state_manager.mark_data_loss(instrument, timestamp_utc)
        
        elif event_type == "DATA_STALL_RECOVERED":
            instrument = event.get("instrument")
            if instrument:
                self._state_manager.update_last_bar(instrument, timestamp_utc)
        
        elif event_type == "TIMETABLE_VALIDATED":
            # NOTE: Timetable validation is now based on timetable_current.json polling,
            # not on TIMETABLE_VALIDATED events from the robot.
            # This event is kept for compatibility but no longer updates timetable_validated.
            # The timetable_validated status is set by update_timetable_streams() based on
            # whether the timetable file was successfully loaded.
            logger.debug(
                f"TIMETABLE_VALIDATED event received (ignored - validation based on timetable_current.json polling)"
            )
    
    def get_last_processed_seq(self, run_id: str) -> int:
        """Get last processed event_seq for a run_id."""
        return self._last_processed_seq.get(run_id, 0)
