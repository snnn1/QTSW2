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
from .market_session import is_market_open

logger = logging.getLogger(__name__)

# Slot times - watchdog defines its own (standalone, no matrix dependency)
# These match the canonical trading time slots but watchdog owns this definition
SLOT_ENDS = {
    "S1": ["07:30", "08:00", "09:00"],
    "S2": ["09:30", "10:00", "10:30", "11:00"],
}

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
        
        # PHASE 3.1: Identity invariants status
        self._last_identity_invariants_pass: Optional[bool] = None
        self._last_identity_invariants_event_chicago: Optional[datetime] = None
        self._last_identity_violations: List[str] = []
        
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
    
    def update_identity_invariants(
        self,
        pass_value: bool,
        violations: List[str],
        canonical_instrument: str,
        execution_instrument: str,
        stream_ids: List[str],
        checked_at_utc: datetime
    ):
        """PHASE 3.1: Update identity invariants status."""
        self._last_identity_invariants_pass = pass_value
        self._last_identity_violations = violations.copy()
        
        # Convert UTC to Chicago time
        try:
            chicago_dt = checked_at_utc.astimezone(CHICAGO_TZ)
            self._last_identity_invariants_event_chicago = chicago_dt
        except Exception as e:
            logger.warning(f"Failed to convert identity invariants timestamp to Chicago: {e}")
            self._last_identity_invariants_event_chicago = None
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
    
    def mark_data_loss(self, instrument: str, timestamp_utc: datetime):
        """Mark instrument as having data loss."""
        # Clear last_bar_utc to indicate data loss
        # This will cause stall_detected to trigger if market is open
        if instrument in self._last_bar_utc_by_instrument:
            del self._last_bar_utc_by_instrument[instrument]
    
    def update_timetable_state(self, validated: bool, trading_date: Optional[str] = None):
        """Update timetable validation state."""
        self._timetable_validated = validated
        if trading_date:
            self._trading_date = trading_date
    
    def cleanup_stale_streams(self, current_trading_date: str, utc_now: datetime):
        """Clean up stale streams from previous runs or old trading dates."""
        # Remove streams from different trading dates
        keys_to_remove = []
        for (trading_date, stream), info in self._stream_states.items():
            # Always remove streams from different trading dates (stale from previous day)
            if trading_date != current_trading_date:
                logger.debug(
                    f"Removing stale stream from different trading date: {stream} "
                    f"(date: {trading_date}, current: {current_trading_date})"
                )
                keys_to_remove.append((trading_date, stream))
            # Also remove streams stuck in PRE_HYDRATION for > 2 hours (likely stale)
            elif info.state == "PRE_HYDRATION":
                stuck_duration = (utc_now - info.state_entry_time_utc).total_seconds()
                if stuck_duration > 2 * 60 * 60:  # 2 hours
                    logger.info(
                        f"Removing stale stream stuck in PRE_HYDRATION: {stream} "
                        f"(stuck for {stuck_duration/3600:.1f} hours, trading_date: {trading_date})"
                    )
                    keys_to_remove.append((trading_date, stream))
        
        for key in keys_to_remove:
            del self._stream_states[key]
        
        if keys_to_remove:
            logger.info(f"Cleaned up {len(keys_to_remove)} stale stream(s) (current_trading_date: {current_trading_date})")
    
    def bars_expected(self, instrument: str, market_open: bool) -> bool:
        """
        PATTERN 1: Determine if bars are expected for an instrument.
        Bars are expected if:
        - market_open == True, AND
        - at least one stream for that instrument is in a bar-dependent state.
        
        Bar-dependent states: PRE_HYDRATION, ARMED, RANGE_BUILDING, RANGE_LOCKED
        Excluded states: DONE, COMMITTED, NO_TRADE, INVALIDATED
        """
        if not market_open:
            return False
        
        # Check if any stream for this instrument is in a bar-dependent state
        bar_dependent_states = {"PRE_HYDRATION", "ARMED", "RANGE_BUILDING", "RANGE_LOCKED"}
        excluded_states = {"DONE", "COMMITTED", "NO_TRADE", "INVALIDATED"}
        
        for (trading_date, stream), info in self._stream_states.items():
            # Check if this stream is for the given instrument
            # Note: info.instrument may be canonical instrument
            if info.instrument == instrument or stream.upper().startswith(instrument.upper()):
                if info.state in bar_dependent_states and not info.committed:
                    return True
        
        return False
    
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
        
        # Check market status for market-aware stuck detection
        try:
            chicago_now = datetime.now(CHICAGO_TZ)
            market_open = is_market_open(chicago_now)
        except Exception as e:
            logger.warning(f"Error checking market status: {e}, defaulting to market_open=True")
            market_open = True  # Default to open to avoid missing stuck streams
        
        for (trading_date, stream), info in self._stream_states.items():
            if info.state == "DONE" or info.committed:
                continue
            
            stuck_duration = (now - info.state_entry_time_utc).total_seconds()
            
            # Priority 2: Market-aware stuck detection
            # Skip stuck detection for ARMED/RANGE_BUILDING if market is closed
            # (streams should commit at market close, but if they don't, that's a separate issue)
            if not market_open and info.state in ("ARMED", "RANGE_BUILDING"):
                continue  # Don't flag as stuck if market is closed
            
            # Special handling for PRE_HYDRATION: streams should transition within ~10 minutes
            # If stuck in PRE_HYDRATION for > 30 minutes, consider it stuck
            if info.state == "PRE_HYDRATION":
                pre_hydration_timeout = 30 * 60  # 30 minutes
                if stuck_duration > pre_hydration_timeout:
                    stuck_streams.append({
                        "stream": stream,
                        "instrument": info.instrument if hasattr(info, 'instrument') else "",
                        "state": info.state,
                        "stuck_duration_seconds": int(stuck_duration),
                        "state_entry_time_chicago": info.state_entry_time_utc.astimezone(CHICAGO_TZ).isoformat(),
                        "issue": "STUCK_IN_PRE_HYDRATION"
                    })
            # Priority 1: ARMED State Timeout Extension
            # ARMED streams can legitimately wait for bars or market open
            # Use longer timeout: 2 hours (only flag if market is open)
            elif info.state == "ARMED":
                armed_timeout = 2 * 60 * 60  # 2 hours
                if stuck_duration > armed_timeout:
                    # Only flag as stuck if market is open (waiting during market hours is suspicious)
                    if market_open:
                        stuck_streams.append({
                            "stream": stream,
                            "instrument": info.instrument if hasattr(info, 'instrument') else "",
                            "state": info.state,
                            "stuck_duration_seconds": int(stuck_duration),
                            "state_entry_time_chicago": info.state_entry_time_utc.astimezone(CHICAGO_TZ).isoformat(),
                            "issue": "STUCK_IN_ARMED"
                        })
            elif stuck_duration > STUCK_STREAM_THRESHOLD_SECONDS:
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
            # PATTERN 1: A stream is "armed" only when it's in the ARMED state
            # Not when it's in PRE_HYDRATION, RANGE_BUILDING, etc.
            stream_armed.append({
                "stream": stream,
                "armed": info.state == "ARMED" and not info.committed
            })
        
        # Validate slot time against allowed slot times for each stream's session
        session_slot_time_valid = True
        if self._stream_states:
            for (trading_date, stream), info in self._stream_states.items():
                if info.session and info.slot_time_chicago:
                    # Extract time portion from slot_time_chicago (format: "HH:MM" or ISO string)
                    slot_time_str = info.slot_time_chicago
                    # Handle ISO format: "2026-01-24T07:30:00-06:00" -> "07:30"
                    if 'T' in slot_time_str:
                        try:
                            from datetime import datetime
                            dt = datetime.fromisoformat(slot_time_str.replace('Z', '+00:00'))
                            slot_time_str = dt.strftime("%H:%M")
                        except Exception:
                            # If parsing fails, try to extract HH:MM pattern
                            import re
                            match = re.search(r'(\d{2}):(\d{2})', slot_time_str)
                            if match:
                                slot_time_str = match.group(0)
                            else:
                                continue
                    
                    # Normalize to HH:MM format
                    slot_time_normalized = slot_time_str[:5] if len(slot_time_str) >= 5 else slot_time_str
                    
                    # Get allowed slot times for this session
                    allowed_times = SLOT_ENDS.get(info.session, [])
                    
                    # Check if slot_time matches any allowed time
                    if slot_time_normalized not in allowed_times:
                        session_slot_time_valid = False
                        logger.debug(
                            f"Slot time validation failed for stream {stream} (session {info.session}): "
                            f"slot_time={slot_time_normalized}, allowed={allowed_times}"
                        )
                        break  # One failure is enough to invalidate
        
        return {
            "recovery_state_allowed": recovery_state_allowed,
            "kill_switch_allowed": kill_switch_allowed,
            "timetable_validated": self._timetable_validated,
            "stream_armed": stream_armed,
            "session_slot_time_valid": session_slot_time_valid,
            "trading_date_set": self._trading_date is not None and self._trading_date != ""
        }
    
    def compute_watchdog_status(self) -> Dict:
        """Compute watchdog status derived field."""
        engine_alive = self.compute_engine_alive()
        stuck_streams = self.compute_stuck_streams()
        
        now = datetime.now(timezone.utc)
        
        # Compute market_open with error handling to ensure it's always returned
        try:
            chicago_now = datetime.now(CHICAGO_TZ)
            market_open = is_market_open(chicago_now)
        except Exception as e:
            logger.warning(f"Error computing market_open: {e}, defaulting to False", exc_info=True)
            market_open = False
        
        # PATTERN 1: Compute engine_activity_state using bars_expected contract
        # Determine which instruments have bars_expected
        instruments_with_bars_expected = []
        all_instruments = set(self._last_bar_utc_by_instrument.keys()) | set(
            info.instrument for info in self._stream_states.values() if info.instrument
        )
        for instrument in all_instruments:
            if self.bars_expected(instrument, market_open):
                instruments_with_bars_expected.append(instrument)
        
        # Compute engine_activity_state based on bars_expected and last bar times
        # Map to frontend-expected states: 'ACTIVE' | 'IDLE_MARKET_CLOSED' | 'STALLED'
        if not market_open:
            # Market closed - no bars expected, engine is idle
            engine_activity_state = "IDLE_MARKET_CLOSED"
        elif not instruments_with_bars_expected:
            # Market open but no streams require bars (waiting for range windows)
            # This is IDLE, not STALLED - streams will activate when their windows arrive
            engine_activity_state = "IDLE_MARKET_CLOSED"  # Use same state as market closed for UI consistency
        else:
            # Market open and at least one instrument requires bars
            # Check if any instrument with bars_expected is missing bars beyond threshold
            worst_bar_age = None
            stalled_instruments = []
            
            # PATTERN 1: Grace period for initial bar arrival
            # Only declare stalled if:
            # 1. No bars received for an instrument that expects bars, AND
            # 2. Enough time has passed since engine start (grace period)
            # OR bars were received but stopped (actual stall)
            engine_start_time = None
            if self._last_engine_tick_utc:
                # Use last engine tick as proxy for engine start time
                # If we have engine ticks, engine has been running
                engine_start_time = self._last_engine_tick_utc
            
            # Grace period: 5 minutes after engine start before declaring stall
            GRACE_PERIOD_SECONDS = 300  # 5 minutes
            engine_running_time = (now - engine_start_time).total_seconds() if engine_start_time else 0
            grace_period_active = engine_running_time < GRACE_PERIOD_SECONDS
            
            for instrument in instruments_with_bars_expected:
                last_bar_utc = self._last_bar_utc_by_instrument.get(instrument)
                if last_bar_utc:
                    # Bars were received - check if they stopped
                    bar_age = (now - last_bar_utc).total_seconds()
                    if worst_bar_age is None or bar_age > worst_bar_age:
                        worst_bar_age = bar_age
                    if bar_age > ENGINE_TICK_STALL_THRESHOLD_SECONDS:
                        stalled_instruments.append(instrument)
                else:
                    # No bars received yet for this instrument
                    # Only declare stalled if grace period has elapsed
                    if not grace_period_active:
                        worst_bar_age = float('inf')
                        stalled_instruments.append(instrument)
                    # During grace period, don't mark as stalled - engine may be starting up
            
            if stalled_instruments:
                engine_activity_state = "STALLED"
            else:
                engine_activity_state = "ACTIVE"
        
        data_stall_detected = {}
        for instrument, last_bar_utc in self._last_bar_utc_by_instrument.items():
            elapsed = (now - last_bar_utc).total_seconds()
            stall_detected = (
                elapsed > DATA_STALL_THRESHOLD_SECONDS
                and market_open
            )
            data_stall_detected[instrument] = {
                "instrument": instrument,
                "last_bar_chicago": last_bar_utc.astimezone(CHICAGO_TZ).isoformat(),
                "stall_detected": stall_detected,
                "market_open": market_open
            }
        
        # PATTERN 1: Additional observability (recommended)
        # Count instruments where bars_expected == True and worst last_bar_age
        bars_expected_count = len(instruments_with_bars_expected)
        worst_last_bar_age_seconds = None
        if instruments_with_bars_expected:
            for instrument in instruments_with_bars_expected:
                last_bar_utc = self._last_bar_utc_by_instrument.get(instrument)
                if last_bar_utc:
                    bar_age = (now - last_bar_utc).total_seconds()
                    if worst_last_bar_age_seconds is None or bar_age > worst_last_bar_age_seconds:
                        worst_last_bar_age_seconds = bar_age
        
        # PATTERN 1: Map internal states to frontend-expected states
        # Frontend expects: 'ACTIVE' | 'IDLE_MARKET_CLOSED' | 'STALLED'
        frontend_engine_state = engine_activity_state
        if engine_activity_state == "ENGINE_MARKET_CLOSED":
            frontend_engine_state = "IDLE_MARKET_CLOSED"
        elif engine_activity_state == "ENGINE_IDLE_WAITING_FOR_DATA":
            frontend_engine_state = "IDLE_MARKET_CLOSED"  # Use same state for UI consistency
        elif engine_activity_state == "ENGINE_STALLED":
            frontend_engine_state = "STALLED"
        elif engine_activity_state == "ENGINE_ACTIVE_PROCESSING":
            frontend_engine_state = "ACTIVE"
        
        return {
            "engine_alive": engine_alive,
            "engine_activity_state": frontend_engine_state,  # Mapped to frontend-expected values
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
            "data_stall_detected": data_stall_detected,
            "market_open": market_open,  # Always included, even if error occurred
            # PATTERN 1: Bars expected observability
            "bars_expected_count": bars_expected_count,
            "worst_last_bar_age_seconds": worst_last_bar_age_seconds,
            # PHASE 3.1: Identity invariants status
            "last_identity_invariants_pass": self._last_identity_invariants_pass,
            "last_identity_invariants_event_chicago": (
                self._last_identity_invariants_event_chicago.isoformat()
                if self._last_identity_invariants_event_chicago else None
            ),
            "last_identity_violations": self._last_identity_violations.copy()
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
        # Range and slot time fields (populated from events)
        self.session: Optional[str] = None
        self.slot_time_chicago: Optional[str] = None
        self.slot_time_utc: Optional[str] = None
        self.range_high: Optional[float] = None
        self.range_low: Optional[float] = None
        self.freeze_close: Optional[float] = None
        self.range_invalidated: bool = False


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
