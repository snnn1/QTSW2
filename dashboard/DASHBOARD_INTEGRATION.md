# How The Dashboard Works With The Pipeline

## Overview

The dashboard provides **real-time monitoring** of the pipeline through a **WebSocket-based event streaming system**. The pipeline writes structured events to JSONL files, and the dashboard reads and displays them in real-time.

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│              Pipeline System (Refactored)                    │
│                                                              │
│  pipeline_runner.py                                          │
│    └─> EventLogger.emit()                                   │
│         └─> Writes to:                                      │
│              automation/logs/events/                         │
│              pipeline_{run_id}.jsonl                         │
└────────────────────────┬────────────────────────────────────┘
                         │
                         │ (File System)
                         │
                         ▼
┌─────────────────────────────────────────────────────────────┐
│              Dashboard Backend (FastAPI)                     │
│                                                              │
│  WebSocket Endpoint: /ws/events/{run_id}                    │
│    └─> ConnectionManager                                     │
│         └─> _tail_file()                                     │
│              • Reads JSONL file                              │
│              • Broadcasts events via WebSocket               │
│              • Polls for new lines every 0.1s                │
└────────────────────────┬────────────────────────────────────┘
                         │
                         │ (WebSocket)
                         │
                         ▼
┌─────────────────────────────────────────────────────────────┐
│              Dashboard Frontend (React)                      │
│                                                              │
│  WebSocketManager                                            │
│    └─> Connects to: ws://localhost:8000/ws/events/{run_id} │
│         └─> Receives events                                 │
│              └─> usePipelineState hook                       │
│                   └─> Updates React state                    │
│                        └─> EventsLog component               │
│                             └─> Displays in UI               │
└─────────────────────────────────────────────────────────────┘
```

## Data Flow

### 1. Pipeline Writes Events

When the pipeline runs, the `EventLogger` service writes structured events to a JSONL file:

**File Location**: `automation/logs/events/pipeline_{run_id}.jsonl`

**Event Format** (one JSON object per line):
```json
{"run_id": "abc-123", "stage": "translator", "event": "start", "timestamp": "2025-04-12T02:00:00-05:00", "msg": "Starting data translator stage"}
{"run_id": "abc-123", "stage": "translator", "event": "metric", "timestamp": "2025-04-12T02:00:15-05:00", "data": {"raw_file_count": 6}}
{"run_id": "abc-123", "stage": "translator", "event": "success", "timestamp": "2025-04-12T02:05:30-05:00", "msg": "Translation completed successfully"}
```

**Event Types**:
- `start` - Stage started
- `metric` - Progress/metrics update
- `success` - Stage completed successfully
- `failure` - Stage failed
- `log` - General log message

**Stages**:
- `pipeline` - Overall pipeline events
- `translator` - Data translation stage
- `analyzer` - Breakout analysis stage
- `merger` - Data merger stage

### 2. Dashboard Backend Tails File

The dashboard backend has a WebSocket endpoint that **tails** (follows) the event log file:

**WebSocket Endpoint**: `/ws/events/{run_id}`

**How It Works**:
1. Client connects to WebSocket with a `run_id`
2. Backend constructs file path: `automation/logs/events/pipeline_{run_id}.jsonl`
3. Backend starts tailing the file:
   - Reads existing content first (catches up on missed events)
   - Polls for new lines every 0.1 seconds
   - Parses each line as JSON
   - Broadcasts events to all connected WebSocket clients
4. Continues tailing until:
   - Client disconnects
   - File is deleted
   - No active connections

**Key Features**:
- **Fault Tolerant**: Handles file not existing, temporary read errors
- **Efficient**: Only reads new lines (seeks to last position)
- **Real-time**: 0.1 second polling interval
- **Multi-client**: Broadcasts to all connected clients

### 3. Frontend Connects and Receives Events

The React frontend connects to the WebSocket when a pipeline run starts:

**Connection Flow**:
1. User clicks "Run Now" or pipeline starts automatically
2. Backend returns `run_id` from API
3. Frontend `WebSocketManager` connects to: `ws://localhost:8000/ws/events/{run_id}`
4. WebSocket receives events in real-time
5. Events are dispatched to React state via `usePipelineState` hook
6. `EventsLog` component displays events in UI

