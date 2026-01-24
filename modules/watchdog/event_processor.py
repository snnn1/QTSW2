"""
Event Processor

Processes events from frontend_feed.jsonl and updates state manager.
"""
import json
import logging
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
            # Clear stale streams on engine start (new run)
            # Keep only streams from today's trading date
            trading_date = event.get("trading_date")
            if trading_date:
                self._state_manager.cleanup_stale_streams(trading_date, timestamp_utc)
        
        elif event_type == "ENGINE_TICK_HEARTBEAT":
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
            # Standardized fields are now always at top level (plan requirement #1)
            trading_date = event.get("trading_date")
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
            
            if trading_date and canonical_stream and new_state:
                state_entry_time_utc = self._parse_timestamp(state_entry_time_utc_str) if state_entry_time_utc_str else timestamp_utc
                
                # Log state transition for debugging
                logger.debug(
                    f"Stream state transition: {canonical_stream} ({trading_date}) "
                    f"{previous_state} -> {new_state} (execution_instrument={execution_instrument}, "
                    f"canonical_instrument={canonical_instrument}, session={session}, slot={slot_time_chicago})"
                )
                
                # PHASE 2: Use canonical stream ID for state management
                self._state_manager.update_stream_state(
                    trading_date, canonical_stream, new_state,
                    state_entry_time_utc=state_entry_time_utc
                )
                # Update instrument, session, and slot_time_chicago if available
                key = (trading_date, canonical_stream)
                if key in self._state_manager._stream_states:
                    info = self._state_manager._stream_states[key]
                    if canonical_instrument:
                        info.instrument = canonical_instrument
                    if session:
                        info.session = session
                    if slot_time_chicago:
                        info.slot_time_chicago = slot_time_chicago
        
        elif event_type == "STREAM_STAND_DOWN":
            # Standardized fields are now always at top level (plan requirement #1)
            trading_date = event.get("trading_date")
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
            
            if trading_date and canonical_stream:
                # PHASE 2: Use canonical stream ID for state management
                self._state_manager.update_stream_state(
                    trading_date, canonical_stream, "DONE", committed=True,
                    commit_reason=data.get("reason")
                )
                # Update instrument info if available
                if canonical_instrument:
                    key = (trading_date, canonical_stream)
                    if key in self._state_manager._stream_states:
                        self._state_manager._stream_states[key].instrument = canonical_instrument
        
        elif event_type == "RANGE_INVALIDATED":
            # Standardized fields are now always at top level (plan requirement #1)
            trading_date = event.get("trading_date")
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
            
            if trading_date and canonical_stream:
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
            trading_date = event.get("trading_date")
            stream = event.get("stream")
            instrument = event.get("instrument")
            execution_instrument = event.get("execution_instrument")  # PHASE 3: Robot may emit both
            canonical_instrument_field = event.get("canonical_instrument")  # PHASE 3: Robot may emit both
            session = event.get("session")
            slot_time_chicago = event.get("slot_time_chicago") or data.get("slot_time_chicago")
            slot_time_utc_str = data.get("slot_time_utc")
            
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
            
            if trading_date and canonical_stream:
                # PHASE 2: Use canonical stream ID for state management
                self._state_manager.update_stream_state(
                    trading_date, canonical_stream, "RANGE_LOCKED"
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
            # Standardized fields are now always at top level (plan requirement #1)
            trading_date = event.get("trading_date")
            self._state_manager.update_timetable_state(True, trading_date)
    
    def get_last_processed_seq(self, run_id: str) -> int:
        """Get last processed event_seq for a run_id."""
        return self._last_processed_seq.get(run_id, 0)
