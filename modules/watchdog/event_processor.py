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
        
        elif event_type == "ENGINE_TICK_HEARTBEAT":
            self._state_manager.update_engine_tick(timestamp_utc)
        
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
            previous_state = data.get("previous_state")
            new_state = data.get("new_state")
            state_entry_time_utc_str = data.get("state_entry_time_utc")
            
            if trading_date and stream and new_state:
                state_entry_time_utc = self._parse_timestamp(state_entry_time_utc_str) if state_entry_time_utc_str else timestamp_utc
                self._state_manager.update_stream_state(
                    trading_date, stream, new_state,
                    state_entry_time_utc=state_entry_time_utc
                )
                # Update instrument info if available
                if instrument:
                    key = (trading_date, stream)
                    if key in self._state_manager._stream_states:
                        self._state_manager._stream_states[key].instrument = instrument
        
        elif event_type == "STREAM_STAND_DOWN":
            # Standardized fields are now always at top level (plan requirement #1)
            trading_date = event.get("trading_date")
            stream = event.get("stream")
            instrument = event.get("instrument")
            if trading_date and stream:
                self._state_manager.update_stream_state(
                    trading_date, stream, "DONE", committed=True,
                    commit_reason=data.get("reason")
                )
                # Update instrument info if available
                if instrument:
                    key = (trading_date, stream)
                    if key in self._state_manager._stream_states:
                        self._state_manager._stream_states[key].instrument = instrument
        
        elif event_type == "RANGE_INVALIDATED":
            # Standardized fields are now always at top level (plan requirement #1)
            trading_date = event.get("trading_date")
            stream = event.get("stream")
            instrument = event.get("instrument")
            if trading_date and stream:
                self._state_manager.update_stream_state(
                    trading_date, stream, "DONE", committed=True,
                    commit_reason="RANGE_INVALIDATED"
                )
                # Update instrument info if available
                if instrument:
                    key = (trading_date, stream)
                    if key in self._state_manager._stream_states:
                        self._state_manager._stream_states[key].instrument = instrument
        
        elif event_type == "RANGE_LOCKED":
            # Standardized fields are now always at top level (plan requirement #1)
            trading_date = event.get("trading_date")
            stream = event.get("stream")
            instrument = event.get("instrument")
            if trading_date and stream:
                self._state_manager.update_stream_state(
                    trading_date, stream, "RANGE_LOCKED"
                )
                # Update instrument info if available
                if instrument:
                    key = (trading_date, stream)
                    if key in self._state_manager._stream_states:
                        self._state_manager._stream_states[key].instrument = instrument
        
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
            direction = data.get("direction")
            entry_filled_qty = data.get("entry_filled_qty", 0)
            
            if intent_id and stream_id and instrument and direction:
                self._state_manager.update_intent_exposure(
                    intent_id, stream_id, instrument, direction,
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
        
        elif event_type == "TIMETABLE_VALIDATED":
            # Standardized fields are now always at top level (plan requirement #1)
            trading_date = event.get("trading_date")
            self._state_manager.update_timetable_state(True, trading_date)
    
    def get_last_processed_seq(self, run_id: str) -> int:
        """Get last processed event_seq for a run_id."""
        return self._last_processed_seq.get(run_id, 0)
