"""
Event Feed Generator

Reads raw robot JSONL files, filters live-critical events, adds event_seq and timestamp_chicago,
and writes to frontend_feed.jsonl.

Automatically rotates frontend_feed.jsonl when it exceeds 100 MB to prevent disk space issues.
"""
import json
import logging
import shutil
from pathlib import Path
from typing import Dict, List, Optional, Set
from datetime import datetime, timezone
import pytz
from collections import defaultdict

from .config import (
    ROBOT_LOGS_DIR,
    FRONTEND_FEED_FILE,
    LIVE_CRITICAL_EVENT_TYPES,
)

logger = logging.getLogger(__name__)

CHICAGO_TZ = pytz.timezone("America/Chicago")


class EventFeedGenerator:
    """Generates frontend_feed.jsonl from raw robot logs."""
    
    def __init__(self):
        self._event_seq_by_run_id: Dict[str, int] = defaultdict(int)
        self._last_read_positions: Dict[str, int] = {}  # File path -> byte position
        # Rate limiting for very frequent events (per run_id)
        self._last_engine_tick_callsite_time: Dict[str, datetime] = {}  # run_id -> last written timestamp
        self._ENGINE_TICK_CALLSITE_RATE_LIMIT_SECONDS = 5  # Only write ENGINE_TICK_CALLSITE every 5 seconds
        # Rate limiting for BAR_RECEIVED_NO_STREAMS (very frequent, only write every 60 seconds)
        self._last_bar_received_no_streams_time: Dict[str, datetime] = {}  # run_id -> last written timestamp
        self._BAR_RECEIVED_NO_STREAMS_RATE_LIMIT_SECONDS = 60  # Only write BAR_RECEIVED_NO_STREAMS every 60 seconds
        
    def _convert_utc_to_chicago(self, utc_timestamp_str: str) -> str:
        """Convert UTC timestamp string to Chicago timezone."""
        try:
            # Parse UTC timestamp (ISO 8601 format)
            dt_utc = datetime.fromisoformat(utc_timestamp_str.replace('Z', '+00:00'))
            if dt_utc.tzinfo is None:
                dt_utc = dt_utc.replace(tzinfo=timezone.utc)
            
            # Convert to Chicago timezone
            dt_chicago = dt_utc.astimezone(CHICAGO_TZ)
            return dt_chicago.isoformat()
        except Exception as e:
            logger.warning(f"Failed to convert timestamp {utc_timestamp_str}: {e}")
            return utc_timestamp_str
    
    def _is_live_critical_event(self, event_type: str) -> bool:
        """Check if event type is live-critical."""
        return event_type in LIVE_CRITICAL_EVENT_TYPES
    
    def _extract_run_id(self, event: Dict) -> Optional[str]:
        """Extract run_id from event (handles both RobotLogEvent and converted formats)."""
        # RobotLogEvent uses "run_id"
        # Converted format might use "run_id" or "runId"
        return event.get("run_id") or event.get("runId")
    
    def _extract_event_type(self, event: Dict) -> str:
        """Extract event type from event (handles both RobotLogEvent and converted formats)."""
        # RobotLogEvent uses "@event" (but JSON serializes as "event")
        # Converted format might use "event_type" or "event"
        return event.get("event_type") or event.get("event") or event.get("@event", "")
    
    def _extract_timestamp_utc(self, event: Dict) -> Optional[str]:
        """Extract UTC timestamp from event (handles both RobotLogEvent and converted formats)."""
        # RobotLogEvent uses "ts_utc"
        # Converted format might use "timestamp_utc" or "timestamp"
        return event.get("timestamp_utc") or event.get("ts_utc") or event.get("timestamp")
    
    def _extract_trading_date(self, event: Dict) -> Optional[str]:
        """Extract trading_date from event."""
        # Standardized fields are now always at top level (plan requirement #1)
        trading_date = event.get("trading_date")
        return str(trading_date) if trading_date else None
    
    def _extract_stream(self, event: Dict) -> Optional[str]:
        """Extract stream from event."""
        # Standardized fields are now always at top level (plan requirement #1)
        stream = event.get("stream")
        return str(stream) if stream else None
    
    def _extract_instrument(self, event: Dict) -> Optional[str]:
        """Extract instrument from event."""
        # Standardized fields are now always at top level (plan requirement #1)
        instrument = event.get("instrument")
        return str(instrument) if instrument else None
    
    def _extract_session(self, event: Dict) -> Optional[str]:
        """Extract session from event."""
        # Standardized fields are now always at top level (plan requirement #1)
        session = event.get("session")
        return str(session) if session else None
    
    def _process_event(self, event: Dict) -> Optional[Dict]:
        """Process a single event and return frontend feed format, or None if filtered."""
        event_type = self._extract_event_type(event)
        
        # Filter: only live-critical events
        if not self._is_live_critical_event(event_type):
            return None
        
        # Filter out initialization STREAM_STATE_TRANSITION events (UNKNOWN -> *)
        # These are noise - we only care about runtime state transitions
        if event_type == "STREAM_STATE_TRANSITION":
            data = event.get("data", {})
            old_state = data.get("old_state", "")
            # Skip initialization transitions (UNKNOWN -> anything)
            if old_state == "UNKNOWN" or old_state == "":
                return None
        
        run_id = self._extract_run_id(event)
        if not run_id:
            logger.debug(f"Event missing run_id: {event_type}, skipping")
            return None
        
        # Rate limit ENGINE_TICK_CALLSITE events (very frequent, only write every 5 seconds)
        if event_type == "ENGINE_TICK_CALLSITE":
            timestamp_utc = self._extract_timestamp_utc(event)
            if timestamp_utc:
                try:
                    event_time = datetime.fromisoformat(timestamp_utc.replace('Z', '+00:00'))
                    if event_time.tzinfo is None:
                        event_time = event_time.replace(tzinfo=timezone.utc)
                    
                    last_written = self._last_engine_tick_callsite_time.get(run_id)
                    if last_written:
                        elapsed = (event_time - last_written).total_seconds()
                        if elapsed < self._ENGINE_TICK_CALLSITE_RATE_LIMIT_SECONDS:
                            # Rate limited - skip this event
                            logger.debug(
                                f"ENGINE_TICK_CALLSITE rate-limited: run_id={run_id}, "
                                f"elapsed={elapsed:.1f}s < {self._ENGINE_TICK_CALLSITE_RATE_LIMIT_SECONDS}s"
                            )
                            return None
                    
                    # Update last written time
                    self._last_engine_tick_callsite_time[run_id] = event_time
                    # Diagnostic: Log when ENGINE_TICK_CALLSITE is written to feed (rate-limited to avoid spam)
                    if not hasattr(self, '_last_tick_write_log_time'):
                        self._last_tick_write_log_time = {}
                    if run_id not in self._last_tick_write_log_time or \
                       (event_time - self._last_tick_write_log_time.get(run_id, event_time)).total_seconds() >= 30:
                        self._last_tick_write_log_time[run_id] = event_time
                        logger.debug(f"ENGINE_TICK_CALLSITE written to feed: run_id={run_id}, timestamp={event_time.isoformat()}")
                except Exception as e:
                    # If timestamp parsing fails, allow event through (better to log than miss)
                    logger.debug(f"Failed to parse timestamp for rate limiting: {e}")
        
        # Rate limit BAR_RECEIVED_NO_STREAMS events (very frequent, only write every 60 seconds)
        # This event fires every time a bar is received before streams are created, which can be very frequent
        # Rate limiting reduces feed file size and processing overhead while still tracking bar arrival
        if event_type == "BAR_RECEIVED_NO_STREAMS":
            timestamp_utc = self._extract_timestamp_utc(event)
            if timestamp_utc:
                try:
                    event_time = datetime.fromisoformat(timestamp_utc.replace('Z', '+00:00'))
                    if event_time.tzinfo is None:
                        event_time = event_time.replace(tzinfo=timezone.utc)
                    
                    last_written = self._last_bar_received_no_streams_time.get(run_id)
                    if last_written:
                        elapsed = (event_time - last_written).total_seconds()
                        if elapsed < self._BAR_RECEIVED_NO_STREAMS_RATE_LIMIT_SECONDS:
                            # Rate limited - skip this event
                            logger.debug(
                                f"BAR_RECEIVED_NO_STREAMS rate-limited: run_id={run_id}, "
                                f"elapsed={elapsed:.1f}s < {self._BAR_RECEIVED_NO_STREAMS_RATE_LIMIT_SECONDS}s"
                            )
                            return None
                    
                    # Update last written time
                    self._last_bar_received_no_streams_time[run_id] = event_time
                except Exception as e:
                    # If timestamp parsing fails, allow event through (better to log than miss)
                    logger.debug(f"Failed to parse timestamp for BAR_RECEIVED_NO_STREAMS rate limiting: {e}")
        
        # Increment event_seq for this run_id (starts at 1, increments by 1)
        if run_id not in self._event_seq_by_run_id:
            self._event_seq_by_run_id[run_id] = 0  # Will be incremented to 1
        self._event_seq_by_run_id[run_id] += 1
        event_seq = self._event_seq_by_run_id[run_id]
        
        # Get UTC timestamp
        timestamp_utc = self._extract_timestamp_utc(event)
        if not timestamp_utc:
            logger.debug(f"Event missing timestamp_utc: {event_type}, skipping")
            return None
        
        # Convert to Chicago timezone
        timestamp_chicago = self._convert_utc_to_chicago(timestamp_utc)
        
        # Extract fields
        trading_date = self._extract_trading_date(event)
        stream = self._extract_stream(event)
        instrument = self._extract_instrument(event)
        session = self._extract_session(event)
        
        # Extract slot_time_chicago and slot_time_utc (standardized top-level fields)
        slot_time_chicago = event.get("slot_time_chicago")
        slot_time_utc = event.get("slot_time_utc")
        
        # Extract execution_instrument and canonical_instrument (PHASE 3 fields)
        execution_instrument = event.get("execution_instrument")
        canonical_instrument = event.get("canonical_instrument")
        
        # Get data payload (flatten if needed)
        data = event.get("data", {})
        if not isinstance(data, dict):
            data = {}
        
        # Extract execution_instrument_full_name from payload string for bar events
        # RobotEvents.EngineBase serializes anonymous objects as payload strings
        # Format: "{ instrument = MES, execution_instrument_full_name = MES 03-26, ... }"
        # OR: "{ instrument = MCL, bar_timestamp_utc = ..., note = ... }" (no execution_instrument_full_name)
        if event_type in ("BAR_RECEIVED_NO_STREAMS", "BAR_ACCEPTED"):
            # Check if we need to extract from payload
            # Extract if field is missing, None, or empty string
            needs_extraction = (
                not data.get("execution_instrument_full_name") and 
                "payload" in data and 
                isinstance(data.get("payload"), str)
            )
            
            if needs_extraction:
                payload_str = data.get("payload")
                try:
                    import re
                    # First try to extract execution_instrument_full_name from payload string
                    # Format: "execution_instrument_full_name = MES 03-26" or "execution_instrument_full_name = MES"
                    match = re.search(r'execution_instrument_full_name\s*=\s*([^,}]+)', payload_str)
                    if match:
                        execution_instrument_full_name = match.group(1).strip()
                        data["execution_instrument_full_name"] = execution_instrument_full_name
                        logger.debug(f"Extracted execution_instrument_full_name='{execution_instrument_full_name}' from payload for {event_type}")
                    
                    # Also extract instrument if not already present or empty
                    if not data.get("instrument"):
                        inst_match = re.search(r'instrument\s*=\s*([^,}]+)', payload_str)
                        if inst_match:
                            data["instrument"] = inst_match.group(1).strip()
                            logger.debug(f"Extracted instrument='{data['instrument']}' from payload for {event_type}")
                    
                    # CRITICAL: If execution_instrument_full_name is still missing but instrument was extracted,
                    # use instrument as fallback for execution_instrument_full_name
                    if not data.get("execution_instrument_full_name") and data.get("instrument"):
                        data["execution_instrument_full_name"] = data["instrument"]
                        logger.debug(f"Using instrument='{data['instrument']}' as execution_instrument_full_name fallback for {event_type}")
                except Exception as e:
                    logger.warning(f"Failed to parse payload for {event_type}: {e}", exc_info=True)
        
        # For RANGE_LOCKED and RANGE_LOCK_SNAPSHOT events, extract range data from data dict
        # and ensure it's available at top level for event processor
        if event_type in ("RANGE_LOCKED", "RANGE_LOCK_SNAPSHOT"):
            # Range data might be in data dict or extra_data (dict or string)
            if "range_high" not in data and "extra_data" in data:
                extra_data = data.get("extra_data")
                if isinstance(extra_data, dict):
                    # extra_data is a dict - promote to data dict
                    if "range_high" in extra_data:
                        data["range_high"] = extra_data.get("range_high")
                    if "range_low" in extra_data:
                        data["range_low"] = extra_data.get("range_low")
                    if "freeze_close" in extra_data:
                        data["freeze_close"] = extra_data.get("freeze_close")
                elif isinstance(extra_data, str):
                    # extra_data is a string (C# anonymous object serialized)
                    # Parse format: "{ range_high = 49564, range_low = 49090, ... }"
                    try:
                        import re
                        # Extract range_high
                        high_match = re.search(r'range_high\s*=\s*([0-9.]+)', extra_data)
                        if high_match:
                            data["range_high"] = float(high_match.group(1))
                        # Extract range_low
                        low_match = re.search(r'range_low\s*=\s*([0-9.]+)', extra_data)
                        if low_match:
                            data["range_low"] = float(low_match.group(1))
                        # Extract freeze_close
                        freeze_match = re.search(r'freeze_close\s*=\s*([0-9.]+)', extra_data)
                        if freeze_match:
                            data["freeze_close"] = float(freeze_match.group(1))
                    except Exception:
                        pass  # Keep original extra_data if parsing fails
        
        # Extract execution_instrument_full_name from data dict for top-level access
        # Use instrument as fallback if execution_instrument_full_name is not available
        execution_instrument_full_name = data.get("execution_instrument_full_name") or data.get("instrument")
        
        # Build frontend feed event
        feed_event = {
            "event_seq": event_seq,
            "run_id": run_id,
            "timestamp_utc": timestamp_utc,
            "timestamp_chicago": timestamp_chicago,
            "event_type": event_type,
            "trading_date": trading_date,
            "stream": stream,
            "instrument": instrument,
            "session": session,
            "slot_time_chicago": slot_time_chicago,
            "slot_time_utc": slot_time_utc,
            "execution_instrument": execution_instrument,
            "canonical_instrument": canonical_instrument,
            "execution_instrument_full_name": execution_instrument_full_name,  # Top-level for easy access
            "data": data,
        }
        
        return feed_event
    
    def _read_log_file_incremental(self, log_file: Path) -> List[Dict]:
        """Read new lines from log file since last read position."""
        if not log_file.exists():
            return []
        
        events = []
        last_pos = self._last_read_positions.get(str(log_file), 0)
        
        try:
            # Use utf-8-sig to automatically handle UTF-8 BOM markers
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                # Check if file was rotated (file size is smaller than last position)
                current_size = f.seek(0, 2)  # Seek to end to get file size
                if current_size < last_pos:
                    # File was rotated or truncated - reset position to start
                    logger.info(
                        f"File rotation detected for {log_file.name}: "
                        f"file_size={current_size}, last_pos={last_pos}, resetting to 0"
                    )
                    last_pos = 0
                
                # Seek to last read position
                f.seek(last_pos)
                
                # Read new lines
                parse_errors = 0
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    
                    try:
                        event = json.loads(line)
                        events.append(event)
                    except json.JSONDecodeError:
                        # Silently skip malformed JSON lines (common in log files)
                        parse_errors += 1
                        continue
                
                # Log parse errors only once per file read, not per line
                if parse_errors > 0:
                    logger.debug(f"Skipped {parse_errors} malformed JSON lines in {log_file.name}")
                
                # Update last read position
                self._last_read_positions[str(log_file)] = f.tell()
        
        except Exception as e:
            logger.error(f"Error reading log file {log_file}: {e}", exc_info=True)
        
        return events
    
    def _find_robot_log_files(self) -> List[Path]:
        """Find all robot log files."""
        if not ROBOT_LOGS_DIR.exists():
            return []
        
        log_files = []
        # Find robot_ENGINE.jsonl and robot_<instrument>.jsonl files
        for log_file in ROBOT_LOGS_DIR.glob("robot_*.jsonl"):
            log_files.append(log_file)
        
        return sorted(log_files)
    
    def _rotate_feed_file_if_needed(self):
        """Rotate frontend_feed.jsonl if it exceeds size limit."""
        MAX_FILE_SIZE_MB = 100  # Rotate at 100 MB
        MAX_FILE_SIZE_BYTES = MAX_FILE_SIZE_MB * 1024 * 1024
        
        if not FRONTEND_FEED_FILE.exists():
            return
        
        try:
            file_size = FRONTEND_FEED_FILE.stat().st_size
            if file_size >= MAX_FILE_SIZE_BYTES:
                # Create archive directory
                archive_dir = FRONTEND_FEED_FILE.parent / "archive"
                archive_dir.mkdir(parents=True, exist_ok=True)
                
                # Generate archive filename with timestamp
                timestamp = datetime.now(CHICAGO_TZ).strftime("%Y%m%d_%H%M%S")
                archive_path = archive_dir / f"frontend_feed_{timestamp}.jsonl"
                
                # Move current file to archive
                shutil.move(str(FRONTEND_FEED_FILE), str(archive_path))
                logger.info(f"Rotated frontend_feed.jsonl ({file_size / (1024*1024):.2f} MB) -> archive/{archive_path.name}")
                
                # Reset read positions since we're starting fresh
                self._last_read_positions.clear()
        except Exception as e:
            logger.warning(f"Failed to rotate frontend_feed.jsonl: {e}")
    
    def process_new_events(self) -> int:
        """
        Process new events from robot log files and append to frontend_feed.jsonl.
        Returns number of events processed.
        """
        log_files = self._find_robot_log_files()
        if not log_files:
            logger.debug("No robot log files found")
            return 0
        
        # Ensure frontend feed file exists
        FRONTEND_FEED_FILE.parent.mkdir(parents=True, exist_ok=True)
        
        # Rotate if needed before processing
        self._rotate_feed_file_if_needed()
        
        processed_count = 0
        
        # Read new events from all log files
        all_events = []
        for log_file in log_files:
            events = self._read_log_file_incremental(log_file)
            all_events.extend(events)
        
        # Sort events by timestamp_utc to maintain chronological order
        all_events.sort(key=lambda e: e.get("timestamp_utc", ""))
        
        # Process events and write to frontend feed
        tick_callsite_written = 0
        with open(FRONTEND_FEED_FILE, 'a', encoding='utf-8') as f:
            for event in all_events:
                feed_event = self._process_event(event)
                if feed_event:
                    if feed_event.get("event_type") == "ENGINE_TICK_CALLSITE":
                        tick_callsite_written += 1
                    f.write(json.dumps(feed_event) + '\n')
                    processed_count += 1
        
        # Diagnostic: Log when ENGINE_TICK_CALLSITE events are written
        if tick_callsite_written > 0:
            logger.info(f"EventFeedGenerator: Wrote {tick_callsite_written} ENGINE_TICK_CALLSITE event(s) to feed")
        
        if processed_count > 0:
            logger.debug(f"Processed {processed_count} new events")
        
        # Diagnostic: Check if tick/heartbeat events are in the raw logs
        tick_or_alive_in_raw = False
        for event in all_events:
            event_type = self._extract_event_type(event)
            if event_type in ("ENGINE_TICK_CALLSITE", "ENGINE_ALIVE"):
                tick_or_alive_in_raw = True
                break
        
        if tick_or_alive_in_raw:
            logger.debug("ENGINE_TICK_CALLSITE or ENGINE_ALIVE events found in raw robot logs")
        else:
            # Only warn if we processed events but no liveness signal
            if processed_count > 0:
                logger.warning(
                    f"Processed {processed_count} events but no ENGINE_TICK_CALLSITE or ENGINE_ALIVE found. "
                    f"This may indicate the robot is not emitting liveness events."
                )
        
        return processed_count
    
    def get_current_run_id(self) -> Optional[str]:
        """Get the most recent run_id from processed events."""
        if not self._event_seq_by_run_id:
            return None
        # Return run_id with highest event_seq (most recent)
        return max(self._event_seq_by_run_id.items(), key=lambda x: x[1])[0]
    
    def get_event_seq(self, run_id: str) -> int:
        """Get current event_seq for a run_id."""
        return self._event_seq_by_run_id.get(run_id, 0)
