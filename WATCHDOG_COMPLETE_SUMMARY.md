# Watchdog System Complete Summary

## Overview

The **Watchdog** is a real-time monitoring and alerting system for the QTSW2 trading robot. It monitors engine health, stream states, data flow, execution safety, and provides a web-based UI for real-time visibility into the trading system's operation.

**Purpose**: Provide continuous monitoring, alerting, and observability for the trading robot to ensure safe and reliable operation.

---

## Architecture

### High-Level Flow

```
Robot Logs (JSONL)
    ↓
EventFeedGenerator (filters, rate-limits, transforms)
    ↓
frontend_feed.jsonl (processed events)
    ↓
EventProcessor (updates state)
    ↓
WatchdogStateManager (in-memory state)
    ↓
WatchdogAggregator (orchestrates, provides API)
    ↓
FastAPI Backend (REST + WebSocket)
    ↓
React Frontend (real-time UI)
```

### Components

1. **EventFeedGenerator** (`event_feed.py`)
   - Reads raw robot log files (`robot_*.jsonl`)
   - Filters to live-critical events (`LIVE_CRITICAL_EVENT_TYPES`)
   - Rate-limits frequent events (e.g., `ENGINE_TICK_CALLSITE` every 5s)
   - Adds `event_seq`, `timestamp_chicago`
   - Writes to `frontend_feed.jsonl`
   - Auto-rotates feed file when > 100 MB

2. **EventProcessor** (`event_processor.py`)
   - Reads events from `frontend_feed.jsonl` incrementally (cursor-based)
   - Processes each event type and updates state manager
   - Handles deduplication (by `event_seq` per `run_id`)
   - Canonicalizes instruments/streams (MES → ES)

3. **WatchdogStateManager** (`state_manager.py`)
   - Maintains in-memory state:
     - Engine liveness (`_last_engine_tick_utc`)
     - Stream states (`_stream_states`)
     - Intent exposures (`_intent_exposures`)
     - Data stall tracking (`_last_bar_utc_by_instrument`)
     - Timetable state (`_trading_date`, `_enabled_streams`)
     - Identity invariants status
   - Computes derived fields:
     - `engine_activity_state` (ALIVE/STALLED/IDLE)
     - `data_stall_detected` (per instrument)
     - `stuck_streams`
     - `unprotected_positions`

4. **TimetablePoller** (`timetable_poller.py`)
   - Polls `timetable_current.json` every 60 seconds
   - Extracts `trading_date`, `enabled_streams`, stream metadata
   - Fail-open mode (continues if timetable unavailable)
   - Provides authoritative trading date

5. **WatchdogAggregator** (`aggregator.py`)
   - Orchestrates all components
   - Runs background loops:
     - `_process_events_loop()` - processes new events
     - `_poll_timetable_loop()` - polls timetable
   - Provides API endpoints:
     - `/api/watchdog/status` - overall status
     - `/api/watchdog/streams` - stream states
     - `/api/watchdog/events` - event feed
     - `/api/watchdog/risk-gates` - risk gate status
     - `/api/watchdog/unprotected` - unprotected positions
   - WebSocket support for real-time events

6. **Backend** (`backend/main.py`, `backend/routers/`)
   - FastAPI application
   - REST API endpoints
   - WebSocket endpoint (`/ws`)
   - CORS configuration
   - Error handling

7. **Frontend** (`frontend/`)
   - React + TypeScript
   - Real-time WebSocket updates
   - Stream status table
   - Event feed viewer
   - Status badges (engine, data, broker, market, identity)

---

## Key Features

### 1. Engine Liveness Monitoring

**Primary Indicator**: `ENGINE_TICK_CALLSITE` events
- Fires every `Tick()` call (rate-limited in feed to every 5 seconds)
- More reliable than `ENGINE_HEARTBEAT` (deprecated)
- Threshold: 15 seconds (3x rate limit)

**Status Values**:
- **ENGINE ALIVE**: Ticks arriving within threshold
- **ENGINE STALLED**: No ticks for > 15 seconds
- **ENGINE IDLE**: Market closed or no streams expecting bars
- **FAIL-CLOSED**: Recovery failed, engine stopped
- **RECOVERY IN PROGRESS**: Recovering from disconnect

**Grace Period**: 5 minutes after engine start before declaring stall (allows startup time)

### 2. Data Flow Monitoring

**Tracks**: Last bar arrival time per instrument (`ONBARUPDATE_CALLED` events)

