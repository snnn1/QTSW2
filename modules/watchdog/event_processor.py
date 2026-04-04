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

from .config import (
    DATA_EVENT_MAX_AGE_SECONDS,
    FRONTEND_FEED_FILE,
    SESSION_CONNECTION_EVENT_MAX_AGE_SECONDS,
)
from .incident_recorder import process_event as incident_recorder_process_event
from .state_manager import WatchdogStateManager
from .timetable_poller import compute_timetable_trading_date

logger = logging.getLogger(__name__)

CHICAGO_TZ = pytz.timezone("America/Chicago")


def _trading_date_from_timestamp(timestamp_utc: datetime) -> str:
    """Derive CME session date from event timestamp (canonical 18:00 CT via timetable poller)."""
    if timestamp_utc.tzinfo is None:
        timestamp_utc = timestamp_utc.replace(tzinfo=timezone.utc)
    chicago_dt = timestamp_utc.astimezone(CHICAGO_TZ)
    return compute_timetable_trading_date(chicago_dt)


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

    def _should_accept_bar_event(self, bar_ts: datetime) -> bool:
        """Return True if bar timestamp is recent enough (not stale from tail replay)."""
        if bar_ts.tzinfo is None:
            bar_ts = bar_ts.replace(tzinfo=timezone.utc)
        bar_age = (datetime.now(timezone.utc) - bar_ts).total_seconds()
        return bar_age <= DATA_EVENT_MAX_AGE_SECONDS
    
    def process_event(self, event: Dict):
        """Process a single event and update state manager."""
        if not isinstance(event, dict):
            return

        # Normalize robot / feed field aliases so gate, adoption, recovery, and incidents agree.
        raw_type = event.get("event_type") or event.get("event") or event.get("@event")
        event_type_norm = str(raw_type).strip() if raw_type is not None else ""
        if event_type_norm:
            event["event_type"] = event_type_norm

        raw_ts = event.get("timestamp_utc") or event.get("ts_utc") or event.get("timestamp")
        if raw_ts is not None and str(raw_ts).strip():
            event["timestamp_utc"] = str(raw_ts).strip()

        # Incident recorder: purely observational, never throws (sees normalized keys)
        try:
            incident_recorder_process_event(event)
        except Exception:
            pass

        event_type = event.get("event_type") or ""
        run_id = event.get("run_id", "")
        timestamp_utc_str = event.get("timestamp_utc") or ""
        data = event.get("data", {})
        if not isinstance(data, dict):
            data = {}
        
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
            # Engine liveness: ENGINE_TICK_CALLSITE, ENGINE_ALIVE, ENGINE_TIMER_HEARTBEAT drive _last_engine_heartbeat.
            # Do NOT update_engine_tick here - avoids false ENGINE ALIVE from stale ENGINE_START in tail.
            self._state_manager.clear_robot_heartbeat_timetable()
            # Phase 7-8: Clear pending orders on new run (avoids stale stuck detection)
            if hasattr(self._state_manager, "_pending_orders"):
                self._state_manager._pending_orders.clear()
            if hasattr(self._state_manager, "clear_broker_order_id_links"):
                self._state_manager.clear_broker_order_id_links()
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
            # DEPRECATED: Do not drive engine liveness - canonical sources are ENGINE_TICK_CALLSITE and ENGINE_ALIVE.
            pass
        
        elif event_type == "ENGINE_TICK_HEARTBEAT":
            # Bar-driven: do NOT update_engine_tick - canonical liveness is ENGINE_TICK_CALLSITE and ENGINE_ALIVE.
            # Keep bar tracking only (update_last_bar).
            # Extract execution_instrument_full_name and bar_time_utc from heartbeat payload
            execution_instrument_full_name = data.get("execution_instrument_full_name") or event.get("execution_instrument_full_name")
            instrument = data.get("instrument") or event.get("instrument")  # Fallback for backward compatibility
            bar_time_utc_str = data.get("bar_time_utc")
            
            if bar_time_utc_str:
                bar_time_utc = self._parse_timestamp(bar_time_utc_str)
                if bar_time_utc and self._should_accept_bar_event(bar_time_utc):
                    # Use execution_instrument_full_name if available, otherwise fall back to instrument
                    if execution_instrument_full_name:
                        self._state_manager.update_last_bar(execution_instrument_full_name, bar_time_utc)
                    elif instrument:
                        # Backward compatibility: fall back to instrument field
                        self._state_manager.update_last_bar(instrument, bar_time_utc)
        
        elif event_type == "ONBARUPDATE_CALLED":
            # Bar event: do NOT update_engine_tick - canonical liveness is ENGINE_TICK_CALLSITE and ENGINE_ALIVE.
            # Bar events are data-flow only; stale bar events in tail must not resurrect engine_alive.
            # Track last bar time per execution instrument contract (authoritative)
            # CRITICAL: Use execution_instrument_full_name for bar tracking (e.g., "MES 03-26")
            if not self._should_accept_bar_event(timestamp_utc):
                return
            execution_instrument_full_name = data.get("execution_instrument_full_name") or event.get("execution_instrument_full_name")
            instrument = event.get("instrument") or data.get("instrument")
            if execution_instrument_full_name:
                # Use event timestamp as bar time proxy (OnBarUpdate is called when bar arrives)
                self._state_manager.update_last_bar(execution_instrument_full_name, timestamp_utc)
                logger.debug(f"ONBARUPDATE_CALLED: Updated last_bar for {execution_instrument_full_name} at {timestamp_utc.isoformat()}")
            elif instrument:
                # Backward compatibility: fall back to instrument field if execution_instrument_full_name not present
                # Old events may only have canonical instrument - handle gracefully
                self._state_manager.update_last_bar(instrument, timestamp_utc)
                logger.debug(f"ONBARUPDATE_CALLED: Updated last_bar for {instrument} (fallback) at {timestamp_utc.isoformat()}")
            else:
                logger.warning(f"ONBARUPDATE_CALLED: No instrument found in event {event.get('event_seq')}")
        
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
        
        elif event_type == "ENGINE_ALIVE":
            # ENGINE_ALIVE: Strategy heartbeat (every N bars in Realtime)
            # Fallback liveness when ENGINE_TICK_CALLSITE not emitted (e.g. older DLL)
            self._state_manager.update_engine_tick(timestamp_utc)

        elif event_type == "ENGINE_TIMER_HEARTBEAT":
            # ENGINE_TIMER_HEARTBEAT: Timer-based heartbeat when market closed (no ticks)
            # Ensures ENGINE ALIVE persists on weekends / no bars
            self._state_manager.update_engine_tick(timestamp_utc)
            th_raw = data.get("timetable_hash") or event.get("timetable_hash")
            td_raw = data.get("trading_date") or event.get("trading_date")
            th_val = str(th_raw).strip() if th_raw is not None else None
            if th_val == "":
                th_val = None
            td_val = str(td_raw).strip() if td_raw is not None else None
            if td_val == "":
                td_val = None
            if th_val or td_val:
                self._state_manager.update_robot_heartbeat_timetable(th_val, td_val, timestamp_utc)
        
        elif event_type == "IDENTITY_INVARIANTS_STATUS":
            # PHASE 3.1: Update identity invariants status
            # CRITICAL: Extract from payload string if not in data dict (C# anonymous object serialization)
            pass_value = data.get("pass")
            violations = data.get("violations", [])
            canonical_instrument = data.get("canonical_instrument", "")
            execution_instrument = data.get("execution_instrument", "")
            stream_ids = data.get("stream_ids", [])
            checked_at_utc_str = data.get("checked_at_utc", "")
            
            # If fields are missing, try to extract from payload string
            if pass_value is None and "payload" in data:
                payload_str = data.get("payload", "")
                if isinstance(payload_str, str):
                    try:
                        # Extract pass value from payload: "pass = True" or "pass = False" or "pass = [REDACTED]"
                        # If redacted, check the note field for "passed" or "failed"
                        pass_match = re.search(r'pass\s*=\s*([^,\s}]+)', payload_str)
                        if pass_match:
                            pass_str = pass_match.group(1).strip()
                            # Handle boolean values
                            if pass_str.lower() in ('true', 'true', '1'):
                                pass_value = True
                            elif pass_str.lower() in ('false', 'false', '0'):
                                pass_value = False
                            elif '[redacted]' in pass_str.lower() or '[REDACTED]' in pass_str:
                                # Value is redacted - check note for hint
                                # Note field comes after checked_at_utc, extract everything after "note ="
                                note_match = re.search(r'note\s*=\s*([^}]+)', payload_str, re.IGNORECASE)
                                if note_match:
                                    note = note_match.group(1).strip().lower()
                                    logger.info(f"Extracted note from identity payload: {note[:150]}")
                                    # Check for positive indicators first
                                    if 'passed' in note or 'consistent' in note or 'all identity invariants passed' in note:
                                        pass_value = True
                                        logger.info(f"Identity check PASSED based on note: '{note[:100]}'")
                                    elif 'failed' in note or 'violation' in note or 'inconsistent' in note:
                                        pass_value = False
                                        logger.info(f"Identity check FAILED based on note: '{note[:100]}'")
                                    else:
                                        # If note is unclear but contains "passed" anywhere, assume pass
                                        if 'passed' in note.lower():
                                            pass_value = True
                                            logger.info(f"Identity check PASSED (fallback) based on note containing 'passed': '{note[:100]}'")
                                        else:
                                            pass_value = None  # Unknown
                                            logger.warning(f"Identity note unclear, cannot determine pass/fail: '{note[:100]}'")
                                else:
                                    logger.warning("No note field found in identity payload - cannot determine pass/fail")
                                    pass_value = None
                            else:
                                logger.warning(f"Unknown pass_str format: '{pass_str}' - cannot determine pass/fail")
                                pass_value = None
                        
                        # Extract violations if present
                        if not violations:
                            # Check if violations list is mentioned (may be empty)
                            violations_match = re.search(r'violations\s*=\s*([^,}]+)', payload_str)
                            # If violations list is not empty, we'd need more complex parsing
                            # For now, leave as empty list if not explicitly found
                        
                        # Extract canonical_instrument
                        if not canonical_instrument:
                            canon_match = re.search(r'canonical_instrument\s*=\s*([^,}]+)', payload_str)
                            if canon_match:
                                canonical_instrument = canon_match.group(1).strip()
                        
                        # Extract execution_instrument
                        if not execution_instrument:
                            exec_match = re.search(r'execution_instrument\s*=\s*([^,}]+)', payload_str)
                            if exec_match:
                                execution_instrument = exec_match.group(1).strip()
                        
                        # Extract checked_at_utc
                        if not checked_at_utc_str:
                            checked_match = re.search(r'checked_at_utc\s*=\s*([^,}]+)', payload_str)
                            if checked_match:
                                checked_at_utc_str = checked_match.group(1).strip()
                    except Exception as e:
                        logger.error(f"Failed to parse identity payload: {e}", exc_info=True)
                        # Don't set pass_value here - let it fall through to default
            
            # Default pass_value to False if still None
            if pass_value is None:
                pass_value = False
            
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
            # Do not drive liveness - canonical sources are ENGINE_TICK_CALLSITE and ENGINE_ALIVE.
            # Recovery will be reflected by the next tick/heartbeat.
            pass
        
        elif event_type in (
            "DISCONNECT_FAIL_CLOSED_ENTERED",
            "DISCONNECT_RECOVERY_STARTED",
            "DISCONNECT_RECOVERY_COMPLETE",
            "DISCONNECT_RECOVERY_ABORTED",
            "CONNECTION_RECOVERY_RESOLVED",  # legacy IEA completion event name (prefer DISCONNECT_RECOVERY_COMPLETE in robot)
        ):
            mapped = event_type
            if event_type == "CONNECTION_RECOVERY_RESOLVED":
                mapped = "DISCONNECT_RECOVERY_COMPLETE"
            self._state_manager.update_recovery_state(mapped, timestamp_utc)
        
        elif event_type in ("CONNECTION_LOST", "CONNECTION_LOST_SUSTAINED", "CONNECTION_RECOVERED", "CONNECTION_RECOVERED_NOTIFICATION", "CONNECTION_CONFIRMED"):
            # CONNECTION_RECOVERED_NOTIFICATION and CONNECTION_CONFIRMED are connection-ok signals
            connection_status = "ConnectionLost" if "LOST" in event_type else "Connected"
            # Skip session metrics (disconnect count, downtime) for old events from tail replay
            age_sec = (datetime.now(timezone.utc) - timestamp_utc).total_seconds()
            skip_session_metrics = age_sec > SESSION_CONNECTION_EVENT_MAX_AGE_SECONDS
            self._state_manager.update_connection_status(connection_status, timestamp_utc, skip_session_metrics=skip_session_metrics)
        
        elif event_type == "CONNECTIVITY_DAILY_SUMMARY":
            self._state_manager.update_connectivity_daily_summary(data or {})
        
        elif event_type == "KILL_SWITCH_ACTIVE":
            self._state_manager.update_kill_switch(True)
        
        elif event_type == "STREAM_STATE_TRANSITION":
            # Priority: 1) timetable, 2) event.trading_date, 3) data.trading_date, 4) derive from event timestamp (CME rollover)
            # Without fallback 4, events are skipped when timetable poll hasn't run yet (first ~60s)
            trading_date = (
                self._state_manager.get_trading_date()
                or event.get("trading_date")
                or data.get("trading_date")
            )
            if not trading_date and timestamp_utc:
                trading_date = _trading_date_from_timestamp(timestamp_utc)
                logger.debug(
                    f"STREAM_STATE_TRANSITION: derived trading_date={trading_date} from event timestamp "
                    f"(timetable not yet loaded)"
                )
            if not trading_date:
                logger.debug(
                    f"STREAM_STATE_TRANSITION skipped: no trading_date "
                    f"(event trading_date: {event.get('trading_date')}, stream: {event.get('stream')})"
                )
                return
            
            stream = event.get("stream")
            instrument = event.get("instrument")
            execution_instrument = event.get("execution_instrument")  # PHASE 3: Robot may emit both
            execution_instrument_full_name = data.get("execution_instrument_full_name") or event.get("execution_instrument_full_name")  # Full contract name (e.g., "M2K 03-26")
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
                # CRITICAL - Backward Compatibility: Only pass execution_instrument_full_name if present
                # Old events won't have this field - handle gracefully
                self._state_manager.update_stream_state(
                    trading_date, canonical_stream, new_state,
                    state_entry_time_utc=state_entry_time_utc,
                    execution_instrument=execution_instrument_full_name  # May be None for old events
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
                    if execution_instrument_full_name:
                        info.execution_instrument = execution_instrument_full_name
                    if session:
                        info.session = session
                    if slot_time_chicago:
                        info.slot_time_chicago = slot_time_chicago
                    
                    # CRITICAL: Clear ranges when transitioning away from RANGE_LOCKED or OPEN
                    # This prevents old ranges from persisting when streams restart or transition to new states
                    if info.state in ("RANGE_LOCKED", "OPEN") and new_state not in ("RANGE_LOCKED", "OPEN"):
                        logger.debug(
                            f"Clearing ranges for stream {canonical_stream} ({trading_date}): "
                            f"transitioning from RANGE_LOCKED to {new_state}"
                        )
                        info.range_high = None
                        info.range_low = None
                        info.freeze_close = None
                        info.range_invalidated = False
                    
                    # Extract range values from data dict if transitioning to RANGE_LOCKED or OPEN
                    if new_state in ("RANGE_LOCKED", "OPEN"):
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
        
        elif event_type == "SLOT_END_SUMMARY":
            # Update stream state with slot summary: trade_executed, reason
            trading_date = (
                self._state_manager.get_trading_date()
                or event.get("trading_date")
                or data.get("trading_date")
            )
            stream = event.get("stream")
            if not trading_date or not stream:
                # Fallback: parse from slot_instance_key (format: YM1_07:30_2026-03-11)
                slot_key = data.get("slot_instance_key", "")
                if slot_key and "_" in slot_key:
                    parts = slot_key.split("_")
                    if len(parts) >= 3:
                        stream = stream or parts[0]
                        trading_date = trading_date or parts[-1]
            if not trading_date or not stream:
                logger.debug(f"SLOT_END_SUMMARY skipped: no trading_date or stream")
                return
            canonical_stream = canonicalize_stream(stream, event.get("execution_instrument") or event.get("instrument") or "")
            key = (trading_date, canonical_stream)
            if key in self._state_manager._stream_states:
                info = self._state_manager._stream_states[key]
                trade_executed = data.get("trade_executed")
                if trade_executed is not None:
                    info.trade_executed = bool(trade_executed) if not isinstance(trade_executed, bool) else trade_executed
                reason = data.get("reason")
                if reason is not None:
                    info.slot_reason = str(reason).strip()
                if data.get("range_high") is not None and info.range_high is None:
                    info.range_high = float(data["range_high"])
                if data.get("range_low") is not None and info.range_low is None:
                    info.range_low = float(data["range_low"])

        elif event_type in ("STREAM_STAND_DOWN", "MARKET_CLOSE_NO_TRADE"):
            # Standardized fields are now always at top level (plan requirement #1)
            # MARKET_CLOSE_NO_TRADE is treated the same as STREAM_STAND_DOWN
            # Prefer timetable's trading_date; fallback to event's for startup (same as STREAM_STATE_TRANSITION)
            trading_date = (
                self._state_manager.get_trading_date()
                or event.get("trading_date")
                or data.get("trading_date")
            )
            if not trading_date:
                logger.debug(f"{event_type} skipped: no trading_date")
                return
            stream = event.get("stream")
            instrument = event.get("instrument")
            execution_instrument = event.get("execution_instrument")  # PHASE 3: Robot may emit both
            execution_instrument_full_name = data.get("execution_instrument_full_name") or event.get("execution_instrument_full_name")  # Full contract name
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
                    commit_reason=commit_reason,
                    execution_instrument=execution_instrument_full_name  # May be None for old events
                )
                # Update instrument info if available
                if canonical_instrument:
                    key = (trading_date, canonical_stream)
                    if key in self._state_manager._stream_states:
                        self._state_manager._stream_states[key].instrument = canonical_instrument
        
        elif event_type == "RANGE_INVALIDATED":
            # Standardized fields are now always at top level (plan requirement #1)
            # Prefer timetable's trading_date; fallback to event's for startup (same as STREAM_STATE_TRANSITION)
            trading_date = (
                self._state_manager.get_trading_date()
                or event.get("trading_date")
                or data.get("trading_date")
            )
            if not trading_date:
                logger.debug(f"RANGE_INVALIDATED skipped: no trading_date")
                return
            stream = event.get("stream")
            instrument = event.get("instrument")
            execution_instrument = event.get("execution_instrument")  # PHASE 3: Robot may emit both
            execution_instrument_full_name = data.get("execution_instrument_full_name") or event.get("execution_instrument_full_name")  # Full contract name
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
                    commit_reason="RANGE_INVALIDATED",
                    execution_instrument=execution_instrument_full_name  # May be None for old events
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
            # Prefer timetable's trading_date; fallback to event's for startup rebuild
            trading_date = self._state_manager.get_trading_date() or event.get("trading_date") or data.get("trading_date")
            if not trading_date:
                logger.debug(f"RANGE_LOCKED skipped: no trading_date available")
                return
            stream = event.get("stream")
            instrument = event.get("instrument")
            execution_instrument = event.get("execution_instrument")  # PHASE 3: Robot may emit both
            execution_instrument_full_name = data.get("execution_instrument_full_name") or event.get("execution_instrument_full_name")  # Full contract name
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
                    state_entry_time_utc=timestamp_utc,
                    execution_instrument=execution_instrument_full_name  # May be None for old events
                )
                # Update instrument, session, slot_time, and range values
                key = (trading_date, canonical_stream)
                if key in self._state_manager._stream_states:
                    info = self._state_manager._stream_states[key]
                    if canonical_instrument:
                        info.instrument = canonical_instrument
                    if execution_instrument_full_name:
                        info.execution_instrument = execution_instrument_full_name
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
            # Prefer timetable's trading_date; fallback to event's for startup rebuild
            trading_date = self._state_manager.get_trading_date() or event.get("trading_date") or data.get("trading_date")
            if not trading_date:
                logger.debug(f"RANGE_LOCK_SNAPSHOT skipped: no trading_date available")
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
        
        elif event_type in ("RANGE_LOCKED_RESTORED_FROM_HYDRATION", "RANGE_LOCKED_RESTORED_FROM_RANGES"):
            # Robot emits these when restoring RANGE_LOCKED from hydration/ranges log on restart.
            # Same structure as RANGE_LOCKED: stream, instrument, session, slot_time at top level;
            # range_high, range_low in data. Process identically to populate metadata for display.
            trading_date = self._state_manager.get_trading_date() or event.get("trading_date") or data.get("trading_date")
            if not trading_date:
                logger.debug(f"{event_type} skipped: no trading_date available")
                return
            stream = event.get("stream") or data.get("stream_id")
            instrument = event.get("instrument")
            execution_instrument = event.get("execution_instrument")
            execution_instrument_full_name = data.get("execution_instrument_full_name") or event.get("execution_instrument_full_name")
            canonical_instrument_field = event.get("canonical_instrument")
            session = event.get("session")
            slot_time_chicago = event.get("slot_time_chicago") or data.get("slot_time_chicago")
            slot_time_utc_str = event.get("slot_time_utc") or data.get("slot_time_utc")
            range_high = data.get("range_high")
            range_low = data.get("range_low")
            freeze_close = data.get("freeze_close")
            state_entry_time_utc_str = data.get("state_entry_time_utc") or event.get("timestamp_utc")
            
            if canonical_instrument_field:
                canonical_instrument = canonical_instrument_field
                canonical_stream = stream
                if not execution_instrument:
                    execution_instrument = instrument if instrument and instrument != canonical_instrument else instrument
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
                state_entry_time_utc = self._parse_timestamp(state_entry_time_utc_str) if state_entry_time_utc_str else timestamp_utc
                self._state_manager.update_stream_state(
                    trading_date, canonical_stream, "RANGE_LOCKED",
                    state_entry_time_utc=state_entry_time_utc,
                    execution_instrument=execution_instrument_full_name
                )
                key = (trading_date, canonical_stream)
                if key in self._state_manager._stream_states:
                    info = self._state_manager._stream_states[key]
                    if canonical_instrument:
                        info.instrument = canonical_instrument
                    if execution_instrument_full_name:
                        info.execution_instrument = execution_instrument_full_name
                    if session:
                        info.session = session
                    if slot_time_chicago:
                        info.slot_time_chicago = slot_time_chicago
                    if slot_time_utc_str:
                        info.slot_time_utc = slot_time_utc_str
                    if range_high is not None:
                        info.range_high = float(range_high)
                    if range_low is not None:
                        info.range_low = float(range_low)
                    if freeze_close is not None:
                        info.freeze_close = float(freeze_close)
                logger.debug(f"Processed {event_type}: {canonical_stream} ({trading_date}) range={range_high}/{range_low}")
        
        elif event_type == "EXECUTION_BLOCKED":
            self._state_manager.record_execution_blocked(timestamp_utc)
        
        elif event_type == "PROTECTIVE_ORDERS_FAILED_FLATTENED":
            self._state_manager.record_protective_failure(timestamp_utc)
        
        elif event_type in (
            "PROTECTIVE_ORDERS_SUBMITTED",
            "PROTECTIVE_ORDERS_SUBMITTED_FROM_RECOVERY_QUEUE",
        ):
            intent_id = data.get("intent_id") or event.get("intent_id")
            if intent_id:
                self._state_manager.record_protective_order_submitted(str(intent_id), timestamp_utc)
        elif event_type == "PROTECTIVES_PLACED":
            # Proof event logged immediately after PROTECTIVE_ORDERS_SUBMITTED; intent_id is in data.
            intent_id = data.get("intent_id") or event.get("intent_id")
            if intent_id:
                self._state_manager.record_protective_order_submitted(str(intent_id), timestamp_utc)
        
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
                # Standardized fields: trading_date at top level
                trading_date = event.get("trading_date") or data.get("trading_date")
                # PHASE 2: Use canonical stream ID and instrument for intent exposure
                self._state_manager.update_intent_exposure(
                    intent_id, canonical_stream_id, canonical_instrument, direction,
                    entry_filled_qty=entry_filled_qty,
                    state="ACTIVE",
                    entry_filled_at_utc=timestamp_utc,
                    trading_date=trading_date
                )
        
        elif event_type == "INTENT_EXPOSURE_CLOSED":
            intent_id = data.get("intent_id")
            if intent_id and intent_id in self._state_manager._intent_exposures:
                exposure = self._state_manager._intent_exposures[intent_id]
                exposure.state = "CLOSED"
        
        elif event_type == "TRADE_RECONCILED":
            # Orphaned journal closed via reconciliation; broker was flat. Transition stream to DONE.
            intent_id = data.get("intent_id")
            stream = event.get("stream") or data.get("stream")
            trading_date = event.get("trading_date") or data.get("trading_date")
            completion_reason = data.get("completion_reason", "RECONCILIATION_BROKER_FLAT")
            if intent_id and intent_id in self._state_manager._intent_exposures:
                self._state_manager._intent_exposures[intent_id].state = "CLOSED"
            if trading_date and stream:
                instrument = data.get("instrument") or event.get("instrument") or ""
                execution_instrument = event.get("execution_instrument") or instrument
                canonical_stream = canonicalize_stream(stream, execution_instrument) if execution_instrument else stream
                self._state_manager.update_stream_state(
                    trading_date, canonical_stream, "DONE",
                    committed=True, commit_reason=completion_reason,
                    state_entry_time_utc=timestamp_utc
                )
                logger.info(
                    f"TRADE_RECONCILED: Stream {canonical_stream} ({trading_date}) -> DONE "
                    f"(intent_id={intent_id}, reason={completion_reason})"
                )

        elif event_type == "TRADE_COMPLETED":
            # StreamStateMachine terminal (injects + normal completions); align exposure/stream with robot.
            intent_id = data.get("intent_id") or event.get("intent_id")
            stream = data.get("stream") or event.get("stream")
            trading_date = event.get("trading_date") or data.get("trading_date")
            if intent_id and str(intent_id) in self._state_manager._intent_exposures:
                self._state_manager._intent_exposures[str(intent_id)].state = "CLOSED"
            if trading_date and stream:
                instrument = data.get("instrument") or event.get("instrument") or ""
                execution_instrument = event.get("execution_instrument") or instrument
                canonical_stream = canonicalize_stream(stream, execution_instrument) if execution_instrument else stream
                completion_reason = data.get("completion_reason") or data.get("exit_reason") or "TRADE_COMPLETED"
                self._state_manager.update_stream_state(
                    trading_date, canonical_stream, "DONE",
                    committed=True, commit_reason=str(completion_reason),
                    state_entry_time_utc=timestamp_utc
                )
        
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
            # Try multiple sources: data dict first (most reliable), then top-level event, then instrument fields
            if not self._should_accept_bar_event(timestamp_utc):
                return
            execution_instrument_full_name = (
                data.get("execution_instrument_full_name") or 
                event.get("execution_instrument_full_name") or
                data.get("instrument") or
                event.get("instrument")
            )
            
            if execution_instrument_full_name and execution_instrument_full_name.strip():
                self._state_manager.update_last_bar(execution_instrument_full_name.strip(), timestamp_utc)
                logger.debug(f"BAR_ACCEPTED: Updated last_bar for {execution_instrument_full_name} at {timestamp_utc.isoformat()}")
            else:
                # Last resort: try to extract from payload if present
                payload = data.get("payload", "")
                if isinstance(payload, str) and "instrument" in payload:
                    try:
                        match = re.search(r'instrument\s*=\s*([^,}]+)', payload)
                        if match:
                            extracted_instrument = match.group(1).strip()
                            self._state_manager.update_last_bar(extracted_instrument, timestamp_utc)
                            logger.debug(f"BAR_ACCEPTED: Extracted and updated last_bar for {extracted_instrument} from payload at {timestamp_utc.isoformat()}")
                        else:
                            logger.warning(f"BAR_ACCEPTED: No instrument found in event {event.get('event_seq')}, payload={payload[:100]}")
                    except Exception as e:
                        logger.warning(f"BAR_ACCEPTED: Failed to extract instrument from payload for event {event.get('event_seq')}: {e}")
                else:
                    logger.warning(f"BAR_ACCEPTED: No instrument found in event {event.get('event_seq')}, data keys: {list(data.keys())}")
        
        elif event_type == "BAR_RECEIVED_NO_STREAMS":
            # Track bar arrival even when no streams exist (for data stall detection)
            # This ensures watchdog can detect data stalls even before streams are created
            if not self._should_accept_bar_event(timestamp_utc):
                return
            # Try multiple sources: data dict first (most reliable), then top-level event, then instrument fields
            execution_instrument_full_name = (
                data.get("execution_instrument_full_name") or 
                event.get("execution_instrument_full_name") or
                data.get("instrument") or
                event.get("instrument")
            )
            
            if execution_instrument_full_name and execution_instrument_full_name.strip():
                self._state_manager.update_last_bar(execution_instrument_full_name.strip(), timestamp_utc)
                logger.debug(f"BAR_RECEIVED_NO_STREAMS: Updated last_bar for {execution_instrument_full_name} at {timestamp_utc.isoformat()}")
            else:
                # Last resort: try to extract from payload if present
                payload = data.get("payload", "")
                if isinstance(payload, str) and "instrument" in payload:
                    try:
                        match = re.search(r'instrument\s*=\s*([^,}]+)', payload)
                        if match:
                            extracted_instrument = match.group(1).strip()
                            self._state_manager.update_last_bar(extracted_instrument, timestamp_utc)
                            logger.debug(f"BAR_RECEIVED_NO_STREAMS: Extracted and updated last_bar for {extracted_instrument} from payload at {timestamp_utc.isoformat()}")
                        else:
                            logger.warning(f"BAR_RECEIVED_NO_STREAMS: No instrument found in event {event.get('event_seq')}, payload={payload[:100]}")
                    except Exception as e:
                        logger.warning(f"BAR_RECEIVED_NO_STREAMS: Failed to extract instrument from payload for event {event.get('event_seq')}: {e}")
                else:
                    logger.warning(f"BAR_RECEIVED_NO_STREAMS: No instrument found in event {event.get('event_seq')}, data keys: {list(data.keys())}")
        
        elif event_type == "DATA_LOSS_DETECTED":
            # Use execution_instrument_full_name if available, otherwise fall back to instrument
            execution_instrument_full_name = data.get("execution_instrument_full_name") or event.get("execution_instrument_full_name")
            instrument = event.get("instrument")
            if execution_instrument_full_name:
                self._state_manager.mark_data_loss(execution_instrument_full_name, timestamp_utc)
            elif instrument:
                # Backward compatibility: fall back to instrument field
                self._state_manager.mark_data_loss(instrument, timestamp_utc)
        
        elif event_type == "DATA_STALL_RECOVERED":
            # Use execution_instrument_full_name if available, otherwise fall back to instrument
            if not self._should_accept_bar_event(timestamp_utc):
                return
            execution_instrument_full_name = data.get("execution_instrument_full_name") or event.get("execution_instrument_full_name")
            instrument = event.get("instrument")
            if execution_instrument_full_name:
                self._state_manager.update_last_bar(execution_instrument_full_name, timestamp_utc)
            elif instrument:
                # Backward compatibility: fall back to instrument field
                self._state_manager.update_last_bar(instrument, timestamp_utc)
        
        elif event_type == "DUPLICATE_INSTANCE_DETECTED":
            # Extract duplicate instance detection information
            account = data.get("account", "")
            execution_instrument = data.get("execution_instrument", "")
            instance_id = data.get("instance_id", "")
            error_msg = data.get("error", "")
            
            logger.warning(
                f"DUPLICATE_INSTANCE_DETECTED: account={account}, execution_instrument={execution_instrument}, "
                f"instance_id={instance_id}, error={error_msg[:100] if error_msg else 'N/A'}"
            )
            
            self._state_manager.record_duplicate_instance(
                account=account,
                execution_instrument=execution_instrument,
                instance_id=instance_id,
                timestamp_utc=timestamp_utc,
                error_message=error_msg
            )
        
        elif event_type == "LEDGER_INVARIANT_VIOLATION":
            self._state_manager.record_ledger_invariant_violation(timestamp_utc)

        elif event_type == "EXECUTION_GATE_INVARIANT_VIOLATION":
            self._state_manager.record_execution_gate_invariant_violation(timestamp_utc)

        elif event_type == "BROKER_FLATTEN_FILL_RECOGNIZED":
            self._state_manager.record_broker_flatten_fill(timestamp_utc)

        elif event_type == "EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL":
            self._state_manager.record_execution_update_unknown_order_critical(timestamp_utc)

        elif event_type == "EXECUTION_FILL_BLOCKED_TRADING_DATE_NULL":
            self._state_manager.record_execution_fill_blocked(timestamp_utc)

        elif event_type == "EXECUTION_FILL_UNMAPPED":
            self._state_manager.record_execution_fill_unmapped(timestamp_utc)

        elif event_type == "RECONCILIATION_QTY_MISMATCH":
            self._state_manager.record_reconciliation_qty_mismatch(timestamp_utc)

        elif event_type == "RECOVERY_POSITION_UNMATCHED":
            instrument = event.get("instrument") or data.get("instrument") or data.get("execution_instrument")
            if instrument:
                self._state_manager.record_unresolved_unmatched(instrument)

        elif event_type in ("ADOPTION_SUCCESS", "RECONCILIATION_RECOVERY_ADOPTION_SUCCESS"):
            self._state_manager.set_adoption_grace_expired(False, timestamp_utc)
            instrument = event.get("instrument") or data.get("instrument") or data.get("execution_instrument")
            if instrument:
                self._state_manager.clear_unresolved_unmatched(instrument)

        elif event_type == "FORCED_FLATTEN_POSITION_CLOSED":
            # Only clear when instrument is explicit — do NOT clear on global/ambiguous events
            instrument = event.get("instrument") or data.get("instrument") or data.get("execution_instrument")
            if instrument:
                self._state_manager.clear_unresolved_unmatched(instrument)
            # RECONCILIATION_PASS_SUMMARY and SESSION_FORCED_FLATTENED are global/ambiguous —
            # do NOT use to clear per-instrument unmatched state

        elif event_type == "ORDER_REGISTRY_BROKER_ID_LINKED":
            canon = (data.get("canonical_broker_order_id") or data.get("canonical") or "").strip()
            alt = (data.get("broker_order_id") or "").strip()
            if canon and alt:
                self._state_manager.record_broker_order_id_link(canon, alt)

        elif event_type == "ORDER_SUBMIT_SUCCESS":
            # Phase 7-8: Track for stuck order and latency spike detection
            broker_order_id = data.get("broker_order_id") or event.get("broker_order_id")
            if broker_order_id:
                order_type = str(data.get("order_type", "entry") or "entry").upper()
                if "TARGET" in order_type:
                    role = "target"
                elif "STOP" in order_type or "PROTECTIVE" in order_type:
                    role = "stop"
                else:
                    role = "entry"
                intent_id_submit = (data.get("intent_id") or event.get("intent_id") or "").strip()
                if role in ("stop", "target") and intent_id_submit:
                    self._state_manager.record_protective_order_submitted(intent_id_submit, timestamp_utc)
                self._state_manager.record_order_submitted(
                    broker_order_id=broker_order_id,
                    submitted_at=timestamp_utc,
                    intent_id=intent_id_submit,
                    instrument=data.get("instrument", "") or event.get("instrument", ""),
                    role=role,
                    stream_key=data.get("stream_key", "") or data.get("stream", "") or event.get("stream_id", ""),
                )

        elif event_type == "EXECUTION_FILLED":
            # Phase 8: Check latency spike on fill
            broker_order_id = (data.get("broker_order_id") or event.get("broker_order_id") or "").strip()
            order_id_alt = (data.get("order_id") or "").strip()
            primary = broker_order_id or order_id_alt or event.get("broker_order_id") or event.get("order_id") or ""
            if primary:
                qty = data.get("quantity") or data.get("qty") or data.get("fill_quantity")
                price = data.get("price") or data.get("fill_price") or data.get("avg_fill_price")
                inst = (data.get("instrument") or event.get("instrument") or "").strip()
                iid = (data.get("intent_id") or event.get("intent_id") or "").strip()
                self._state_manager.record_order_filled(
                    broker_order_id=broker_order_id or primary,
                    filled_at=timestamp_utc,
                    qty=qty,
                    price=float(price) if price is not None else None,
                    order_id_alt=order_id_alt if order_id_alt else None,
                    instrument=inst,
                    intent_id=iid,
                )

        elif event_type == "EXECUTION_PARTIAL_FILL":
            rem = data.get("remaining_qty")
            try:
                rem_int = int(rem) if rem is not None and rem != "" else None
            except (TypeError, ValueError):
                rem_int = None
            if rem_int == 0:
                broker_order_id = (data.get("broker_order_id") or event.get("broker_order_id") or "").strip()
                order_id_alt = (data.get("order_id") or "").strip()
                primary = broker_order_id or order_id_alt or event.get("broker_order_id") or ""
                if primary:
                    inst = (data.get("instrument") or event.get("instrument") or "").strip()
                    iid = (data.get("intent_id") or event.get("intent_id") or "").strip()
                    qty = data.get("fill_quantity") or data.get("quantity") or data.get("qty")
                    price = data.get("fill_price") or data.get("price")
                    self._state_manager.record_order_filled(
                        broker_order_id=broker_order_id or primary,
                        filled_at=timestamp_utc,
                        qty=qty,
                        price=float(price) if price is not None else None,
                        order_id_alt=order_id_alt if order_id_alt else None,
                        instrument=inst,
                        intent_id=iid,
                    )

        elif event_type == "ORDER_CANCELLED":
            # Phase 7-8: Remove from pending
            broker_order_id = (data.get("broker_order_id") or event.get("broker_order_id") or "").strip()
            order_id_alt = (data.get("order_id") or "").strip()
            primary = broker_order_id or order_id_alt
            if primary:
                self._state_manager.record_order_cancelled(
                    broker_order_id=broker_order_id or primary,
                    order_id_alt=order_id_alt if order_id_alt and order_id_alt != (broker_order_id or primary) else None,
                    timestamp_utc=timestamp_utc,
                )

        elif event_type == "ORDER_REJECTED":
            # Phase 7-8: Remove from pending (rejected orders never fill/cancel; were falsely triggering ORDER_STUCK_DETECTED)
            broker_order_id = (data.get("broker_order_id") or event.get("broker_order_id") or "").strip()
            order_id_alt = (data.get("order_id") or "").strip()
            primary = broker_order_id or order_id_alt
            if primary:
                self._state_manager.record_order_cancelled(
                    broker_order_id=broker_order_id or primary,
                    order_id_alt=order_id_alt if order_id_alt and order_id_alt != (broker_order_id or primary) else None,
                    timestamp_utc=timestamp_utc,
                    event_type="ORDER_REJECTED",
                )

        elif event_type == "EXECUTION_POLICY_VALIDATION_FAILED":
            # Extract execution policy validation failure information
            errors = data.get("errors", [])
            unique_execution_instruments = data.get("unique_execution_instruments", [])
            note = data.get("note", "")
            
            logger.warning(
                f"EXECUTION_POLICY_VALIDATION_FAILED: {len(errors)} error(s), "
                f"execution_instruments={unique_execution_instruments}, note={note[:100] if note else 'N/A'}"
            )
            
            self._state_manager.record_execution_policy_failure(
                errors=errors,
                execution_instruments=unique_execution_instruments,
                timestamp_utc=timestamp_utc,
                note=note
            )

        elif event_type in (
            "MISMATCH_FAIL_CLOSED",
            "RECONCILIATION_MISMATCH_FAIL_CLOSED",
            "RECONCILIATION_MISMATCH_CLEARED",
            "RECONCILIATION_MISMATCH_DETECTED",
            "STATE_CONSISTENCY_GATE_ENGAGED",
            "STATE_CONSISTENCY_GATE_RELEASED",
            "STATE_CONSISTENCY_GATE_RECOVERY_FAILED",
        ):
            self._state_manager.update_reconciliation_gate_event(event_type, timestamp_utc, data if isinstance(data, dict) else {})

        elif event_type == "ADOPTION_GRACE_EXPIRED_UNOWNED":
            self._state_manager.set_adoption_grace_expired(True, timestamp_utc)
        
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
