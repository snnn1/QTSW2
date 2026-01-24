"""
State Manager for Watchdog Aggregator

Maintains in-memory state for derived fields and cursor management.
"""
import json
import logging
from pathlib import Path
from typing import Dict, List, Optional, Set
from datetime import datetime, timezone, timedelta
from collections import defaultdict
import pytz

from .config import (
    FRONTEND_CURSOR_FILE,
    ENGINE_TICK_STALL_THRESHOLD_SECONDS,
    STUCK_STREAM_THRESHOLD_SECONDS,
    UNPROTECTED_TIMEOUT_SECONDS,
    DATA_STALL_THRESHOLD_SECONDS,
)

logger = logging.getLogger(__name__)

CHICAGO_TZ = pytz.timezone("America/Chicago")


class WatchdogStateManager:
    """Manages watchdog state and derived fields."""
    
    def __init__(self):
        # Engine state
        self._last_engine_tick_utc: Optional[datetime] = None
        self._recovery_state: str = "CONNECTED_OK"
        self._kill_switch_active: bool = False
        self._connection_status: str = "Connected"
        self._last_connection_event_utc: Optional[datetime] = None
        
        # Stream states: (trading_date, stream) -> StreamStateInfo
        self._stream_states: Dict[tuple, 'StreamStateInfo'] = {}
        
        # Intent exposures: intent_id -> IntentExposureInfo
        self._intent_exposures: Dict[str, 'IntentExposureInfo'] = {}
        
        # Protective order events: intent_id -> Set[event_timestamp]
        self._protective_events: Dict[str, Set[str]] = defaultdict(set)
        
        # Event counts (last 1 hour window)
        self._execution_blocked_events: List[datetime] = []
        self._protective_failure_events: List[datetime] = []
        
        # Data stall tracking: instrument -> last_bar_utc
        self._last_bar_utc_by_instrument: Dict[str, datetime] = {}
        
        # Timetable state
        self._timetable_validated: bool = False
        self._trading_date: Optional[str] = None
        
        # Risk gate state
        self._allowed_slot_times: List[str] = []  # From timetable config
        
    def update_engine_tick(self, timestamp_utc: datetime):
        """Update engine tick timestamp."""
        self._last_engine_tick_utc = timestamp_utc
    
    def update_recovery_state(self, state: str, timestamp_utc: datetime):
        """Update recovery state."""
        # Map event types to recovery state enum values
        state_mapping = {
            "DISCONNECT_FAIL_CLOSED_ENTERED": "DISCONNECT_FAIL_CLOSED",
            "DISCONNECT_RECOVERY_STARTED": "RECOVERY_RUNNING",
            "DISCONNECT_RECOVERY_COMPLETE": "RECOVERY_COMPLETE",
            "DISCONNECT_RECOVERY_ABORTED": "DISCONNECT_FAIL_CLOSED",
        }
        self._recovery_state = state_mapping.get(state, state)
    
    def update_kill_switch(self, active: bool):
        """Update kill switch status."""
        self._kill_switch_active = active
    
    def update_connection_status(self, status: str, timestamp_utc: datetime):
        """Update connection status."""
        self._connection_status = status
        self._last_connection_event_utc = timestamp_utc
    
    def update_stream_state(self, trading_date: str, stream: str, state: str, 
                          committed: bool = False, commit_reason: Optional[str] = None,
                          state_entry_time_utc: Optional[datetime] = None):
        """Update stream state."""
        key = (trading_date, stream)
        if key not in self._stream_states:
            self._stream_states[key] = StreamStateInfo(
                trading_date=trading_date,
                stream=stream,
                state=state,
                committed=committed,
                commit_reason=commit_reason,
                state_entry_time_utc=state_entry_time_utc or datetime.now(timezone.utc)
            )
        else:
            info = self._stream_states[key]
            if info.state != state:
                info.state = state
                info.state_entry_time_utc = state_entry_time_utc or datetime.now(timezone.utc)
            info.committed = committed
            if commit_reason:
                info.commit_reason = commit_reason
    
    def update_intent_exposure(self, intent_id: str, stream_id: str, instrument: str,
                              direction: str, entry_filled_qty: int = 0, exit_filled_qty: int = 0,
                              state: str = "ACTIVE", entry_filled_at_utc: Optional[datetime] = None):
        """Update intent exposure."""
        if intent_id not in self._intent_exposures:
            self._intent_exposures[intent_id] = IntentExposureInfo(
                intent_id=intent_id,
                stream_id=stream_id,
                instrument=instrument,
                direction=direction,
                entry_filled_qty=entry_filled_qty,
                exit_filled_qty=exit_filled_qty,
                state=state,
                entry_filled_at_utc=entry_filled_at_utc
            )
        else:
            info = self._intent_exposures[intent_id]
            info.entry_filled_qty = entry_filled_qty
            info.exit_filled_qty = exit_filled_qty
            info.state = state
            if entry_filled_at_utc:
                info.entry_filled_at_utc = entry_filled_at_utc
    
    def record_protective_order_submitted(self, intent_id: str, timestamp_utc: datetime):
        """Record protective order submission."""
        self._protective_events[intent_id].add(timestamp_utc.isoformat())
    
    def record_execution_blocked(self, timestamp_utc: datetime):
        """Record execution blocked event."""
        self._execution_blocked_events.append(timestamp_utc)
        # Keep only last 1 hour
        cutoff = datetime.now(timezone.utc) - timedelta(hours=1)
        self._execution_blocked_events = [e for e in self._execution_blocked_events if e > cutoff]
    
    def record_protective_failure(self, timestamp_utc: datetime):
        """Record protective order failure."""
        self._protective_failure_events.append(timestamp_utc)
        # Keep only last 1 hour
        cutoff = datetime.now(timezone.utc) - timedelta(hours=1)
        self._protective_failure_events = [e for e in self._protective_failure_events if e > cutoff]
    
    def update_last_bar(self, instrument: str, timestamp_utc: datetime):
        """Update last bar timestamp for instrument."""
        self._last_bar_utc_by_instrument[instrument] = timestamp_utc
    
    def update_timetable_state(self, validated: bool, trading_date: Optional[str] = None):
        """Update timetable validation state."""
        self._timetable_validated = validated
        if trading_date:
            self._trading_date = trading_date
    
    def compute_engine_alive(self) -> bool:
        """Compute engine_alive derived field."""
        if self._last_engine_tick_utc is None:
            return False
        
        now = datetime.now(timezone.utc)
        elapsed = (now - self._last_engine_tick_utc).total_seconds()
        return elapsed < ENGINE_TICK_STALL_THRESHOLD_SECONDS
    
    def compute_stuck_streams(self) -> List[Dict]:
        """Compute stuck streams derived field."""
        stuck_streams = []
        now = datetime.now(timezone.utc)
        
        for (trading_date, stream), info in self._stream_states.items():
            if info.state == "DONE" or info.committed:
                continue
            
            stuck_duration = (now - info.state_entry_time_utc).total_seconds()
            if stuck_duration > STUCK_STREAM_THRESHOLD_SECONDS:
                stuck_streams.append({
                    "stream": stream,
                    "instrument": info.instrument if hasattr(info, 'instrument') else "",
                    "state": info.state,
                    "stuck_duration_seconds": int(stuck_duration),
                    "state_entry_time_chicago": info.state_entry_time_utc.astimezone(CHICAGO_TZ).isoformat()
                })
        
        return stuck_streams
    
    def compute_risk_gate_status(self) -> Dict:
        """Compute risk gate status derived field."""
        recovery_state_allowed = self._recovery_state in ("CONNECTED_OK", "RECOVERY_COMPLETE")
        kill_switch_allowed = not self._kill_switch_active
        
        stream_armed = []
        for (trading_date, stream), info in self._stream_states.items():
            stream_armed.append({
                "stream": stream,
                "armed": not info.committed and info.state != "DONE"
            })
        
        return {
            "recovery_state_allowed": recovery_state_allowed,
            "kill_switch_allowed": kill_switch_allowed,
            "timetable_validated": self._timetable_validated,
            "stream_armed": stream_armed,
            "session_slot_time_valid": True,  # TODO: Implement slot time validation
            "trading_date_set": self._trading_date is not None and self._trading_date != ""
        }
    
    def compute_watchdog_status(self) -> Dict:
        """Compute watchdog status derived field."""
        engine_alive = self.compute_engine_alive()
        stuck_streams = self.compute_stuck_streams()
        
        now = datetime.now(timezone.utc)
        
        data_stall_detected = {}
        for instrument, last_bar_utc in self._last_bar_utc_by_instrument.items():
            elapsed = (now - last_bar_utc).total_seconds()
            stall_detected = elapsed > DATA_STALL_THRESHOLD_SECONDS
            data_stall_detected[instrument] = {
                "instrument": instrument,
                "last_bar_chicago": last_bar_utc.astimezone(CHICAGO_TZ).isoformat(),
                "stall_detected": stall_detected
            }
        
        return {
            "engine_alive": engine_alive,
            "last_engine_tick_chicago": (
                self._last_engine_tick_utc.astimezone(CHICAGO_TZ).isoformat()
                if self._last_engine_tick_utc else None
            ),
            "engine_tick_stall_detected": not engine_alive,
            "recovery_state": self._recovery_state,
            "kill_switch_active": self._kill_switch_active,
            "connection_status": self._connection_status,
            "last_connection_event_chicago": (
                self._last_connection_event_utc.astimezone(CHICAGO_TZ).isoformat()
                if self._last_connection_event_utc else None
            ),
            "stuck_streams": stuck_streams,
            "execution_blocked_count": len(self._execution_blocked_events),
            "protective_failures_count": len(self._protective_failure_events),
            "data_stall_detected": data_stall_detected
        }
    
    def compute_unprotected_positions(self) -> List[Dict]:
        """Compute unprotected positions derived field."""
        unprotected = []
        now = datetime.now(timezone.utc)
        
        for intent_id, exposure in self._intent_exposures.items():
            if exposure.state != "ACTIVE":
                continue
            
            # Check if protective orders were submitted
            protective_acknowledged = len(self._protective_events.get(intent_id, set())) > 0
            
            if not protective_acknowledged and exposure.entry_filled_at_utc:
                timeout_seconds = (now - exposure.entry_filled_at_utc).total_seconds()
                if timeout_seconds > UNPROTECTED_TIMEOUT_SECONDS:
                    unprotected.append({
                        "intent_id": intent_id,
                        "stream": exposure.stream_id,
                        "instrument": exposure.instrument,
                        "direction": exposure.direction,
                        "entry_filled_at_chicago": exposure.entry_filled_at_utc.astimezone(CHICAGO_TZ).isoformat(),
                        "unprotected_duration_seconds": int(timeout_seconds)
                    })
        
        return unprotected