**Status Values**:
- **DATA FLOWING**: Bars arriving within threshold (90 seconds)
- **DATA STALLED**: No bars for > 90 seconds (market open)
- **DATA SILENT (OK)**: No bars but market closed (acceptable)
- **DATA UNKNOWN**: No bars tracked yet OR bars arriving but streams not in bar-dependent states

**Logic**:
- Only tracks instruments where `bars_expected() == True`
- `bars_expected()` requires:
  - Market open
  - At least one stream in bar-dependent state (PRE_HYDRATION, ARMED, RANGE_BUILDING, RANGE_LOCKED)
- Shows "FLOWING" if `worst_last_bar_age_seconds < 120` even if `data_stall_detected` is empty

### 3. Stream State Tracking

**Tracks**: State machine transitions for each stream

**States**:
- `PRE_HYDRATION` - Waiting for historical data
- `ARMED` - Ready, waiting for range window
- `RANGE_BUILDING` - Building range from live bars
- `RANGE_LOCKED` - Range locked, ready to trade
- `DONE` - Stream completed
- `COMMITTED` - Stream committed (no trade)
- `NO_TRADE` - Market closed, no trade
- `INVALIDATED` - Range invalidated

**Features**:
- Tracks state entry time
- Detects stuck streams (> 5 minutes in same state, market-aware)
- Shows time in state
- Tracks P&L, range, commit status

**Stream Table**:
- Shows all enabled streams from timetable
- Merges timetable metadata (instrument, session, slot_time) with watchdog state
- Shows defaults for streams without state transitions yet

### 4. Execution Safety Monitoring

**Unprotected Positions**:
- Tracks intents with exposure but no protective orders
- Timeout: 10 seconds (should have protective orders within 10s of exposure)
- Shows intent details, exposure time, protective order status

**Risk Gates**:
- Tracks execution blocking/allowing events
- Shows gate status (blocked/allowed)
- Tracks event counts (last hour)

**Protective Orders**:
- Tracks protective order submission/failure events
- Flags intents with failed protective orders

### 5. Identity Invariants Checking

**Checks** (every 60 seconds):
1. Stream IDs are canonical (no execution instrument in stream ID)
2. `Stream.Instrument == Stream.CanonicalInstrument`
3. `ExecutionInstrument` is present

**Status Values**:
- **IDENTITY UNKNOWN**: No check performed yet (startup)
- **IDENTITY OK**: All checks passed
- **IDENTITY VIOLATION**: Violations detected (shows details in tooltip)

### 6. Market Status

**Tracks**: Market open/closed status (Chicago timezone)

**Uses**: `market_session.py` to determine market hours

**Status Values**:
- **MARKET OPEN**: Market is open
- **MARKET CLOSED**: Market is closed
- **MARKET UNKNOWN**: Market status not determined yet

### 7. Broker Connection Status

**Tracks**: Connection events (`CONNECTION_LOST`, `CONNECTION_RECOVERED`)

**Status Values**:
- **BROKER CONNECTED**: Connected
- **BROKER DISCONNECTED**: Connection lost
- **BROKER UNKNOWN**: No connection events received

### 8. Timetable Validation

**Tracks**: Whether `timetable_current.json` is valid and loaded

**Status Values**:
- **Timetable Validated✓**: Timetable loaded successfully
- **Timetable Validated❌**: Timetable missing or invalid

**Note**: Timetable validation is NOT required for streams to appear, only for trades to execute.

---

## Status Indicators

### Header Badges

1. **ENGINE ALIVE/STALLED/IDLE**: Engine liveness
2. **BROKER CONNECTED/DISCONNECTED**: Broker connection
3. **DATA FLOWING/STALLED/SILENT/UNKNOWN**: Data flow
4. **MARKET OPEN/CLOSED**: Market status
5. **IDENTITY OK/VIOLATION/UNKNOWN**: Identity invariants

### Stream Table Columns

- **Stream**: Stream ID
- **Instr**: Instrument (canonical)
- **Session**: S1 or S2
- **State**: Current state machine state
- **Time in State**: Duration in current state
- **Slot**: Slot time (Chicago)
- **Range**: Range values (if locked)
- **PnL**: Realized P&L
- **Commit**: Commit status
- **Issues**: Stuck stream warnings

---

## Configuration

### Thresholds (`config.py`)

```python
ENGINE_TICK_STALL_THRESHOLD_SECONDS = 15  # Engine stall detection
DATA_STALL_THRESHOLD_SECONDS = 90          # Data stall detection
STUCK_STREAM_THRESHOLD_SECONDS = 300      # Stuck stream detection (5 min)
UNPROTECTED_TIMEOUT_SECONDS = 10          # Unprotected position timeout
```

### Update Frequencies

