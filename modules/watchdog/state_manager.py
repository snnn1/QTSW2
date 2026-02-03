"""
State Manager for Watchdog Aggregator

Maintains in-memory state for derived fields and cursor management.
"""
import json
import logging
import time
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
    RECOVERY_TIMEOUT_SECONDS,
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
        self._recovery_started_utc: Optional[datetime] = None  # Track when recovery started for timeout
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
        
        # Data stall tracking: execution instrument full name -> last_bar_utc
        # CRITICAL: Track by execution instrument contract (e.g., "MES 03-26"), not canonical (e.g., "ES")
        # This ensures: MES stalled ≠ ES flowing, M2K stalled ≠ RTY flowing
        self._last_bar_utc_by_execution_instrument: Dict[str, datetime] = {}
        # Keep old dict for backward compatibility during transition (will be removed later)
        self._last_bar_utc_by_instrument: Dict[str, datetime] = {}
        
        # Timetable state
        self._timetable_validated: bool = False
        self._trading_date: Optional[str] = None
        
        # Timetable-derived state (from timetable_current.json polling)
        self._enabled_streams: Optional[Set[str]] = None  # None = timetable unavailable
        self._timetable_streams: Dict[str, Dict] = {}  # stream_id -> {instrument, session, slot_time, enabled}
        self._timetable_hash: Optional[str] = None
        self._timetable_last_ok_utc: Optional[datetime] = None
        
        # Risk gate state
        self._allowed_slot_times: List[str] = []  # From timetable config
        
        # PHASE 3.1: Identity invariants status
        self._last_identity_invariants_pass: Optional[bool] = None
        self._last_identity_invariants_event_chicago: Optional[datetime] = None
        self._last_identity_violations: List[str] = []
        
        # Duplicate instance detection tracking
        # Key: (account, execution_instrument) -> DuplicateInstanceInfo
        self._duplicate_instances: Dict[tuple, 'DuplicateInstanceInfo'] = {}
        
        # Execution policy validation failures (keep last 10 failures)
        self._execution_policy_failures: List['ExecutionPolicyFailureInfo'] = []
        self._max_execution_policy_failures = 10  # Keep last 10 failures
        
        # Status smoothing/debouncing to prevent flickering
        # Track last N status computations to smooth out temporary threshold violations
        self._engine_status_history: List[tuple] = []  # List of (timestamp, status) tuples
        self._data_status_history: List[tuple] = []   # List of (timestamp, status) tuples
        self._status_history_size = 3  # Keep last 3 status computations (15 seconds at 5s polling)
        self._status_change_threshold = 2  # Require 2 consecutive polls with same status before changing
        
    def update_engine_tick(self, timestamp_utc: datetime):
        """
        Update engine tick timestamp from ENGINE_TICK_CALLSITE event.
        This represents engine loop (Tick()) execution - primary liveness indicator.
        ENGINE_TICK_CALLSITE fires every Tick() call (rate-limited in feed to every 5 seconds).
        """
        prev_tick = self._last_engine_tick_utc
        self._last_engine_tick_utc = timestamp_utc
        
        # Diagnostic: Log when ticks are received (rate-limited to avoid spam)
        if not hasattr(self, '_last_tick_update_log_utc'):
            self._last_tick_update_log_utc = None
        now = datetime.now(timezone.utc)
        if self._last_tick_update_log_utc is None or (now - self._last_tick_update_log_utc).total_seconds() >= 30:
            self._last_tick_update_log_utc = now
            elapsed_since_prev = (timestamp_utc - prev_tick).total_seconds() if prev_tick else None
            elapsed_str = f"{elapsed_since_prev:.1f}s" if elapsed_since_prev is not None else "first_tick"
            logger.debug(
                f"ENGINE_TICK_UPDATED: timestamp_utc={timestamp_utc.isoformat()}, "
                f"elapsed_since_prev={elapsed_str}"
            )
    
    def update_recovery_state(self, state: str, timestamp_utc: datetime):
        """Update recovery state."""
        # Map event types to recovery state enum values
        state_mapping = {
            "DISCONNECT_FAIL_CLOSED_ENTERED": "DISCONNECT_FAIL_CLOSED",
            "DISCONNECT_RECOVERY_STARTED": "RECOVERY_RUNNING",
            "DISCONNECT_RECOVERY_COMPLETE": "RECOVERY_COMPLETE",
            "DISCONNECT_RECOVERY_ABORTED": "DISCONNECT_FAIL_CLOSED",
        }
        new_state = state_mapping.get(state, state)
        
        # Track when recovery started for timeout detection
        if new_state == "RECOVERY_RUNNING":
            self._recovery_started_utc = timestamp_utc
        elif new_state in ("RECOVERY_COMPLETE", "CONNECTED_OK", "DISCONNECT_FAIL_CLOSED"):
            # Recovery completed or reset - clear start time
            self._recovery_started_utc = None
        
        self._recovery_state = new_state
    
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
        # Note: Do NOT update _last_connection_event_utc here - identity invariants are not connection events
    
    def update_stream_state(self, trading_date: str, stream: str, state: str, 
                          committed: bool = False, commit_reason: Optional[str] = None,
                          state_entry_time_utc: Optional[datetime] = None,
                          execution_instrument: Optional[str] = None):
        """
        Update stream state.
        
        Args:
            trading_date: Trading date string (YYYY-MM-DD)
            stream: Stream ID
            state: Stream state
            committed: Whether stream is committed
            commit_reason: Reason for commit (if committed)
            state_entry_time_utc: UTC timestamp of state entry
            execution_instrument: Execution instrument full name (e.g., "MES 03-26") - optional for backward compatibility
        """
        key = (trading_date, stream)
        if key not in self._stream_states:
            self._stream_states[key] = StreamStateInfo(
                trading_date=trading_date,
                stream=stream,
                state=state,
                committed=committed,
                commit_reason=commit_reason,
                state_entry_time_utc=state_entry_time_utc or datetime.now(timezone.utc),
                execution_instrument=execution_instrument
            )
        else:
            info = self._stream_states[key]
            previous_state = info.state
            if info.state != state:
                info.state = state
                info.state_entry_time_utc = state_entry_time_utc or datetime.now(timezone.utc)
                
                # CRITICAL: Clear ranges when transitioning away from RANGE_LOCKED
                # This prevents old ranges from persisting across state transitions
                if previous_state == "RANGE_LOCKED" and state != "RANGE_LOCKED":
                    logger.debug(
                        f"Clearing ranges for stream {stream} ({trading_date}): "
                        f"transitioning from RANGE_LOCKED to {state}"
                    )
                    info.range_high = None
                    info.range_low = None
                    info.freeze_close = None
                    info.range_invalidated = False
                    
            info.committed = committed
            if commit_reason:
                info.commit_reason = commit_reason
            # Update execution_instrument if provided (backward compatible - only set if not None)
            if execution_instrument is not None:
                info.execution_instrument = execution_instrument
    
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
    
    def record_duplicate_instance(
        self,
        account: str,
        execution_instrument: str,
        instance_id: str,
        timestamp_utc: datetime,
        error_message: str
    ):
        """
        Record duplicate instance detection.
        
        Args:
            account: Account name where duplicate was detected
            execution_instrument: Execution instrument full name (e.g., "MES 03-26")
            instance_id: Instance ID of the duplicate instance
            timestamp_utc: UTC timestamp when duplicate was detected
            error_message: Error message describing the duplicate
        """
        key = (account, execution_instrument)
        self._duplicate_instances[key] = DuplicateInstanceInfo(
            account=account,
            execution_instrument=execution_instrument,
            instance_id=instance_id,
            detected_at_utc=timestamp_utc,
            error_message=error_message
        )
        logger.warning(
            f"Duplicate instance recorded: account={account}, execution_instrument={execution_instrument}, "
            f"instance_id={instance_id}, detected_at={timestamp_utc.isoformat()}"
        )
    
    def record_execution_policy_failure(
        self,
        errors: List[str],
        execution_instruments: List[str],
        timestamp_utc: datetime,
        note: str
    ):
        """
        Record execution policy validation failure.
        
        Args:
            errors: List of error messages from validation
            execution_instruments: List of execution instruments involved in the failure
            timestamp_utc: UTC timestamp when failure occurred
            note: Additional note about the failure
        """
        failure_info = ExecutionPolicyFailureInfo(
            errors=errors.copy(),
            execution_instruments=execution_instruments.copy(),
            failed_at_utc=timestamp_utc,
            note=note
        )
        
        # Add to list and keep only last N failures
        self._execution_policy_failures.append(failure_info)
        if len(self._execution_policy_failures) > self._max_execution_policy_failures:
            self._execution_policy_failures.pop(0)  # Remove oldest
        
        logger.warning(
            f"Execution policy validation failure recorded: {len(errors)} error(s), "
            f"execution_instruments={execution_instruments}, failed_at={timestamp_utc.isoformat()}"
        )
    
    def update_last_bar(self, execution_instrument_full_name: str, timestamp_utc: datetime):
        """
        Update last bar timestamp for execution instrument contract.
        
        Args:
            execution_instrument_full_name: Full contract name (e.g., "MES 03-26", "M2K 03-26")
            timestamp_utc: Bar timestamp in UTC
        """
        # CRITICAL FIX: Clean up old root-name entries that don't match current format
        # Old entries like "M2K" should be removed when we receive "M2K 03-26"
        root_name = execution_instrument_full_name.split()[0] if execution_instrument_full_name else execution_instrument_full_name
        if root_name and root_name != execution_instrument_full_name:
            # If we're updating a full contract name, remove any old root-name entry
            if root_name in self._last_bar_utc_by_execution_instrument:
                old_entry = self._last_bar_utc_by_execution_instrument[root_name]
                # Only remove if it's significantly older (more than 10 minutes)
                # This prevents removing valid entries during transition
                age_seconds = (timestamp_utc - old_entry).total_seconds() if old_entry else 0
                if abs(age_seconds) > 600:  # 10 minutes difference
                    logger.debug(
                        f"Cleaning up old root-name entry: {root_name} (replaced by {execution_instrument_full_name})"
                    )
                    del self._last_bar_utc_by_execution_instrument[root_name]
        
        # Track by execution instrument full name (authoritative)
        prev_timestamp = self._last_bar_utc_by_execution_instrument.get(execution_instrument_full_name)
        self._last_bar_utc_by_execution_instrument[execution_instrument_full_name] = timestamp_utc
        
        # Log update for debugging (rate-limited to avoid spam)
        if not hasattr(self, '_last_bar_update_log'):
            self._last_bar_update_log = {}
        
        now = datetime.now(timezone.utc)
        log_key = execution_instrument_full_name
        last_log_time = self._last_bar_update_log.get(log_key)
        
        if not last_log_time or (now - last_log_time).total_seconds() >= 60:
            logger.debug(
                f"update_last_bar: {execution_instrument_full_name} -> {timestamp_utc.isoformat()}"
                f"{f' (prev: {prev_timestamp.isoformat()})' if prev_timestamp else ' (new)'}"
            )
            self._last_bar_update_log[log_key] = now
        
        # Also update old dict for backward compatibility (will be removed later)
        # Extract root name for old dict (e.g., "MES 03-26" -> "MES")
        self._last_bar_utc_by_instrument[root_name] = timestamp_utc
    
    def mark_data_loss(self, execution_instrument_full_name: str, timestamp_utc: datetime):
        """
        Mark execution instrument contract as having data loss.
        
        Args:
            execution_instrument_full_name: Full contract name (e.g., "MES 03-26")
            timestamp_utc: Timestamp of data loss event
        """
        # Clear last_bar_utc to indicate data loss
        # This will cause stall_detected to trigger if market is open
        if execution_instrument_full_name in self._last_bar_utc_by_execution_instrument:
            del self._last_bar_utc_by_execution_instrument[execution_instrument_full_name]
        # Also clear from old dict for backward compatibility
        root_name = execution_instrument_full_name.split()[0] if execution_instrument_full_name else execution_instrument_full_name
        if root_name in self._last_bar_utc_by_instrument:
            del self._last_bar_utc_by_instrument[root_name]
    
    def update_timetable_state(self, validated: bool, trading_date: Optional[str] = None):
        """Update timetable validation state."""
        self._timetable_validated = validated
        if trading_date:
            self._trading_date = trading_date
    
    def update_timetable_streams(
        self,
        enabled_streams: Optional[Set[str]],
        trading_date: str,
        timetable_hash: Optional[str],
        utc_now: datetime,
        enabled_streams_metadata: Optional[Dict[str, Dict]] = None
    ):
        """
        Update timetable-derived state.
        
        - Sets trading_date always (authoritative)
        - Sets enabled_streams only when non-None (preserves None on failure)
        - Stores full stream metadata (instrument, session, slot_time) when available
        - Updates timetable_hash when non-None
        - Records last_ok timestamp when successful
        - Sets timetable_validated based on whether timetable was successfully loaded
        """
        self._trading_date = trading_date
        if enabled_streams is not None:
            self._enabled_streams = enabled_streams
        if enabled_streams_metadata is not None:
            self._timetable_streams = enabled_streams_metadata
        if timetable_hash is not None:
            self._timetable_hash = timetable_hash
            self._timetable_last_ok_utc = utc_now
        
        # Timetable is validated if we successfully loaded it (enabled_streams is not None)
        # This means the file exists, is parseable, and has valid structure
        self._timetable_validated = enabled_streams is not None
    
    def get_enabled_streams(self) -> Optional[Set[str]]:
        """Get enabled streams set (None if timetable unavailable)."""
        return self._enabled_streams
    
    def get_timetable_streams_metadata(self) -> Optional[Dict[str, Dict]]:
        """
        Get full timetable stream metadata (None if timetable unavailable).
        
        Returns None only if timetable is unavailable (enabled_streams is None).
        Returns empty dict if timetable is available but no streams are enabled.
        """
        if self._enabled_streams is None:
            return None  # Timetable unavailable
        return self._timetable_streams
    
    def get_timetable_hash(self) -> Optional[str]:
        """Get current timetable hash."""
        return self._timetable_hash
    
    def get_trading_date(self) -> Optional[str]:
        """Get current trading_date (from timetable, or CME rollover fallback)."""
        return self._trading_date
    
    def cleanup_stale_streams(self, current_trading_date: str, utc_now: datetime, clear_all_for_date: bool = False):
        """
        Clean up stale streams from previous runs or old trading dates.
        
        Args:
            current_trading_date: Current trading date to keep
            utc_now: Current UTC time for age calculations
            clear_all_for_date: If True, clear streams for current_trading_date that haven't been updated recently
        """
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
            # If clear_all_for_date is True (ENGINE_START), only remove streams that haven't been updated recently
            # This prevents clearing active streams that are still transitioning
            elif clear_all_for_date and trading_date == current_trading_date:
                # Only clear if stream hasn't been updated in the last 30 seconds
                # This allows streams to be re-initialized after restart while preserving active ones
                time_since_update = (utc_now - info.state_entry_time_utc).total_seconds()
                if time_since_update > 30:  # 30 seconds
                    logger.info(
                        f"Removing stale stream on ENGINE_START (not updated recently): {stream} "
                        f"(state: {info.state}, last_update: {time_since_update:.1f}s ago, trading_date: {trading_date})"
                    )
                    keys_to_remove.append((trading_date, stream))
                else:
                    logger.debug(
                        f"Keeping active stream on ENGINE_START: {stream} "
                        f"(state: {info.state}, updated {time_since_update:.1f}s ago)"
                    )
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
            logger.info(
                f"cleanup_stale_streams: Removed {len(keys_to_remove)} stale stream(s) "
                f"(current_trading_date: {current_trading_date}, clear_all_for_date: {clear_all_for_date})"
            )
            logger.info(f"Cleaned up {len(keys_to_remove)} stale stream(s) (current_trading_date: {current_trading_date}, clear_all: {clear_all_for_date})")
    
    def bars_expected(self, execution_instrument_full_name: str, market_open: bool) -> bool:
        """
        PATTERN 1: Determine if bars are expected for an execution instrument contract.
        
        NEW LOGIC: "Is there an enabled stream whose execution instrument matches X and whose state expects bars?"
        
        Bars are expected if:
        - market_open == True, AND
        - at least one stream for that execution instrument is in a bar-dependent state.
        
        Bar-dependent states: PRE_HYDRATION, ARMED, RANGE_BUILDING, RANGE_LOCKED
        Excluded states: DONE, COMMITTED, NO_TRADE, INVALIDATED
        
        Args:
            execution_instrument_full_name: Full contract name (e.g., "MES 03-26", "M2K 03-26")
            market_open: Whether market is currently open
        """
        if not market_open:
            return False
        
        # Check if any stream for this execution instrument is in a bar-dependent state
        # CRITICAL: Also check if the stream is enabled on the timetable
        bar_dependent_states = {"PRE_HYDRATION", "ARMED", "RANGE_BUILDING", "RANGE_LOCKED"}
        excluded_states = {"DONE", "COMMITTED", "NO_TRADE", "INVALIDATED"}
        
        for (trading_date, stream), info in self._stream_states.items():
            # Check if stream is enabled on timetable (if timetable available)
            stream_id = f"{trading_date}_{stream}"
            if self._enabled_streams is not None:
                # Timetable available - only check enabled streams
                if stream_id not in self._enabled_streams:
                    continue  # Stream not enabled, skip it
            # If timetable unavailable (_enabled_streams is None), check all streams (conservative)
            
            # Check if this stream's execution instrument matches the given execution instrument
            # Match by full name if available, otherwise match by root name
            stream_execution_instrument = getattr(info, 'execution_instrument', None)
            if stream_execution_instrument:
                # Match by full name (exact match)
                if stream_execution_instrument == execution_instrument_full_name:
                    if info.state in bar_dependent_states and not info.committed:
                        return True
                # Also match by root name (e.g., "MES 03-26" matches stream with "MES")
                elif execution_instrument_full_name.startswith(stream_execution_instrument.split()[0] if ' ' in stream_execution_instrument else stream_execution_instrument):
                    if info.state in bar_dependent_states and not info.committed:
                        return True
        
        return False
    
    def compute_engine_alive(self) -> bool:
        """Compute engine_alive derived field."""
        if self._last_engine_tick_utc is None:
            return False
        
        now = datetime.now(timezone.utc)
        elapsed = (now - self._last_engine_tick_utc).total_seconds()
        engine_alive = elapsed < ENGINE_TICK_STALL_THRESHOLD_SECONDS
        
        # Phase 4: Diagnostic logging (rate-limited to every ~30 seconds)
        if not hasattr(self, '_last_engine_alive_log_utc'):
            self._last_engine_alive_log_utc = None
        
        if self._last_engine_alive_log_utc is None or (now - self._last_engine_alive_log_utc).total_seconds() >= 30:
            self._last_engine_alive_log_utc = now
            import logging
            logger = logging.getLogger(__name__)
            logger.info(
                f"ENGINE_ALIVE_STATUS: last_engine_tick_utc={self._last_engine_tick_utc.isoformat() if self._last_engine_tick_utc else None}, "
                f"elapsed_seconds={elapsed:.2f}, engine_alive={engine_alive}, threshold={ENGINE_TICK_STALL_THRESHOLD_SECONDS}"
            )
        
        return engine_alive
    
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
        # Determine which execution instruments have bars_expected
        instruments_with_bars_expected = []
        # Use execution instrument full names from bar tracking
        all_execution_instruments = set(self._last_bar_utc_by_execution_instrument.keys())
        # Also include execution instruments from stream states
        for info in self._stream_states.values():
            execution_instrument = getattr(info, 'execution_instrument', None)
            if execution_instrument:
                all_execution_instruments.add(execution_instrument)
        
        # CRITICAL FIX: Filter out root-name entries (e.g., "M2K") that don't have contract suffix
        # These are old entries from before the contract name format change
        # Only keep entries that look like full contract names (contain space and date)
        filtered_instruments = set()
        for inst in all_execution_instruments:
            # Full contract names have format like "MES 03-26" (space + date)
            # Root names are just "MES" (no space)
            if ' ' in inst and len(inst.split()) >= 2:
                # Looks like full contract name - keep it
                filtered_instruments.add(inst)
            elif inst not in self._last_bar_utc_by_execution_instrument:
                # Root name but not in tracking dict - skip it
                continue
            else:
                # Root name in tracking dict - check if it's old (more than 10 minutes)
                last_bar = self._last_bar_utc_by_execution_instrument.get(inst)
                if last_bar:
                    age = (now - last_bar).total_seconds()
                    if age > 600:  # More than 10 minutes old - likely stale
                        logger.debug(f"Filtering out old root-name entry: {inst} (age: {age:.1f}s)")
                        continue
                # Recent root-name entry - keep it (might be valid during transition)
                filtered_instruments.add(inst)
        
        # Check bars_expected for each execution instrument (using filtered set)
        for execution_instrument_full_name in filtered_instruments:
            if self.bars_expected(execution_instrument_full_name, market_open):
                instruments_with_bars_expected.append(execution_instrument_full_name)
        
        # PATTERN 1: Grace period for initial bar arrival
        # Only declare stalled if enough time has passed since engine start
        engine_start_time = None
        if self._last_engine_tick_utc:
            # Use last engine tick as proxy for engine start time
            # If we have engine ticks, engine has been running
            engine_start_time = self._last_engine_tick_utc
        
        # Grace period: 5 minutes after engine start before declaring stall
        GRACE_PERIOD_SECONDS = 300  # 5 minutes
        engine_running_time = (now - engine_start_time).total_seconds() if engine_start_time else 0
        grace_period_active = engine_running_time < GRACE_PERIOD_SECONDS
        
        # Compute engine_activity_state based on bars_expected and last bar times
        # Map to frontend-expected states: 'ACTIVE' | 'IDLE_MARKET_CLOSED' | 'STALLED'
        # PRIMARY INDICATOR: Use engine tick liveness (ENGINE_TICK_CALLSITE) as the main signal
        # Ticks fire very frequently (every Tick() call) and are rate-limited in feed to every 5 seconds
        # This is more reliable than bar events which are rate-limited to 60 seconds
        
        engine_tick_age = None
        if self._last_engine_tick_utc:
            engine_tick_age = (now - self._last_engine_tick_utc).total_seconds()
        
        # PRIORITY 1: Check if ticks are arriving (most reliable indicator)
        if engine_tick_age is not None and engine_tick_age <= ENGINE_TICK_STALL_THRESHOLD_SECONDS:
            # Ticks are arriving - engine is ACTIVE regardless of market status or bar expectations
            # This ensures that if you see ticks in the UI, the status shows ACTIVE
            engine_activity_state = "ACTIVE"
            logger.debug(
                f"ENGINE_ACTIVE: Ticks arriving (tick_age={engine_tick_age:.1f}s < {ENGINE_TICK_STALL_THRESHOLD_SECONDS}s)"
            )
        elif engine_tick_age is not None and engine_tick_age > ENGINE_TICK_STALL_THRESHOLD_SECONDS:
            # Engine ticks stopped - this is a real stall
            engine_activity_state = "STALLED"
            logger.warning(
                f"ENGINE_STALL_DETECTED_TICKS_STOPPED: tick_age_seconds={engine_tick_age:.1f}, "
                f"threshold={ENGINE_TICK_STALL_THRESHOLD_SECONDS}, "
                f"last_tick_utc={self._last_engine_tick_utc.isoformat() if self._last_engine_tick_utc else None}, "
                f"now={now.isoformat()}"
            )
        elif not market_open:
            # No ticks received and market closed - engine is idle
            engine_activity_state = "IDLE_MARKET_CLOSED"
        elif not instruments_with_bars_expected:
            # No ticks received, market open but no streams require bars (waiting for range windows)
            # This is IDLE, not STALLED - streams will activate when their windows arrive
            engine_activity_state = "IDLE_MARKET_CLOSED"  # Use same state as market closed for UI consistency
        elif engine_tick_age is None:
            # No ticks received yet - only stall if grace period elapsed
            if not grace_period_active:
                engine_activity_state = "STALLED"
                logger.warning(
                    f"ENGINE_STALL_DETECTED_NO_TICKS: grace_period_elapsed=True, "
                    f"engine_running_time={engine_running_time:.1f}s, "
                    f"_last_engine_tick_utc={self._last_engine_tick_utc}"
                )
            else:
                engine_activity_state = "ACTIVE"  # Grace period active, engine starting up
                logger.debug(
                    f"ENGINE_ACTIVE_GRACE_PERIOD: No ticks yet but grace period active "
                    f"(running_time={engine_running_time:.1f}s < {GRACE_PERIOD_SECONDS}s)"
                )
        
        data_stall_detected = {}
        # Only check for stalls on execution instruments that are expected to receive bars
        # This prevents false positives from instruments that received bars earlier
        # but are no longer expected to receive them (e.g., stream completed, market closed)
        # CRITICAL: Stall detection is per execution instrument contract (MES stalled ≠ ES flowing)
        # Check instruments that are expecting bars
        for execution_instrument_full_name in instruments_with_bars_expected:
            last_bar_utc = self._last_bar_utc_by_execution_instrument.get(execution_instrument_full_name)
            if last_bar_utc:
                elapsed = (now - last_bar_utc).total_seconds()
                stall_detected = (
                    elapsed > DATA_STALL_THRESHOLD_SECONDS
                    and market_open
                )
                data_stall_detected[execution_instrument_full_name] = {
                    "instrument": execution_instrument_full_name,  # Execution instrument full name
                    "last_bar_chicago": last_bar_utc.astimezone(CHICAGO_TZ).isoformat(),
                    "stall_detected": stall_detected,
                    "market_open": market_open
                }
            else:
                # Execution instrument expects bars but hasn't received any yet
                # Only mark as stalled if grace period has elapsed
                if not grace_period_active:
                    data_stall_detected[execution_instrument_full_name] = {
                        "instrument": execution_instrument_full_name,
                        "last_bar_chicago": None,
                        "stall_detected": market_open,  # Stall if market is open and no bars received
                        "market_open": market_open
                    }
        
        # Compute worst_last_bar_age_seconds for ALL execution instruments that have received bars
        # This helps detect data flow even when streams aren't in bar-dependent states yet
        # Do this BEFORE checking for stalls so we can use it in the stall detection logic
        worst_last_bar_age_seconds = None
        if self._last_bar_utc_by_execution_instrument:
            # Check all execution instruments that have received bars (not just those with bars_expected)
            for execution_instrument_full_name, last_bar_utc in self._last_bar_utc_by_execution_instrument.items():
                if last_bar_utc:
                    bar_age = (now - last_bar_utc).total_seconds()
                    if worst_last_bar_age_seconds is None or bar_age > worst_last_bar_age_seconds:
                        worst_last_bar_age_seconds = bar_age
        
        # Also check instruments that have received bars but aren't currently expecting them
        # This catches stalls when streams aren't in bar-dependent states yet
        # CRITICAL: Only check instruments that are currently enabled/active
        # Don't flag instruments that:
        # 1. Received bars hours/days ago but are no longer active
        # 2. Are not enabled on the timetable (e.g., NG not enabled)
        # Use a "recent activity" threshold (e.g., 1 hour) AND check if instrument has enabled streams
        RECENT_ACTIVITY_THRESHOLD_SECONDS = 3600  # 1 hour - if no bars in last hour, instrument is inactive
        if market_open and worst_last_bar_age_seconds is not None:
            if worst_last_bar_age_seconds > DATA_STALL_THRESHOLD_SECONDS:
                # Find instruments with old bars that aren't already in data_stall_detected
                # BUT only if:
                # 1. They've received bars recently (within RECENT_ACTIVITY_THRESHOLD)
                # 2. They have enabled streams on the timetable (or timetable unavailable)
                # CRITICAL FIX: Filter out old root-name entries (e.g., "M2K" without contract suffix)
                # These are stale entries from before the contract name format change
                for execution_instrument_full_name, last_bar_utc in self._last_bar_utc_by_execution_instrument.items():
                    # Skip root-name entries that don't have contract suffix (old format)
                    # Full contract names have format like "MES 03-26" (space + date)
                    if ' ' not in execution_instrument_full_name or len(execution_instrument_full_name.split()) < 2:
                        # Root name without contract - check if it's old and should be ignored
                        bar_age = (now - last_bar_utc).total_seconds() if last_bar_utc else 0
                        if bar_age > 600:  # More than 10 minutes old - likely stale, skip it
                            logger.debug(
                                f"Skipping old root-name entry in stall detection: {execution_instrument_full_name} "
                                f"(age: {bar_age:.1f}s)"
                            )
                            continue
                        # Recent root-name entry - might be valid during transition, but prefer full contract names
                        # Check if there's a corresponding full contract name entry
                        root_name = execution_instrument_full_name
                        has_full_contract_entry = any(
                            inst.startswith(root_name + ' ') 
                            for inst in self._last_bar_utc_by_execution_instrument.keys()
                        )
                        if has_full_contract_entry:
                            # Full contract name exists - skip root name entry
                            logger.debug(
                                f"Skipping root-name entry in favor of full contract name: {execution_instrument_full_name}"
                            )
                            continue
                    
                    if execution_instrument_full_name not in data_stall_detected and last_bar_utc:
                        bar_age = (now - last_bar_utc).total_seconds()
                        # Only flag as stalled if:
                        # 1. Bar age exceeds stall threshold (> 120s)
                        # 2. Instrument was active recently (< 1 hour ago)
                        # 3. Instrument has enabled streams (check if any stream for this instrument is enabled)
                        if bar_age > DATA_STALL_THRESHOLD_SECONDS and bar_age < RECENT_ACTIVITY_THRESHOLD_SECONDS:
                            # Check if this instrument has any enabled streams
                            has_enabled_streams = False
                            if self._enabled_streams is None:
                                # Timetable unavailable - assume enabled (conservative)
                                has_enabled_streams = True
                            else:
                                # Check if any stream for this execution instrument is enabled
                                for (trading_date, stream), info in self._stream_states.items():
                                    stream_execution_instrument = getattr(info, 'execution_instrument', None)
                                    if stream_execution_instrument and stream_execution_instrument == execution_instrument_full_name:
                                        stream_id = f"{trading_date}_{stream}"
                                        if stream_id in self._enabled_streams:
                                            has_enabled_streams = True
                                            break
                            
                            # Only flag as stalled if instrument has enabled streams
                            if has_enabled_streams:
                                data_stall_detected[execution_instrument_full_name] = {
                                    "instrument": execution_instrument_full_name,
                                    "last_bar_chicago": last_bar_utc.astimezone(CHICAGO_TZ).isoformat(),
                                    "stall_detected": True,
                                    "market_open": market_open
                                }
        
        # PATTERN 1: Additional observability (recommended)
        # Count execution instruments where bars_expected == True and worst last_bar_age
        bars_expected_count = len(instruments_with_bars_expected)
        
        # worst_last_bar_age_seconds already computed above
        
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
        
        # CRITICAL FIX: Auto-clear recovery state if stuck in RECOVERY_RUNNING for too long
        # This prevents watchdog from being stuck in "RECOVERY IN PROGRESS" indefinitely
        # if DISCONNECT_RECOVERY_COMPLETE event is never received
        recovery_state = self._recovery_state
        if recovery_state == "RECOVERY_RUNNING" and self._recovery_started_utc:
            recovery_duration = (now - self._recovery_started_utc).total_seconds()
            if recovery_duration > RECOVERY_TIMEOUT_SECONDS:
                logger.warning(
                    f"RECOVERY_STATE_TIMEOUT: Recovery has been running for {recovery_duration:.0f}s "
                    f"(started: {self._recovery_started_utc.isoformat()}), "
                    f"but DISCONNECT_RECOVERY_COMPLETE event was never received. "
                    f"Auto-clearing recovery state to CONNECTED_OK."
                )
                # Auto-transition to CONNECTED_OK if engine is alive and receiving ticks
                # Otherwise, assume recovery completed but event was missed
                if engine_alive:
                    recovery_state = "CONNECTED_OK"
                    self._recovery_state = "CONNECTED_OK"
                    self._recovery_started_utc = None
                else:
                    # Engine not alive - recovery may have failed, but clear the stuck state anyway
                    # This prevents indefinite "RECOVERY IN PROGRESS" when engine is actually stalled
                    recovery_state = "CONNECTED_OK"
                    self._recovery_state = "CONNECTED_OK"
                    self._recovery_started_utc = None
        
        # CRITICAL FIX: Auto-clear DISCONNECT_FAIL_CLOSED state if engine is alive and receiving ticks
        # This handles the case where disconnect happened but engine recovered without sending recovery events
        # If engine is receiving ticks consistently, connection has been restored
        if recovery_state == "DISCONNECT_FAIL_CLOSED" and engine_alive:
            # Engine is alive (receiving ticks within threshold) - connection is restored
            # Auto-clear fail-closed state since engine is operational
            logger.info(
                f"RECOVERY_STATE_AUTO_CLEAR: Engine is alive and receiving ticks "
                f"(tick_age={(now - self._last_engine_tick_utc).total_seconds():.1f}s < {ENGINE_TICK_STALL_THRESHOLD_SECONDS}s). "
                f"Auto-clearing DISCONNECT_FAIL_CLOSED state to CONNECTED_OK."
            )
            recovery_state = "CONNECTED_OK"
            self._recovery_state = "CONNECTED_OK"
            self._recovery_started_utc = None
        
        # CRITICAL FIX: Auto-clear ConnectionLost status if engine is alive and receiving ticks
        # This handles the case where connection was lost but recovered without sending CONNECTION_RECOVERED event
        # If engine is receiving ticks consistently, connection has been restored
        if self._connection_status == "ConnectionLost" and engine_alive:
            # Engine is alive (receiving ticks within threshold) - connection is restored
            # Check if connection event is old enough to warrant auto-clear (prevent immediate re-trigger)
            MIN_STABLE_CONNECTION_SECONDS = 60  # 1 minute - allow brief recovery period
            should_auto_clear = False
            
            if self._last_connection_event_utc:
                connection_event_age = (now - self._last_connection_event_utc).total_seconds()
                if connection_event_age > MIN_STABLE_CONNECTION_SECONDS:
                    should_auto_clear = True
            else:
                # No connection event timestamp - auto-clear if engine is alive
                should_auto_clear = True
            
            if should_auto_clear:
                logger.info(
                    f"CONNECTION_STATUS_AUTO_CLEAR: Engine is alive and receiving ticks "
                    f"(tick_age={(now - self._last_engine_tick_utc).total_seconds():.1f}s < {ENGINE_TICK_STALL_THRESHOLD_SECONDS}s). "
                    f"Auto-clearing ConnectionLost status to Connected."
                )
                self._connection_status = "Connected"
                # Update last connection event time to prevent immediate re-trigger
                self._last_connection_event_utc = now
        
        # Apply smoothing to data stall detection
        # Compute data status: 'FLOWING' | 'STALLED' | 'ACCEPTABLE_SILENCE'
        data_status = "FLOWING"
        if data_stall_detected:
            critical_stall = any(d.get('stall_detected', False) and d.get('market_open', False) 
                                for d in data_stall_detected.values())
            if critical_stall:
                data_status = "STALLED"
            elif any(d.get('stall_detected', False) for d in data_stall_detected.values()):
                data_status = "ACCEPTABLE_SILENCE"  # Market closed or acceptable pause
        
        # Apply smoothing/debouncing to data status
        self._data_status_history.append((now, data_status))
        if len(self._data_status_history) > self._status_history_size:
            self._data_status_history.pop(0)
        
        # Check if status has been consistent for threshold polls
        if len(self._data_status_history) >= self._status_change_threshold:
            recent_states = [state for _, state in self._data_status_history[-self._status_change_threshold:]]
            if len(set(recent_states)) == 1:
                # Status has been consistent - use it
                smoothed_data_status = recent_states[0]
            else:
                # Status is inconsistent - use most recent but keep previous if available
                if len(self._data_status_history) > self._status_change_threshold:
                    prev_stable = self._data_status_history[-self._status_change_threshold - 1][1]
                    smoothed_data_status = prev_stable
                else:
                    smoothed_data_status = data_status
        else:
            smoothed_data_status = data_status
        
        # Filter data_stall_detected based on smoothed status
        # Only suppress stalls if smoothed status indicates they're temporary flickering
        smoothed_data_stall_detected = data_stall_detected
        if smoothed_data_status == "FLOWING" and data_status == "STALLED":
            # Temporary stall detected but smoothed status is FLOWING - likely flickering
            # Only suppress if this is a single-poll violation (not persistent)
            if len(self._data_status_history) >= 2:
                # Check if previous status was also FLOWING (indicates temporary violation)
                prev_status = self._data_status_history[-2][1]
                if prev_status == "FLOWING":
                    # Previous was FLOWING, current is STALLED, smoothed is FLOWING = flickering
                    smoothed_data_stall_detected = {}
                # If previous was STALLED, this might be real - keep stalls
        
        return {
            "engine_alive": engine_alive,
            "engine_activity_state": frontend_engine_state,  # Mapped to frontend-expected values (smoothed)
            "last_engine_tick_chicago": (
                self._last_engine_tick_utc.astimezone(CHICAGO_TZ).isoformat()
                if self._last_engine_tick_utc else None
            ),
            "engine_tick_stall_detected": not engine_alive,
            "recovery_state": recovery_state,  # Use computed recovery_state (may be auto-cleared)
            "kill_switch_active": self._kill_switch_active,
            "connection_status": self._connection_status,
            "last_connection_event_chicago": (
                self._last_connection_event_utc.astimezone(CHICAGO_TZ).isoformat()
                if self._last_connection_event_utc else None
            ),
            "stuck_streams": stuck_streams,
            "execution_blocked_count": len(self._execution_blocked_events),
            "protective_failures_count": len(self._protective_failure_events),
            "data_stall_detected": smoothed_data_stall_detected,  # Smoothed to prevent flickering
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
            "last_identity_violations": self._last_identity_violations.copy(),
            # Duplicate instance detection
            "duplicate_instances_detected": [
                {
                    "account": info.account,
                    "execution_instrument": info.execution_instrument,
                    "instance_id": info.instance_id,
                    "detected_at_chicago": info.detected_at_utc.astimezone(CHICAGO_TZ).isoformat(),
                    "error_message": info.error_message
                }
                for info in self._duplicate_instances.values()
            ],
            "duplicate_instances_count": len(self._duplicate_instances),
            # Execution policy validation failures
            "execution_policy_failures": [
                {
                    "errors": failure.errors.copy(),
                    "execution_instruments": failure.execution_instruments.copy(),
                    "failed_at_chicago": failure.failed_at_utc.astimezone(CHICAGO_TZ).isoformat(),
                    "note": failure.note
                }
                for failure in self._execution_policy_failures
            ],
            "execution_policy_failures_count": len(self._execution_policy_failures)
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
                 state_entry_time_utc: Optional[datetime] = None,
                 execution_instrument: Optional[str] = None):
        self.trading_date = trading_date
        self.stream = stream
        self.state = state
        self.committed = committed
        self.commit_reason = commit_reason
        self.state_entry_time_utc = state_entry_time_utc or datetime.now(timezone.utc)
        self.instrument = ""  # Canonical instrument (DO NOT CHANGE - backward compatibility)
        self.execution_instrument: Optional[str] = execution_instrument  # Full contract name (e.g., "MES 03-26")
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