class StreamStateInfo:
    """Information about a stream's state."""
    def __init__(self, trading_date: str, stream: str, state: str,
                 committed: bool = False, commit_reason: Optional[str] = None,
                 state_entry_time_utc: Optional[datetime] = None):
        self.trading_date = trading_date
        self.stream = stream
        self.state = state
        self.committed = committed
        self.commit_reason = commit_reason
        self.state_entry_time_utc = state_entry_time_utc or datetime.now(timezone.utc)
        self.instrument = ""  # Will be populated from events


class IntentExposureInfo:
    """Information about an intent's exposure."""
    def __init__(self, intent_id: str, stream_id: str, instrument: str, direction: str,
                 entry_filled_qty: int = 0, exit_filled_qty: int = 0, state: str = "ACTIVE",
                 entry_filled_at_utc: Optional[datetime] = None):
        self.intent_id = intent_id
        self.stream_id = stream_id
        self.instrument = instrument
        self.direction = direction
        self.entry_filled_qty = entry_filled_qty
        self.exit_filled_qty = exit_filled_qty
        self.state = state
        self.entry_filled_at_utc = entry_filled_at_utc


class CursorManager:
    """Manages cursor state for incremental event tailing."""
    
    def __init__(self):
        self._cursor_file = FRONTEND_CURSOR_FILE
        self._cursor_file.parent.mkdir(parents=True, exist_ok=True)
    
    def load_cursor(self) -> Dict[str, int]:
        """Load cursor state from file."""
        if not self._cursor_file.exists():
            return {}
        
        try:
            with open(self._cursor_file, 'r') as f:
                return json.load(f)
        except Exception as e:
            logger.warning(f"Failed to load cursor: {e}")
            return {}
    
    def save_cursor(self, cursor: Dict[str, int]):
        """Save cursor state to file."""
        try:
            with open(self._cursor_file, 'w') as f:
                json.dump(cursor, f, indent=2)
        except Exception as e:
            logger.error(f"Failed to save cursor: {e}")