```python
ENGINE_ALIVE_UPDATE_FREQUENCY = 5         # Status update frequency
STREAM_STUCK_UPDATE_FREQUENCY = 10
RISK_GATE_UPDATE_FREQUENCY = 5
WATCHDOG_STATUS_UPDATE_FREQUENCY = 5
UNPROTECTED_POSITION_UPDATE_FREQUENCY = 2
```

### Rate Limiting

- `ENGINE_TICK_CALLSITE`: Rate-limited to every 5 seconds in feed
- `ONBARUPDATE_CALLED`: Rate-limited to every 60 seconds in feed

### File Paths

```python
ROBOT_LOGS_DIR = QTSW2_ROOT / "logs" / "robot"
FRONTEND_FEED_FILE = ROBOT_LOGS_DIR / "frontend_feed.jsonl"
FRONTEND_CURSOR_FILE = QTSW2_ROOT / "data" / "frontend_cursor.json"
TIMETABLE_FILE = QTSW2_ROOT / "data" / "timetable_current.json"
```

---

## Common Errors and Issues

### 1. "ENGINE STALLED" False Positives

**Symptoms**: Engine shows as stalled but robot is running

**Causes**:
- `ENGINE_TICK_CALLSITE` events not being emitted by robot
- Log file rotation not detected (fixed: now detects rotation)
- Cursor position incorrect (fixed: reads from end of file for liveness)

**Solutions**:
- Check robot logs for `ENGINE_TICK_CALLSITE` events
- Restart watchdog backend
- Check log file rotation handling

### 2. "DATA UNKNOWN" When Bars Are Arriving

**Symptoms**: Shows "DATA UNKNOWN" but `ONBARUPDATE_CALLED` events are present

**Causes**:
- Streams in PRE_HYDRATION without instrument set
- `data_stall_detected` only tracks instruments with `bars_expected() == True`
- Instrument mismatch between events and streams

**Solutions**:
- Fixed: Now checks `worst_last_bar_age_seconds` to show "FLOWING" if bars are recent
- Fixed: Computes `worst_last_bar_age_seconds` for ALL instruments, not just those with bars expected

### 3. Streams Not Showing

**Symptoms**: Streams don't appear in stream table

**Causes**:
- Trading date mismatch (old streams vs current timetable)
- Timetable not loaded
- Streams filtered out by date/enabled status

**Solutions**:
- Fixed: Stream table now shows ALL enabled streams from timetable
- Merges timetable metadata with watchdog state
- Shows defaults for streams without state transitions

### 4. "Timetable Validated❌"

**Symptoms**: Timetable shows as invalid

**Causes**:
- `timetable_current.json` missing or invalid JSON
- Parse errors

**Solutions**:
- Check `timetable_current.json` exists and is valid JSON
- Check watchdog logs for parse errors
- Note: Timetable validation is NOT required for streams to appear

### 5. "IDENTITY UNKNOWN"

**Symptoms**: Identity status shows as unknown

**Causes**:
- No `IDENTITY_INVARIANTS_STATUS` event received yet (normal at startup)
- Robot not running
- Event not in feed

**Solutions**:
- Normal at startup - wait for first check (every 60 seconds)
- Check robot is running
- Check feed for `IDENTITY_INVARIANTS_STATUS` events

### 6. Log File Rotation Issues

**Symptoms**: Events not being read after log rotation

**Causes**:
- `_read_log_file_incremental()` didn't detect file rotation
- Cursor position incorrect after rotation

**Solutions**:
- Fixed: Now detects rotation by comparing file size to last position
- Resets cursor to 0 if rotation detected

### 7. Trading Date Mismatch

**Symptoms**: Streams filtered out due to date mismatch

**Causes**:
- Old streams from previous trading date
- Timetable trading date changed
- Watchdog state not updated

**Solutions**:
- Fixed: All event processing now uses timetable's trading date (authoritative)
- Events with different trading dates are ignored
- Stream table shows streams for current trading date only

### 8. WebSocket Connection Issues

**Symptoms**: Frontend not receiving real-time updates

**Causes**:
- WebSocket connection dropped
- Aggregator not initialized
- Network issues

**Solutions**:
- Check WebSocket health endpoint: `/api/watchdog/ws-health`
- Check browser console for WebSocket errors
- Restart watchdog backend
- Check network connectivity

### 9. Cursor File Corruption

**Symptoms**: Events not being processed, duplicate events

**Causes**:
- `frontend_cursor.json` corrupted
- Cursor position incorrect

**Solutions**:
- Delete `data/frontend_cursor.json` (will rebuild from feed)
- Restart watchdog backend

### 10. Feed File Too Large

