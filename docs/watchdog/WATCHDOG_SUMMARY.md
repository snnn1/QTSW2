# Watchdog System Summary

## Overview

The Watchdog is a **standalone monitoring and alerting system** for the trading execution pipeline. It operates independently from the Dashboard and Master Matrix, providing real-time health monitoring, risk gate validation, and execution journaling.

**Observational Contract**: The Watchdog is a pure observation and reporting system. It does not influence execution decisions or halt trading. All execution halting must occur via explicit risk gates implemented in the Robot execution engine. The Watchdog monitors, evaluates, and reports—it does not control.

## Architecture

### Separation
- **Backend**: FastAPI server on port **8002** (`modules/watchdog/backend/main.py`)
- **Frontend**: React app on port **5175** (`modules/watchdog/frontend/`)
- **No dependencies** on Dashboard (8001) or Matrix (8000)
- **Independent lifecycle** - can run even if other services are down

### Core Components

1. **WatchdogAggregator** (`modules/watchdog/aggregator.py`)
   - Main service coordinator
   - Manages event feed, event processing, and state management
   - Provides computed status endpoints

2. **EventFeedGenerator** (`modules/watchdog/event_feed.py`)
   - Reads from `logs/robot/frontend_feed.jsonl`
   - Generates filtered events for frontend consumption
   - Maintains cursor position for incremental reads

3. **EventProcessor** (`modules/watchdog/event_processor.py`)
   - Processes events and updates state manager
   - Handles event type routing (ENGINE_START, STREAM_STATE_TRANSITION, etc.)
   - Canonicalizes instruments and stream IDs

4. **WatchdogStateManager** (`modules/watchdog/state_manager.py`)
   - Maintains in-memory state for derived fields
   - Tracks stream states, intent exposures, risk gates
   - Computes health metrics (engine_alive, stuck_streams, etc.)

## What It Monitors

### 1. Engine Health
- **Engine Alive**: Tracks `ENGINE_TICK_HEARTBEAT` events
  - Threshold: 120 seconds without heartbeat = engine dead
  - Detects stalls and recovery
- **Engine State**: Start, stop, stall detection/recovery

### 2. Stream States
- Tracks state machine transitions for each trading stream
- States: `PRE_HYDRATION`, `ARMED`, `RANGE_BUILDING`, `RANGE_LOCKED`, `DONE`, `COMMITTED`, `NO_TRADE`, `INVALIDATED`
- Detects **stuck streams**:
  - PRE_HYDRATION: > 30 minutes
  - ARMED: > 2 hours (only during market hours)
  - Other states: > 5 minutes

### 3. Risk Gates
Monitors execution gates that must pass before trading:
- **Recovery State**: Must be `CONNECTED_OK` or `RECOVERY_COMPLETE`
- **Kill Switch**: Must be inactive
- **Stream Armed Status**: Tracks which streams are in ARMED state
- **Session Slot Time Validation**: Validates slot times against timetable
- **Market Status**: Checks if market is open

### 4. Unprotected Positions
- Tracks intents with open exposure but no protective orders
- Timeout: 10 seconds after exposure registration
- Flags positions that need protective orders

### 5. Data Health
- **Data Stall Detection**: Tracks last bar time per instrument
  - Threshold: 60 seconds without new bars (configurable)
- **Bar Time Tracking**: Per-instrument last bar timestamps from heartbeats

### 6. Connection Status
- Monitors connection events: `CONNECTION_LOST`, `CONNECTION_RECOVERED`
- Tracks recovery state transitions

### 7. Identity Invariants (Phase 3.1)
- Validates instrument/stream identity consistency
- Tracks violations and pass/fail status

### 8. Execution Journaling
- Records execution journal entries per intent
- Tracks stream state history
- Provides historical summaries

## API Endpoints

### REST API (`/api/watchdog/*`)
- `GET /status` - Current watchdog status (engine_alive, stuck_streams, etc.)
- `GET /events` - Incremental event feed (with cursor support)
- `GET /risk-gates` - Current risk gate status
- `GET /unprotected-positions` - Positions needing protection
- `GET /stream-states` - All stream states
- `GET /active-intents` - Active trading intents
- `GET /stream-pnl` - Realized P&L by stream
- `GET /journal/execution` - Historical execution journals
- `GET /journal/streams` - Historical stream states
- `GET /journal/summary` - Trading day summaries

### WebSocket (`/ws/events`)
- Real-time event streaming
- Sends snapshot of last 100 events on connect (best-effort)
- Streams new events as they occur
- Supports filtering by `run_id`
- Heartbeat messages during quiet periods
- See "WebSocket Semantics" section below for detailed behavior

### Health Endpoint
- `GET /ws-health` - WebSocket connection health and metrics

## Current Issues & Potential Errors

### 1. WebSocket Connection Failure (CURRENT ISSUE)
**Status**: ⚠️ **ACTIVE PROBLEM**

