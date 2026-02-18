"""
Watchdog Aggregator Service

Main service that coordinates event feed generation, event processing, and state management.

INGESTION INVARIANTS (WATCHDOG_INGESTION_HARDENING):
- At most ONE tail read per ingestion cycle.
- No full-file reads under any circumstance.
- All ingestion lag measured using event timestamps (never processing time).
- Observational only — no trade gating logic.
"""
import json
import logging
import re
import asyncio
import time
from pathlib import Path
from typing import Dict, List, Optional, Tuple, Any
from datetime import datetime, timezone
from collections import defaultdict, deque
import pytz

from .event_feed import EventFeedGenerator
from .event_processor import EventProcessor
from .state_manager import WatchdogStateManager, CursorManager, _is_trading_date_within_max_age
from .timetable_poller import TimetablePoller, compute_timetable_trading_date
from .config import (
    EXECUTION_JOURNALS_DIR,
    FRONTEND_FEED_FILE,
    TAIL_LINE_COUNT,
    ENGINE_TICK_MAX_AGE_FOR_INIT_SECONDS,
    DEGRADATION_LOOP_THRESHOLD_MS,
    DEGRADATION_CONSECUTIVE_CYCLES,
    INGESTION_STATS_INTERVAL_SECONDS,
    EVENTS_CACHE_TTL_SECONDS,
)

logger = logging.getLogger(__name__)

CHICAGO_TZ = pytz.timezone("America/Chicago")

# Execution policy path for canonical->execution instrument lookup
_EXECUTION_POLICY_PATH = Path(__file__).parent.parent.parent / "configs" / "execution_policy.json"
_execution_instrument_cache: Optional[Dict[str, str]] = None


def _canonical_instrument_root(instrument: str) -> str:
    """Extract canonical root from instrument (e.g. YM2 -> YM, NQ 03-26 -> NQ)."""
    if not instrument:
        return ""
    m = re.match(r"^([A-Za-z]+)", str(instrument).strip().upper())
    return m.group(1) if m else str(instrument).strip().upper()


def _get_execution_instrument_for_canonical(canonical_instrument: str) -> Optional[str]:
    """
    Get the enabled execution instrument for a canonical market from execution policy.
    E.g., YM -> MYM, NQ -> MNQ, CL -> MCL. Returns None if not found.
    """
    global _execution_instrument_cache
    if not canonical_instrument:
        return None
    canonical = _canonical_instrument_root(canonical_instrument)
    if not canonical:
        return None
    try:
        if _execution_instrument_cache is None:
            if not _EXECUTION_POLICY_PATH.exists():
                return None
            with open(_EXECUTION_POLICY_PATH, "r", encoding="utf-8") as f:
                policy = json.load(f)
            markets = policy.get("canonical_markets", {})
            cache = {}
            for canon, cfg in markets.items():
                exec_instrs = cfg.get("execution_instruments", {})
                for exec_inst, inst_cfg in exec_instrs.items():
                    if inst_cfg.get("enabled"):
                        cache[canon.upper()] = exec_inst
                        break
            _execution_instrument_cache = cache
        return _execution_instrument_cache.get(canonical)
    except Exception as e:
        logger.debug(f"Failed to get execution instrument for {canonical}: {e}")
        return None


def _read_last_lines(path, n: int, encoding: str = "utf-8-sig") -> List[str]:
    """
    Read the last N lines from a file without loading the entire file.
    Uses reverse chunked read - O(tail size) not O(file size).
    """
    if not path.exists():
        return []
    lines = []
    chunk_size = 65536  # 64KB
    try:
        with open(path, "rb") as f:
            f.seek(0, 2)
            size = f.tell()
            pos = size
            buffer = b""
            while pos > 0 and len(lines) < n:
                read_size = min(chunk_size, pos)
                pos -= read_size
                f.seek(pos)
                chunk = f.read(read_size)
                buffer = chunk + buffer
                while b"\n" in buffer and len(lines) < n:
                    last_nl = buffer.rfind(b"\n")
                    if last_nl == -1:
                        break
                    line = buffer[last_nl + 1 :].decode(encoding, errors="replace").strip()
                    buffer = buffer[:last_nl]
                    if line:
                        lines.append(line)
            if buffer.strip() and len(lines) < n:
                line = buffer.decode(encoding, errors="replace").strip()
                if line:
                    lines.append(line)
        lines.reverse()
    except Exception as e:
        logger.warning(f"Error reading last {n} lines from {path}: {e}")
    return lines