**Symptoms**: Slow processing, disk space issues

**Causes**:
- `frontend_feed.jsonl` growing unbounded

**Solutions**:
- Fixed: Auto-rotates when > 100 MB
- Old feed files archived to `logs/robot/frontend_feed_*.jsonl`

---

## Troubleshooting Guide

### Check Engine Liveness

1. Check `ENGINE_TICK_CALLSITE` events in feed:
   ```bash
   grep "ENGINE_TICK_CALLSITE" logs/robot/frontend_feed.jsonl | tail -5
   ```

2. Check last engine tick age in status API:
   ```bash
   curl http://localhost:8000/api/watchdog/status | jq '.last_engine_tick_chicago'
   ```

3. Check watchdog logs for stall warnings:
   ```bash
   grep "ENGINE_STALL" logs/watchdog/*.log
   ```

### Check Data Flow

1. Check `ONBARUPDATE_CALLED` events:
   ```bash
   grep "ONBARUPDATE_CALLED" logs/robot/frontend_feed.jsonl | tail -5
   ```

2. Check `data_stall_detected` in status:
   ```bash
   curl http://localhost:8000/api/watchdog/status | jq '.data_stall_detected'
   ```

3. Check `worst_last_bar_age_seconds`:
   ```bash
   curl http://localhost:8000/api/watchdog/status | jq '.worst_last_bar_age_seconds'
   ```

### Check Stream States

1. Check stream states API:
   ```bash
   curl http://localhost:8000/api/watchdog/streams | jq '.streams[] | {stream, state, time_in_state}'
   ```

2. Check stuck streams:
   ```bash
   curl http://localhost:8000/api/watchdog/status | jq '.stuck_streams'
   ```

### Check Timetable

1. Check timetable file:
   ```bash
   cat data/timetable_current.json | jq '{trading_date, enabled_streams: .streams[] | select(.enabled) | .stream}'
   ```

2. Check timetable polling in logs:
   ```bash
   grep "TIMETABLE_POLL" logs/watchdog/*.log | tail -10
   ```

### Check Event Processing

1. Check cursor position:
   ```bash
   cat data/frontend_cursor.json | jq
   ```

2. Check feed file size:
   ```bash
   ls -lh logs/robot/frontend_feed.jsonl
   ```

3. Check recent events:
   ```bash
   tail -20 logs/robot/frontend_feed.jsonl | jq -r '.event_type'
   ```

### Restart Watchdog

If issues persist:

1. Stop watchdog backend
2. Delete cursor file (optional, will rebuild):
   ```bash
   rm data/frontend_cursor.json
   ```
3. Restart watchdog backend
4. Check logs for errors

---

## API Endpoints

### REST API

- `GET /api/watchdog/status` - Overall watchdog status
- `GET /api/watchdog/streams` - Stream states
- `GET /api/watchdog/events` - Event feed (since sequence)
- `GET /api/watchdog/risk-gates` - Risk gate status
- `GET /api/watchdog/unprotected` - Unprotected positions
- `GET /api/watchdog/ws-health` - WebSocket health

### WebSocket

- `WS /ws` - Real-time event stream

---

## File Structure

```
modules/watchdog/
├── aggregator.py          # Main orchestrator
├── state_manager.py       # State management
├── event_feed.py          # Feed generation
├── event_processor.py      # Event processing
├── timetable_poller.py    # Timetable polling
├── config.py              # Configuration
├── market_session.py      # Market hours logic
├── backend/
│   ├── main.py           # FastAPI app
│   └── routers/
│       ├── watchdog.py   # REST endpoints
│       └── websocket.py  # WebSocket endpoint
└── frontend/              # React UI

logs/robot/
├── robot_ENGINE.jsonl    # Raw robot logs
├── frontend_feed.jsonl   # Processed feed
└── frontend_feed_*.jsonl # Archived feeds

data/
├── frontend_cursor.json  # Cursor state
└── timetable_current.json # Timetable
```

---

## Best Practices

1. **Monitor Logs**: Regularly check watchdog logs for warnings/errors
2. **Check Status**: Use status API to verify system health
3. **Restart After Changes**: Restart watchdog after configuration changes
4. **Clean Cursor**: Delete cursor file if events not processing correctly
5. **Monitor Feed Size**: Ensure feed rotation is working (< 100 MB)
6. **Check Timetable**: Verify timetable is valid and up-to-date
7. **WebSocket Health**: Monitor WebSocket connections for frontend updates

---

## Future Enhancements

- Historical event replay
- Alert notifications (email/SMS)
- Performance metrics dashboard
- Automated recovery actions
- Extended event filtering options
- Custom alert rules