**Symptoms**:
- Frontend cannot connect to `ws://localhost:8002/ws/events`
- Error code 1006 (abnormal closure)
- Connection closes immediately after upgrade attempt

**Root Cause Analysis**:
- WebSocket routes ARE registered (`/ws/events`, `/ws/events/{run_id}`)
- Route handler exists and has logging
- **Likely causes**:
  1. Middleware interference (GZipMiddleware might affect WebSocket upgrades)
  2. CORS configuration issue (though `allow_origins=["*"]` should work)
  3. Route handler not being invoked (check backend logs for "Route handler invoked")
  4. Exception during `websocket.accept()` call

**Diagnostic Steps**:
- Check backend logs for:
  - "Registered WebSocket routes" message at startup
  - "Route handler invoked" message when frontend connects
  - Any exception during connection acceptance

**Potential Fixes**:
- Verify middleware order (CORS before GZip)
- Add explicit WebSocket CORS handling
- Check if GZipMiddleware needs to exclude WebSocket paths
- Verify frontend is connecting to correct URL (`ws://localhost:8002/ws/events`)

### 2. JSON Parsing Errors
**Status**: ✅ **FIXED** (but may recur)

**Symptoms**:
- "Failed to parse JSON line" errors in logs
- UTF-8 BOM issues
- Malformed JSON lines

**Fix Applied**:
- Changed file reading to `utf-8-sig` encoding
- Silent skip of malformed JSON lines (logged at debug level)

**Potential Recurrence**:
- If robot logs contain invalid JSON, errors will be silently skipped
- Monitor debug logs for parse error counts

### 3. Aggregator Initialization Failure
**Status**: ⚠️ **POTENTIAL ERROR**

**Symptoms**:
- Backend starts but aggregator fails to initialize
- HTTP endpoints return 503 "Watchdog aggregator not initialized"

**Causes**:
- Import errors (circular dependencies)
- File system issues (missing directories)
- Configuration errors

**Error Handling**:
- Backend continues running even if aggregator fails
- All endpoints check for aggregator before processing
- Returns 503 with clear error message

### 4. Event Feed File Missing
**Status**: ⚠️ **EXPECTED BEHAVIOR**

**Symptoms**:
- No events returned
- Empty WebSocket snapshots

**Cause**:
- `logs/robot/frontend_feed.jsonl` doesn't exist (normal on first run)
- Robot hasn't started generating events yet

**Behavior**:
- Returns empty arrays (not an error)
- WebSocket sends empty snapshot
- System continues monitoring

### 5. Stale Stream Cleanup
**Status**: ✅ **WORKING**

**Behavior**:
- Automatically removes streams from previous trading dates
- Removes streams stuck in PRE_HYDRATION > 2 hours
- Logs cleanup actions

### 6. Frontend Loading State Issues
**Status**: ✅ **FIXED**

**Previous Issue**:
- "Loading watchdog data... Connecting to backend... won't stop"
- Infinite loading spinners

**Fix Applied**:
- All hooks now set `hasLoadedRef.current = true` in all cases (success, null, error)
- Removed aggressive client-side timeouts
- Increased Vite proxy timeout to 60 seconds

### 7. React Key Warnings
**Status**: ✅ **FIXED**

**Previous Issue**:
- "Encountered two children with the same key" warnings
- Duplicate stream IDs in `gates.stream_armed` array

**Fix Applied**:
- Client-side deduplication in `RiskGatesPanel.tsx`
- Uses `Map` to deduplicate before rendering

## WebSocket Semantics

### Snapshot Behavior
- **Best-effort**: Snapshot send is non-blocking and may fail silently
- **Non-critical**: Snapshot failure does not abort the connection
- **Typed messages**: Snapshot messages include:
  - `type`: "snapshot_chunk" or "snapshot_done"
  - `server_time_utc`: ISO timestamp
  - `snapshot_size`: Number of events in snapshot
  - `cursor`: Cursor position if available
- **Streaming independence**: Streaming continues even if snapshot fails completely

### Streaming Behavior
- Polls `frontend_feed.jsonl` every second for new events
- Filters events by `run_id` if specified
- Sends events as they become available
- Continues indefinitely until connection closes

### Heartbeat Mechanism
- If no events sent for `WS_HEARTBEAT_INTERVAL_SECONDS` (30s), sends heartbeat message
- Heartbeat message: `{"type": "heartbeat", "server_time_utc": ISO_timestamp}`
- Keeps connection alive during quiet periods
- Allows frontend to detect stale connections
- Provides server time synchronization

### Backpressure Handling
- Tracks `send_fail_count` per connection
- If send fails or times out:
  - Increments failure count and dropped events counter
  - Logs `WS_ERROR phase=send`
  - Continues to next event