**WebSocketManager Features**:
- **Auto-reconnect**: Reconnects if connection drops (with exponential backoff)
- **Run ID tracking**: Only connects to one run at a time
- **Connection lifecycle**: Properly disconnects when pipeline completes

### 4. Events Displayed in UI

The `EventsLog` component displays events in real-time:

**Features**:
- **Auto-scroll**: Automatically scrolls to bottom when new events arrive
- **Filtering**: Can filter by stage (translator, analyzer, merger, etc.)
- **Verbose toggle**: Show/hide detailed log messages
- **Color coding**: Different colors for success/failure/log events
- **Timestamp display**: Shows formatted timestamps

**Event Display Format**:
```
[02:00:00] translator start Starting data translator stage
[02:00:15] translator metric Files found: 6
[02:05:30] translator success Translation completed successfully
```

## Integration Points

### 1. Event Log File Naming

**Pipeline** writes to: `automation/logs/events/pipeline_{run_id}.jsonl`

**Dashboard** expects: `automation/logs/events/pipeline_{run_id}.jsonl`

✅ **Compatible**: Same naming convention, same directory

### 2. Event Format

**Pipeline** emits events with structure:
```json
{
  "run_id": "uuid",
  "stage": "translator|analyzer|merger|pipeline",
  "event": "start|metric|success|failure|log",
  "timestamp": "ISO-8601 string",
  "msg": "Optional message",
  "data": { "Optional data object" }
}
```

**Dashboard** expects the same structure.

✅ **Compatible**: Identical format

### 3. Run ID Generation

**Pipeline** generates `run_id` using `uuid.uuid4()` in `pipeline_runner.py`

**Dashboard** can:
- Get `run_id` from API when starting pipeline manually
- Find latest `run_id` by scanning event log files
- Connect to specific `run_id` via WebSocket

✅ **Compatible**: UUID format works with both systems

## How To Use

### Starting Pipeline from Dashboard

1. **User clicks "Run Now"** in dashboard
2. **Backend API** (`/api/pipeline/start`) is called
3. **Backend**:
   - Generates `run_id`
   - Creates empty event log file: `pipeline_{run_id}.jsonl`
   - Starts pipeline subprocess with `run_id` in environment
4. **Frontend**:
   - Receives `run_id` from API response
   - Connects WebSocket to `/ws/events/{run_id}`
   - Starts displaying events in real-time

### Automatic Pipeline Runs

1. **Windows Task Scheduler** runs `python -m automation.pipeline_runner` every 15 minutes
2. **Pipeline** generates its own `run_id` and creates event log file
3. **Dashboard** can:
   - **Option A**: User manually connects to latest run (finds most recent event log)
   - **Option B**: Dashboard auto-connects to latest run on page load
   - **Option C**: Dashboard polls for new runs and auto-connects

### Viewing Historical Runs

The dashboard can:
- List all event log files in `automation/logs/events/`
- Allow user to select a `run_id` to view
- Connect WebSocket to that `run_id` to replay events
- Display events from the file (even if pipeline already completed)

## Key Components

### Backend (`dashboard/backend/routers/websocket.py`)

**`ConnectionManager`**:
- Manages WebSocket connections
- Tails event log files
- Broadcasts events to all clients

**`_tail_file()` method**:
- Reads existing content first
- Polls for new lines
- Handles errors gracefully
- Stops when no connections

### Frontend (`dashboard/frontend/src/`)

**`websocketManager.js`**:
- Singleton WebSocket manager
- Handles connection lifecycle
- Auto-reconnect logic
- Event callback system