def _read_last_lines_with_metrics(
    path, n: int, encoding: str = "utf-8-sig"
) -> Tuple[List[str], int, float]:
    """
    Read last N lines with telemetry. Returns (lines, bytes_read, duration_ms).
    Tracks actual bytes read from disk for ingestion metrics.
    """
    if not path.exists():
        return [], 0, 0.0
    lines = []
    total_bytes = 0
    chunk_size = 65536
    start = time.perf_counter()
    try:
        with open(path, "rb") as f:
            f.seek(0, 2)
            size = f.tell()
            pos = size
            buffer = b""
            while pos > 0 and len(lines) < n:
                read_size = min(chunk_size, pos)
                pos -= read_size
                f.seek(pos)
                chunk = f.read(read_size)
                total_bytes += len(chunk)
                buffer = chunk + buffer
                while b"\n" in buffer and len(lines) < n:
                    last_nl = buffer.rfind(b"\n")
                    if last_nl == -1:
                        break
                    line = buffer[last_nl + 1:].decode(encoding, errors="replace").strip()
                    buffer = buffer[:last_nl]
                    if line:
                        lines.append(line)
            if buffer.strip() and len(lines) < n:
                line = buffer.decode(encoding, errors="replace").strip()
                if line:
                    lines.append(line)
        lines.reverse()
    except Exception as e:
        logger.warning(f"Error reading last {n} lines from {path}: {e}")
    duration_ms = (time.perf_counter() - start) * 1000
    return lines, total_bytes, duration_ms