- If `send_fail_count` exceeds `WS_MAX_SEND_FAILURES` (10):
  - Closes connection with code 1008 (policy violation)
  - Logs `WS_CLOSED` with reason "max send failures exceeded"

### Lifecycle Logging
Standardized INFO-level logs for debugging:
- `WS_CONNECT_ATTEMPT`: Route handler invoked (proves route match)
  - Includes: client IP/port, path, run_id
- `WS_ACCEPTED`: Connection accepted successfully
- `WS_ERROR`: Exception occurred (with phase tag: accept/snapshot/stream/send)
- `WS_CLOSED`: Connection closed (includes code, reason, events sent, duration)

## Configuration

### Thresholds (`modules/watchdog/config.py`)
- `ENGINE_TICK_STALL_THRESHOLD_SECONDS`: 120 seconds
- `STUCK_STREAM_THRESHOLD_SECONDS`: 300 seconds (5 minutes)
- `UNPROTECTED_TIMEOUT_SECONDS`: 10 seconds
- `DATA_STALL_THRESHOLD_SECONDS`: 60 seconds

### WebSocket Configuration
- `WS_SEND_TIMEOUT_SECONDS`: 5 seconds - Timeout for send operations
- `WS_HEARTBEAT_INTERVAL_SECONDS`: 30 seconds - Heartbeat interval
- `WS_MAX_SEND_FAILURES`: 10 - Max failures before closing connection
- `WS_MAX_BUFFER_SIZE`: 100 - Max events in buffer (if buffer used)

### File Paths
- `FRONTEND_FEED_FILE`: `logs/robot/frontend_feed.jsonl`
- `FRONTEND_CURSOR_FILE`: `data/frontend_cursor.json`
- `EXECUTION_JOURNALS_DIR`: `data/execution_journals/`
- `EXECUTION_SUMMARIES_DIR`: `data/execution_summaries/`
- `ROBOT_JOURNAL_DIR`: `logs/robot/journal/`

## Startup Sequence

1. Backend starts (`modules/watchdog/backend/main.py`)
2. Lifespan handler initializes aggregator
3. Aggregator starts event processing loop
4. Routers registered (watchdog + websocket)
5. Frontend connects via WebSocket
6. Backend sends snapshot of recent events
7. Backend streams new events as they occur

## Monitoring Checklist

When watchdog is running, verify:
- ✅ Backend logs show "Aggregator started successfully"
- ✅ WebSocket routes registered: `['/ws/events', '/ws/events/{run_id}']`
- ✅ Frontend connects to WebSocket (check browser console)
- ✅ `/api/watchdog/status` returns data (not 503)
- ✅ Events are being processed (check backend logs)
- ✅ No infinite loading spinners in frontend
- ✅ Risk gates show current status
- ✅ Stream states update in real-time

## Known Limitations

1. **Observational Only**: Watchdog cannot stop execution (observational only)
   - Risk gates are evaluated by Robot, not Watchdog
   - Watchdog reports status but does not control execution
2. **No Persistence**: State is in-memory only (lost on restart)
3. **Single Instance**: Not designed for horizontal scaling
4. **File-Based Events**: Relies on JSONL file (not a message queue)
5. **No Authentication**: All endpoints are open (development mode)
6. **WebSocket Connection**: May fail if route handler not invoked (check logs for `WS_CONNECT_ATTEMPT`)

## Validation Checklist

Manual testing steps to verify WebSocket improvements:

1. **Health Endpoint Test**:
   - Open `http://localhost:8002/api/watchdog/ws-health` in browser
   - Verify `ws_enabled: true` and `active_connections_total: 0`
   - Connect frontend to WebSocket
   - Refresh health endpoint - verify `active_connections_total` increases
   - Disconnect frontend - verify count decreases

2. **Log Order Verification**:
   - Connect via browser to `ws://localhost:8002/ws/events`
   - Check backend logs for correct order:
     - `WS_CONNECT_ATTEMPT` (first, proves route match)
     - `WS_ACCEPTED` (after accept)
     - `WS_CLOSED` (on disconnect)
   - If `WS_CONNECT_ATTEMPT` is missing, routing is wrong

3. **Snapshot Failure Test**:
   - Temporarily rename `logs/robot/frontend_feed.jsonl` to simulate missing file
   - Connect to WebSocket
   - Verify connection still succeeds (snapshot fails but streaming continues)
   - Check logs for `WS_ERROR phase=snapshot`
   - Restore file and verify events stream normally

4. **Backpressure Test**:
   - Simulate slow consumer (throttle network or pause frontend processing)
   - Generate many events rapidly
   - Verify connection closes after `WS_MAX_SEND_FAILURES` failures
   - Check logs for `WS_CLOSED` with reason "max send failures exceeded"

5. **Heartbeat Test**:
   - Connect to WebSocket
   - Wait 30+ seconds without events
   - Verify heartbeat messages received: `{"type": "heartbeat", "server_time_utc": ...}`