class DuplicateInstanceInfo:
    """Information about a detected duplicate instance."""
    def __init__(self, account: str, execution_instrument: str, instance_id: str,
                 detected_at_utc: datetime, error_message: str):
        self.account = account
        self.execution_instrument = execution_instrument
        self.instance_id = instance_id
        self.detected_at_utc = detected_at_utc
        self.error_message = error_message


class ExecutionPolicyFailureInfo:
    """Information about an execution policy validation failure."""
    def __init__(self, errors: List[str], execution_instruments: List[str],
                 failed_at_utc: datetime, note: str):
        self.errors = errors
        self.execution_instruments = execution_instruments
        self.failed_at_utc = failed_at_utc
        self.note = note


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
        """Save cursor state to file with retry logic."""
        max_retries = 3
        for attempt in range(max_retries):
            try:
                with open(self._cursor_file, 'w') as f:
                    json.dump(cursor, f, indent=2)
                return  # Success
            except Exception as e:
                if attempt < max_retries - 1:
                    wait_time = 0.1 * (2 ** attempt)  # Exponential backoff
                    logger.warning(f"Failed to save cursor (attempt {attempt + 1}/{max_retries}): {e}, retrying in {wait_time}s")
                    time.sleep(wait_time)
                else:
                    logger.error(f"Failed to save cursor after {max_retries} attempts: {e}")