class IngestionTelemetry:
    """Per-cycle ingestion metrics for WATCHDOG_INGESTION_STATS."""

    def __init__(self):
        self._tail_read_durations: deque = deque(maxlen=100)
        self._loop_durations: deque = deque(maxlen=100)
        self._lines_parsed: deque = deque(maxlen=100)
        self._last_stats_emit_utc: Optional[datetime] = None
        self._degraded_consecutive_count: int = 0

    def record_cycle(
        self,
        tail_read_bytes: int,
        tail_read_duration_ms: float,
        lines_parsed: int,
        events_processed: int,
        parse_errors: int,
        newest_event_ts: Optional[datetime],
        loop_duration_ms: float,
    ) -> None:
        self._tail_read_durations.append(tail_read_duration_ms)
        self._loop_durations.append(loop_duration_ms)
        self._lines_parsed.append(lines_parsed)
        if loop_duration_ms > DEGRADATION_LOOP_THRESHOLD_MS:
            self._degraded_consecutive_count += 1
        else:
            self._degraded_consecutive_count = 0

    def should_emit_stats(self, now: datetime) -> bool:
        if self._last_stats_emit_utc is None:
            return True
        return (now - self._last_stats_emit_utc).total_seconds() >= INGESTION_STATS_INTERVAL_SECONDS

    def emit_stats(self, now: datetime, ingestion_lag_seconds: Optional[float]) -> Dict[str, Any]:
        self._last_stats_emit_utc = now
        stats = self.get_latest_stats(now, ingestion_lag_seconds)
        stats["event_type"] = "WATCHDOG_INGESTION_STATS"
        return stats

    def get_latest_stats(self, now: Optional[datetime] = None, ingestion_lag_seconds: Optional[float] = None) -> Dict[str, Any]:
        """Get latest ingestion metrics (for API / debugging)."""
        from datetime import timezone as tz
        now = now or datetime.now(tz.utc)
        dr = list(self._tail_read_durations)
        dl = list(self._loop_durations)
        lp = list(self._lines_parsed)
        avg_tail = sum(dr) / len(dr) if dr else 0
        p95_tail = sorted(dr)[int(len(dr) * 0.95)] if len(dr) >= 20 else (dr[-1] if dr else 0)
        avg_loop = sum(dl) / len(dl) if dl else 0
        total_time_sec = sum(dl) / 1000.0 if dl else 1.0
        total_lines = sum(lp) if lp else 0
        lines_per_sec = total_lines / total_time_sec if total_time_sec > 0 else 0
        return {
            "timestamp_utc": now.isoformat(),
            "avg_tail_read_ms": round(avg_tail, 2),
            "p95_tail_read_ms": round(p95_tail, 2),
            "avg_loop_duration_ms": round(avg_loop, 2),
            "ingestion_lag_seconds": round(ingestion_lag_seconds, 2) if ingestion_lag_seconds is not None else None,
            "lines_parsed_per_second": round(lines_per_sec, 1),
            "sample_count": len(dr),
            "degraded": self.is_degraded(),
        }

    def is_degraded(self) -> bool:
        return self._degraded_consecutive_count >= DEGRADATION_CONSECUTIVE_CYCLES

    def reset_degraded_count(self) -> None:
        self._degraded_consecutive_count = 0


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
        self._last_hydrate_utc: Optional[datetime] = None  # Throttle journal hydrate in get_active_intents
        self._last_slot_journal_hydrate_utc: Optional[datetime] = None  # Throttle slot journal hydrate in get_stream_states

        # INGESTION: Single tail read per cycle, telemetry, degradation mode
        self._ingestion_telemetry = IngestionTelemetry()
        self._ingestion_degraded: bool = False
        self._ingestion_degraded_entered_at: Optional[datetime] = None

        # INGESTION: /events cache - (events, cached_at_utc) to avoid linear disk scaling
        self._events_cache: Optional[Tuple[List[Dict], datetime]] = None
    
    async def start(self):
        """Start the aggregator service."""
        self._running = True
        logger.info("Watchdog aggregator started")
        
        # Load cursor state
        cursor = self._cursor_manager.load_cursor()
        logger.info(f"Loaded cursor state: {cursor}")
        
        # Initialize state from recent events on startup
        # INGESTION: Single tail read for all startup init (connection, tick, bars, identity)
        logger.info("Initializing watchdog state from recent events on startup")
        startup_snapshot: List[Dict] = []
        if FRONTEND_FEED_FILE.exists():
            lines, _, _ = _read_last_lines_with_metrics(FRONTEND_FEED_FILE, TAIL_LINE_COUNT)
            for line_str in lines:
                line = line_str.strip() if isinstance(line_str, str) else str(line_str)
                if not line:
                    continue
                try:
                    startup_snapshot.append(json.loads(line))
                except json.JSONDecodeError:
                    continue

        logger.info("Rebuilding connection status from startup snapshot")
        self._rebuild_connection_status_from_snapshot(startup_snapshot)

        logger.info("Hydrating intent exposures from execution journals")
        self._state_manager.hydrate_intent_exposures_from_journals(EXECUTION_JOURNALS_DIR)

        # Init engine tick from snapshot
        ticks = [e for e in reversed(startup_snapshot) if e.get("event_type") in ("ENGINE_TICK_CALLSITE", "ENGINE_ALIVE")]
        if ticks:
            most_recent_tick = ticks[0]
            ts_str = most_recent_tick.get("timestamp_utc")
            if ts_str:
                try:
                    tick_ts = datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
                    if tick_ts.tzinfo is None:
                        tick_ts = tick_ts.replace(tzinfo=timezone.utc)
                    age_sec = (datetime.now(timezone.utc) - tick_ts).total_seconds()
                    if age_sec <= ENGINE_TICK_MAX_AGE_FOR_INIT_SECONDS:
                        self._event_processor.process_event(most_recent_tick)
                        logger.info(f"Initialized engine tick from snapshot: age={age_sec:.0f}s")
                    else:
                        logger.info(f"Skipped stale tick on init: age={age_sec:.0f}s > {ENGINE_TICK_MAX_AGE_FOR_INIT_SECONDS}s")
                except Exception as e:
                    logger.warning(f"Failed to parse tick on init: {e}")
        else:
            logger.info("No recent ticks found for initialization")

        # Init bar tracking from snapshot
        bar_types = ("BAR_RECEIVED_NO_STREAMS", "BAR_ACCEPTED", "ONBARUPDATE_CALLED")
        bars = [e for e in reversed(startup_snapshot) if e.get("event_type") in bar_types][:50]
        for bar_ev in bars:
            try:
                self._event_processor.process_event(bar_ev)
            except Exception as e:
                logger.debug(f"Failed to process bar event during startup init: {e}")
        if bars:
            logger.info(f"Initialized bar tracking from {len(bars)} bar events on startup")

        # Init identity from snapshot
        logger.info("Initializing identity status from startup snapshot")
        try:
            if startup_snapshot:
                latest_identity_event = None
                latest_timestamp = None
                for event in startup_snapshot:
                    if event.get("event_type") != "IDENTITY_INVARIANTS_STATUS":
                        continue
                    ts_str = event.get("timestamp_utc", "")
                    if not ts_str:
                        continue
                    try:
                        ts = datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
                        if ts.tzinfo is None:
                            ts = ts.replace(tzinfo=timezone.utc)
                        if latest_timestamp is None or ts > latest_timestamp:
                            latest_identity_event = event
                            latest_timestamp = ts
                    except Exception:
                        pass
                if latest_identity_event:
                    self._event_processor.process_event(latest_identity_event)
                    logger.info(f"Initialized identity status from snapshot at {latest_timestamp.isoformat()[:19] if latest_timestamp else 'unknown'}")
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
                        # Hydrate from journals so carry-over positions are visible
                        self._state_manager.hydrate_intent_exposures_from_journals(EXECUTION_JOURNALS_DIR)
                    
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
        """
        Process new events from frontend_feed.jsonl (synchronous, runs in thread pool).
        INGESTION INVARIANT: Exactly ONE tail read per cycle.
        """
        cycle_start = time.perf_counter()
        cycle_start_utc = datetime.now(timezone.utc)
        cursor_events: List[Dict] = []
        try:
            cursor = self._cursor_manager.load_cursor()

            # --- SINGLE TAIL READ PER CYCLE ---
            lines, tail_read_bytes, tail_read_duration_ms = _read_last_lines_with_metrics(
                FRONTEND_FEED_FILE, TAIL_LINE_COUNT
            )

            # Parse lines into events (single pass)
            parsed_events: List[Dict] = []
            parse_errors = 0
            newest_event_ts: Optional[datetime] = None
            for line_str in lines:
                line = line_str.strip() if isinstance(line_str, str) else str(line_str)
                if not line:
                    continue
                try:
                    ev = json.loads(line)
                    parsed_events.append(ev)
                    ts_str = ev.get("timestamp_utc") or ev.get("ts_utc")
                    if ts_str:
                        try:
                            ts = datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
                            if ts.tzinfo is None:
                                ts = ts.replace(tzinfo=timezone.utc)
                            if newest_event_ts is None or ts > newest_event_ts:
                                newest_event_ts = ts
                        except Exception:
                            pass
                except json.JSONDecodeError:
                    parse_errors += 1

            # Rebuilds from snapshot (no second read)
            if len(self._state_manager._stream_states) == 0:
                logger.info("State manager is empty - rebuilding stream states from snapshot")
                self._rebuild_stream_states_from_snapshot(parsed_events)
            
            # Check if connection status needs initialization - rebuild from recent events
            # This handles initialization or state loss scenarios
            # Note: connection_status defaults to "Unknown" until we receive a connection event
            # We rebuild if status is Unknown OR if we've never seen a connection event
            if (
                self._state_manager._connection_status == "Unknown"
                or self._state_manager._last_connection_event_utc is None
            ):
                logger.info("Connection status needs initialization - rebuilding from snapshot")
                self._rebuild_connection_status_from_snapshot(parsed_events)
            
            # Extract ticks from snapshot (no second read)
            ticks = [e for e in reversed(parsed_events) if e.get("event_type") in ("ENGINE_TICK_CALLSITE", "ENGINE_ALIVE")]
            if ticks:
                most_recent_tick = ticks[0]
                tick_ts_str = most_recent_tick.get("timestamp_utc")
                if tick_ts_str:
                    try:
                        tick_ts = datetime.fromisoformat(tick_ts_str.replace("Z", "+00:00"))
                        if tick_ts.tzinfo is None:
                            tick_ts = tick_ts.replace(tzinfo=timezone.utc)
                        tick_age_sec = (cycle_start_utc - tick_ts).total_seconds()
                        if tick_age_sec <= ENGINE_TICK_MAX_AGE_FOR_INIT_SECONDS:
                            current_tick_utc = self._state_manager._last_engine_tick_utc
                            if not current_tick_utc or tick_ts > current_tick_utc:
                                self._event_processor.process_event(most_recent_tick)
                                logger.info(f"✅ Updated liveness from snapshot: tick_age={tick_age_sec:.1f}s")
                    except Exception as e:
                        logger.warning(f"Failed to parse tick timestamp: {e}")

            # Extract bars from snapshot
            bar_types = ("BAR_RECEIVED_NO_STREAMS", "BAR_ACCEPTED", "ONBARUPDATE_CALLED")
            bars = [e for e in reversed(parsed_events) if e.get("event_type") in bar_types][:10]
            for bar_ev in bars:
                try:
                    self._event_processor.process_event(bar_ev)
                except Exception as e:
                    logger.warning(f"Failed to process bar event: {e}")

            # Degradation mode: skip full event processing if loop too slow
            degraded = self._ingestion_telemetry.is_degraded()
            if degraded and not self._ingestion_degraded:
                self._ingestion_degraded = True
                self._ingestion_degraded_entered_at = cycle_start_utc
                logger.warning("WATCHDOG_DEGRADED_MODE_ENTERED: loop_duration exceeded threshold for 10 consecutive cycles")
            elif not degraded and self._ingestion_degraded:
                self._ingestion_degraded = False
                logger.info("WATCHDOG_DEGRADED_MODE_EXITED: loop duration recovered")
                self._ingestion_telemetry.reset_degraded_count()

            cursor_events: List[Dict] = []
            if not degraded:
                is_bar = lambda e: e.get("event_type") in bar_types
                for ev in parsed_events:
                    run_id = ev.get("run_id")
                    event_seq = ev.get("event_seq", 0)
                    if not run_id:
                        continue
                    last_seq = cursor.get(run_id, 0)
                    # Include bars and new events only. Ticks are handled by snapshot path (lines 579-598)
                    # and must not be re-processed every cycle — that would refresh liveness from old ticks.
                    if is_bar(ev) or event_seq > last_seq:
                        cursor_events.append(ev)
                cursor_events.sort(key=lambda e: e.get("timestamp_utc", ""))

                for ev in cursor_events:
                    self._event_processor.process_event(ev)
                    self._add_to_ring_buffer_if_important(ev)

                if cursor_events:
                    run_ids_seen = {}
                    for ev in cursor_events:
                        rid, seq = ev.get("run_id"), ev.get("event_seq", 0)
                        if rid and (rid not in run_ids_seen or seq > run_ids_seen[rid]):
                            run_ids_seen[rid] = seq
                    for rid, seq in run_ids_seen.items():
                        cursor[rid] = seq
                    self._cursor_manager.save_cursor(cursor)

            loop_duration_ms = (time.perf_counter() - cycle_start) * 1000
            ingestion_lag_seconds = (cycle_start_utc - newest_event_ts).total_seconds() if newest_event_ts else None

            self._ingestion_telemetry.record_cycle(
                tail_read_bytes=tail_read_bytes,
                tail_read_duration_ms=tail_read_duration_ms,
                lines_parsed=len(parsed_events),
                events_processed=len(bars) + (0 if degraded else len(cursor_events)),
                parse_errors=parse_errors,
                newest_event_ts=newest_event_ts,
                loop_duration_ms=loop_duration_ms,
            )

            now_utc = datetime.now(timezone.utc)
            if self._ingestion_telemetry.should_emit_stats(now_utc):
                stats = self._ingestion_telemetry.emit_stats(now_utc, ingestion_lag_seconds)
                logger.info(f"WATCHDOG_INGESTION_STATS: {stats}")
        
        except Exception as e:
            logger.error(f"Error processing feed events: {e}", exc_info=True)
    
    def _read_recent_bar_events_from_end(self, max_events: int = 10) -> List[Dict]:
        """Read recent bar events from the end of frontend_feed.jsonl.
        Uses tail-only read (no full file load) for performance."""
        import json
        
        bar_events = []
        if not FRONTEND_FEED_FILE.exists():
            return bar_events
        
        try:
            lines = _read_last_lines(FRONTEND_FEED_FILE, 5000)
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
        """Read events from frontend_feed.jsonl since cursor position.
        Uses tail-only read (no full file load) for performance."""
        import json
        
        events = []
        
        if not FRONTEND_FEED_FILE.exists():
            return events
        
        try:
            MAX_LINES_TO_READ = 5000
            lines = _read_last_lines(FRONTEND_FEED_FILE, MAX_LINES_TO_READ)
            
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
                    last_seq = cursor.get(run_id, 0)
                    is_bar_event = event_type in ("BAR_RECEIVED_NO_STREAMS", "BAR_ACCEPTED", "ONBARUPDATE_CALLED")
                    is_tick_event = event_type == "ENGINE_TICK_CALLSITE"
                    should_include = (is_bar_event or is_tick_event) or (event_seq > last_seq)
                    if should_include:
                        events.append(event)
                except json.JSONDecodeError:
                    parse_errors += 1
                    continue
            if parse_errors > 0:
                logger.debug(f"Skipped {parse_errors} malformed JSON lines in feed file")
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
            
            # Tail-only read: avoid loading entire file
            recent_lines = _read_last_lines(FRONTEND_FEED_FILE, 5000)
            for line in recent_lines:
                line = line.strip() if isinstance(line, str) else str(line)
                if not line:
                    continue
                try:
                    event = json.loads(line)
                    event_type = event.get("event_type")
                    if event_type != "STREAM_STATE_TRANSITION":
                        continue
                    trading_date = event.get("trading_date")
                    stream = event.get("stream")
                    timestamp_str = event.get("timestamp_utc")
                    if not trading_date or not stream or not timestamp_str:
                        continue
                    try:
                        from datetime import datetime
                        timestamp = datetime.fromisoformat(timestamp_str.replace('Z', '+00:00'))
                    except Exception:
                        continue
                    key = (trading_date, stream)
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
            
            # Tail-only read: avoid loading entire file
            recent_lines = _read_last_lines(FRONTEND_FEED_FILE, 5000)
            for line in recent_lines:
                line = line.strip() if isinstance(line, str) else str(line)
                if not line:
                    continue
                try:
                    event = json.loads(line)
                    event_type = event.get("event_type")
                    if event_type not in ("CONNECTION_LOST", "CONNECTION_LOST_SUSTAINED",
                                         "CONNECTION_RECOVERED", "CONNECTION_RECOVERED_NOTIFICATION"):
                        continue
                    timestamp_str = event.get("timestamp_utc")
                    if not timestamp_str:
                        continue
                    try:
                        timestamp = datetime.fromisoformat(timestamp_str.replace('Z', '+00:00'))
                        if timestamp.tzinfo is None:
                            timestamp = timestamp.replace(tzinfo=timezone.utc)
                    except Exception:
                        continue
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
                logger.info("No connection events found in recent feed - keeping connection status Unknown")
                # Do NOT default to Connected - we have no evidence of connection.
                # Unknown is correct when no connection events have ever been received.
        
        except Exception as e:
            logger.error(f"Error rebuilding connection status from recent events: {e}", exc_info=True)

    def _rebuild_stream_states_from_snapshot(self, parsed_events: List[Dict]) -> None:
        """Rebuild stream states from in-memory snapshot (no file read).
        
        Processes STREAM_STATE_TRANSITION for state, and RANGE_LOCKED / RANGE_LOCK_SNAPSHOT /
        RANGE_LOCKED_RESTORED_* for full metadata (range, session, instrument, slot).
        Events are processed in timestamp order so metadata from restore events is applied.
        """
        rebuild_types = {
            "STREAM_STATE_TRANSITION",
            "RANGE_LOCKED",
            "RANGE_LOCK_SNAPSHOT",
            "RANGE_LOCKED_RESTORED_FROM_HYDRATION",
            "RANGE_LOCKED_RESTORED_FROM_RANGES",
        }
        to_process: List[Tuple[Dict, datetime]] = []
        for event in parsed_events:
            event_type = event.get("event_type")
            if event_type not in rebuild_types:
                continue
            timestamp_str = event.get("timestamp_utc")
            if not timestamp_str:
                continue
            trading_date = event.get("trading_date") or (event.get("data") or {}).get("trading_date")
            stream = event.get("stream") or (event.get("data") or {}).get("stream_id")
            if not trading_date or not stream:
                continue
            try:
                ts = datetime.fromisoformat(timestamp_str.replace("Z", "+00:00"))
                if ts.tzinfo is None:
                    ts = ts.replace(tzinfo=timezone.utc)
            except Exception:
                continue
            to_process.append((event, ts))
        to_process.sort(key=lambda x: x[1])
        for ev, _ in to_process:
            self._event_processor.process_event(ev)
        if to_process:
            logger.info(f"Rebuilt stream states from snapshot ({len(to_process)} events processed)")

    def _rebuild_connection_status_from_snapshot(self, parsed_events: List[Dict]) -> None:
        """Rebuild connection status from in-memory snapshot (no file read)."""
        conn_types = ("CONNECTION_LOST", "CONNECTION_LOST_SUSTAINED",
                     "CONNECTION_RECOVERED", "CONNECTION_RECOVERED_NOTIFICATION")
        latest_ev = None
        latest_ts = None
        for event in parsed_events:
            if event.get("event_type") not in conn_types:
                continue
            ts_str = event.get("timestamp_utc")
            if not ts_str:
                continue
            try:
                ts = datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
                if ts.tzinfo is None:
                    ts = ts.replace(tzinfo=timezone.utc)
            except Exception:
                continue
            if latest_ts is None or ts > latest_ts:
                latest_ev = event
                latest_ts = ts
        if latest_ev:
            self._event_processor.process_event(latest_ev)
            logger.info(f"Rebuilt connection status from snapshot: {latest_ev.get('event_type')}")
    
    # Event types excluded from Live Event Feed (too verbose, backend/diagnostic only)
    _LIVE_FEED_EXCLUDED_TYPES = frozenset({
        "ENGINE_TICK_CALLSITE",      # Backend liveness only, ~12/min
        "ENGINE_TICK_HEARTBEAT",     # Backend liveness
        "ENGINE_ALIVE",              # Backend liveness
        "ENGINE_TICK_EXECUTED",      # Diagnostic
        "ENGINE_TICK_BEFORE_LOCK",   # Diagnostic
        "ENGINE_TICK_LOCK_ACQUIRED", # Diagnostic
        "ENGINE_TICK_AFTER_LOCK",    # Diagnostic
        "ENGINE_BUILD_STAMP",        # Diagnostic
        "BAR_ACCEPTED",              # Bar heartbeat, low signal
        "BAR_RECEIVED_NO_STREAMS",   # Bar heartbeat, low signal
        "ONBARUPDATE_CALLED",        # Diagnostic, can flood
        "ONBARUPDATE_DIAGNOSTIC",    # Diagnostic
        "BAR_ROUTING_DIAGNOSTIC",    # Diagnostic
        "RANGE_LOCK_SNAPSHOT",       # Redundant with RANGE_LOCKED
    })

    def _filter_events_for_live_feed(self, events: List[Dict]) -> List[Dict]:
        """Filter events for Live Event Feed - exclude noisy/diagnostic types."""
        filtered = []
        for ev in events:
            event_type = ev.get("event_type", "")
            if event_type in self._LIVE_FEED_EXCLUDED_TYPES:
                continue
            # IDENTITY_INVARIANTS_STATUS: only include when violations detected
            if event_type == "IDENTITY_INVARIANTS_STATUS":
                data = ev.get("data") or {}
                if isinstance(data, dict):
                    violations = data.get("violations", [])
                    if not violations:
                        continue
                else:
                    continue
            filtered.append(ev)
        return filtered

    def get_events_since(self, run_id: str, since_seq: int) -> List[Dict]:
        """
        Get most recent events for Live Events feed.
        
        INGESTION: Cached for EVENTS_CACHE_TTL_SECONDS. Multiple clients within TTL
        share one disk read to avoid linear I/O scaling.
        
        Filters out noisy event types (tick heartbeats, bar heartbeats, diagnostics)
        to reduce verbosity - same philosophy as WebSocket important_events buffer.
        """
        now = datetime.now(timezone.utc)
        if self._events_cache:
            cached_events, cached_at = self._events_cache
            age = (now - cached_at).total_seconds()
            if age < EVENTS_CACHE_TTL_SECONDS:
                all_events = cached_events
            else:
                all_events = self._read_events_tail(1000)
                self._events_cache = (all_events, now)
        else:
            all_events = self._read_events_tail(1000)
            self._events_cache = (all_events, now)

        # Filter out noisy event types for Live Event Feed
        filtered = self._filter_events_for_live_feed(all_events)
        filtered.sort(key=lambda e: e.get("timestamp_utc", "") or e.get("timestamp_chicago", ""))
        return filtered[-200:] if len(filtered) > 200 else filtered

    def _read_events_tail(self, n: int) -> List[Dict]:
        """Read last N lines from feed and parse to events. No cache."""
        events = []
        if not FRONTEND_FEED_FILE.exists():
            return events
        try:
            lines = _read_last_lines(FRONTEND_FEED_FILE, n)
            for line_str in lines:
                if not line_str.strip():
                    continue
                try:
                    events.append(json.loads(line_str))
                except json.JSONDecodeError:
                    continue
        except Exception as e:
            logger.error(f"Error reading feed file for events: {e}", exc_info=True)
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
    
    def get_ingestion_stats(self) -> Dict:
        """Get latest ingestion telemetry (tail read duration, loop duration, etc.)."""
        return self._ingestion_telemetry.get_latest_stats()

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
        """
        Get current engine run_id - the run_id from the most recent event in the feed.
        Previously returned run_id with highest event_seq (wrong: event_seq is per-run,
        so an old long-running session could win over a newer one, causing Live Events
        to show 30+ min stale data).
        """
        run_id = self._get_run_id_from_most_recent_feed_event()
        if not run_id:
            run_id = self._event_feed.get_current_run_id()
        if not run_id:
            cursor = self._cursor_manager.load_cursor()
            if cursor:
                run_id = max(cursor.items(), key=lambda x: x[1])[0] if cursor else None
        return run_id

    def _get_run_id_from_most_recent_feed_event(self) -> Optional[str]:
        """Get run_id from the last event in frontend_feed.jsonl (most recent by time)."""
        import json
        if not FRONTEND_FEED_FILE.exists():
            return None
        try:
            lines = _read_last_lines(FRONTEND_FEED_FILE, 1)
            if lines:
                event = json.loads(lines[-1])
                return event.get("run_id")
        except Exception as e:
            logger.debug(f"Could not get run_id from feed tail: {e}")
        return None
    
    def get_stream_states(self) -> Dict:
        """
        Get current stream states, merging timetable data with watchdog state data.
        
        Returns all enabled streams from timetable, with watchdog state data merged in.
        If timetable unavailable, falls back to showing only streams with watchdog state.
        """
        streams = []
        timetable_unavailable = False
        
        try:
            # Hydrate stream states from slot journals when event-derived state is missing (throttled 60s)
            # Also hydrate range data from ranges_{date}.jsonl for RANGE_LOCKED streams missing range_high/low
            now_utc = datetime.now(timezone.utc)
            if self._last_slot_journal_hydrate_utc is None or \
                    (now_utc - self._last_slot_journal_hydrate_utc).total_seconds() >= 60:
                self._state_manager.hydrate_stream_states_from_slot_journals()
                self._state_manager.hydrate_range_data_from_ranges_file()
                self._last_slot_journal_hydrate_utc = now_utc

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
                        
                        canonical = getattr(watchdog_info, 'instrument', None) or instrument
                        exec_instr = getattr(watchdog_info, 'execution_instrument', None) or _get_execution_instrument_for_canonical(canonical)
                        streams.append({
                            "trading_date": current_trading_date,
                            "stream": stream_id,
                            "instrument": canonical,  # Canonical instrument (DO NOT CHANGE)
                            "execution_instrument": exec_instr,  # Execution instrument (e.g., MYM, MNQ) from events/ranges or policy
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
                        exec_instr = _get_execution_instrument_for_canonical(instrument)
                        streams.append({
                            "trading_date": current_trading_date,
                            "stream": stream_id,
                            "instrument": instrument,  # Canonical instrument (DO NOT CHANGE)
                            "execution_instrument": exec_instr,  # From execution policy when not yet from events
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
                
                active_stream_keys = self._state_manager.get_active_intent_stream_keys()
                filtered_by_date = 0
                filtered_by_enabled = 0
                for (trading_date, stream), info in stream_states_dict.items():
                    # Include streams from current trading date, OR carry-over with active intents AND within ~24h
                    if trading_date != current_trading_date:
                        if (trading_date, stream) not in active_stream_keys:
                            filtered_by_date += 1
                            continue
                        if not _is_trading_date_within_max_age(trading_date, current_trading_date):
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
                    
                    canonical = getattr(info, 'instrument', '')
                    exec_instr = getattr(info, 'execution_instrument', None) or _get_execution_instrument_for_canonical(canonical)
                    streams.append({
                        "trading_date": trading_date,
                        "stream": stream,
                        "instrument": canonical,
                        "execution_instrument": exec_instr,
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

            # Add carry-over streams (from previous day with active intents) not already in streams
            # Only include streams within STREAM_MAX_AGE_DAYS (~24h) - no 10-day-old carry-over
            active_stream_keys = self._state_manager.get_active_intent_stream_keys()
            existing_keys = {(s.get("trading_date"), s.get("stream")) for s in streams}
            for (trading_date, stream) in active_stream_keys:
                if (trading_date, stream) in existing_keys:
                    continue
                if trading_date == current_trading_date:
                    continue  # Already handled by timetable loop
                if not _is_trading_date_within_max_age(trading_date, current_trading_date):
                    continue  # Skip streams older than ~24h
                info = stream_states_dict.get((trading_date, stream))
                if info:
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
                    canonical = getattr(info, 'instrument', '')
                    exec_instr = getattr(info, 'execution_instrument', None) or _get_execution_instrument_for_canonical(canonical)
                    streams.append({
                        "trading_date": trading_date,
                        "stream": stream,
                        "instrument": canonical,
                        "execution_instrument": exec_instr,
                        "session": getattr(info, 'session', None) or "",
                        "state": getattr(info, 'state', 'RANGE_LOCKED'),
                        "committed": False,
                        "commit_reason": None,
                        "slot_time_chicago": slot_time_chicago or None,
                        "slot_time_utc": getattr(info, 'slot_time_utc', None) or None,
                        "range_high": getattr(info, 'range_high', None),
                        "range_low": getattr(info, 'range_low', None),
                        "freeze_close": getattr(info, 'freeze_close', None),
                        "range_invalidated": getattr(info, 'range_invalidated', False),
                        "state_entry_time_utc": state_entry_time_utc.isoformat(),
                        "range_locked_time_utc": state_entry_time_utc.isoformat(),
                        "range_locked_time_chicago": state_entry_time_utc.astimezone(CHICAGO_TZ).isoformat()
                    })
                    logger.debug(f"get_stream_states: Added carry-over stream {stream} ({trading_date})")
            
        except Exception as e:
            logger.error(f"Error getting stream states: {e}", exc_info=True)
        
        # Sort streams by slot_time_chicago descending (latest first)
        def _slot_sort_key(s):
            st = s.get("slot_time_chicago") or ""
            if not st:
                return (0, 0)
            # Extract HH:MM from "HH:MM" or "YYYY-MM-DDTHH:MM:SS" format
            if "T" in st:
                try:
                    idx = st.index("T")
                    st = st[idx + 1 : idx + 6]  # "HH:MM"
                except Exception:
                    pass
            parts = st.split(":")
            h = int(parts[0]) if len(parts) > 0 and parts[0].isdigit() else 0
            m = int(parts[1]) if len(parts) > 1 and parts[1].isdigit() else 0
            return (h, m)
        streams.sort(key=_slot_sort_key, reverse=True)
        
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
        """Get current active intents. Merges journal truth (open journals) for reliability."""
        # Merge journal truth: hydrate from journals (throttled to every 60s)
        now = datetime.now(timezone.utc)
        if self._last_hydrate_utc is None or (now - self._last_hydrate_utc).total_seconds() >= 60:
            self._state_manager.hydrate_intent_exposures_from_journals(EXECUTION_JOURNALS_DIR)
            self._last_hydrate_utc = now
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