**`usePipelineState.js`**:
- React hook for pipeline state
- Connects WebSocket when pipeline starts
- Dispatches events to reducer
- Updates UI state

**`EventsLog.jsx`**:
- Displays events in scrollable container
- Auto-scrolls to bottom
- Filtering and verbose toggle
- Color-coded event display

## Benefits of This Architecture

### 1. **Decoupled**
- Pipeline doesn't know about dashboard
- Dashboard doesn't know about pipeline internals
- Communication via file system (JSONL files)

### 2. **Real-time**
- Events appear immediately in dashboard
- No polling needed (WebSocket push)
- Low latency (0.1s polling interval)

### 3. **Fault Tolerant**
- If dashboard is down, pipeline still runs
- Events are persisted to files
- Dashboard can catch up on missed events

### 4. **Scalable**
- Multiple dashboard clients can connect
- Each gets same events (broadcast)
- No state in backend (stateless)

### 5. **Historical**
- Event logs are saved
- Can replay past runs
- Audit trail for debugging

## Event Examples

### Translator Stage
```json
{"run_id": "abc-123", "stage": "translator", "event": "start", "msg": "Starting data translator stage"}
{"run_id": "abc-123", "stage": "translator", "event": "metric", "data": {"raw_file_count": 6}}
{"run_id": "abc-123", "stage": "translator", "event": "log", "msg": "Processing ES_2025_01.csv"}
{"run_id": "abc-123", "stage": "translator", "event": "success", "msg": "Translation completed successfully"}
```

### Analyzer Stage
```json
{"run_id": "abc-123", "stage": "analyzer", "event": "start", "msg": "Starting breakout analyzer"}
{"run_id": "abc-123", "stage": "analyzer", "event": "metric", "data": {"processed_file_count": 6, "instruments": ["ES", "NQ", "YM"]}}
{"run_id": "abc-123", "stage": "analyzer", "event": "success", "msg": "Analysis completed successfully"}
```

### Pipeline Overall
```json
{"run_id": "abc-123", "stage": "pipeline", "event": "start", "msg": "Pipeline run started"}
{"run_id": "abc-123", "stage": "pipeline", "event": "success", "msg": "Pipeline complete - success"}
```

## Troubleshooting

### No Events Showing

1. **Check event log file exists**:
   - `automation/logs/events/pipeline_{run_id}.jsonl`
   - File should be created when pipeline starts

2. **Check WebSocket connection**:
   - Open browser DevTools → Network → WS
   - Should see connection to `ws://localhost:8000/ws/events/{run_id}`
   - Status should be "101 Switching Protocols"

3. **Check backend logs**:
   - Backend should log: "Tailing event log: pipeline_{run_id}.jsonl"
   - Check for errors in backend console

4. **Check pipeline is running**:
   - Verify pipeline process is active
   - Check pipeline log file: `automation/logs/pipeline_*.log`

### Events Delayed

- **Normal**: 0.1 second polling interval means up to 0.1s delay
- **If longer**: Check file system performance, backend CPU usage

### WebSocket Disconnects

- **Auto-reconnect**: WebSocketManager should reconnect automatically
- **Check backend**: Ensure backend is still running
- **Check network**: Firewall blocking WebSocket connections?

## Summary

The dashboard integration works through:

1. **Pipeline** writes structured events to JSONL files
2. **Backend** tails files and broadcasts via WebSocket
3. **Frontend** connects and displays events in real-time

This architecture is:
- ✅ **Decoupled**: Pipeline and dashboard are independent
- ✅ **Real-time**: Events appear immediately
- ✅ **Fault tolerant**: Works even if dashboard is down
- ✅ **Historical**: Can replay past runs
- ✅ **Compatible**: Refactored pipeline uses same event format

The refactored pipeline system is **fully compatible** with the existing dashboard - no changes needed!


