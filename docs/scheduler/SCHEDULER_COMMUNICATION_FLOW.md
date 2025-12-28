# Windows Task Scheduler Communication Flow

## Overview

This document explains how the system communicates with Windows Task Scheduler and how scheduler events flow to the live events log in the dashboard.

## Architecture Flow

```
┌─────────────────────────────────────────────────────────────────┐
│                    Windows Task Scheduler                       │
│  (Runs every 15 minutes at :00, :15, :30, :45)                 │
│  Task Name: "Pipeline Runner"                                  │
└──────────────────────────┬──────────────────────────────────────┘
                           │
                           │ Triggers
                           ▼
┌─────────────────────────────────────────────────────────────────┐
│         automation/run_pipeline_standalone.py                    │
│  • Creates separate orchestrator instance                      │
│  • Runs pipeline independently                                  │
│  • Writes events to JSONL files                                 │
└──────────────────────────┬──────────────────────────────────────┘
                           │
                           │ Writes events
                           ▼
┌─────────────────────────────────────────────────────────────────┐
│     automation/logs/events/pipeline_{run_id}.jsonl             │
│  • Each scheduled run creates a new JSONL file                  │
│  • Events written in real-time as pipeline runs                │
│  • Format: One JSON event per line                              │
└──────────────────────────┬──────────────────────────────────────┘
                           │
                           │ Monitored by
                           ▼
┌─────────────────────────────────────────────────────────────────┐
│     Dashboard Backend (PipelineOrchestrator)                   │
│  • JSONL Event Replay Monitor (background task)                │
│  • Scans event directory every 2 seconds                        │
│  • Reads only new data (offset-based)                           │
│  • Publishes events to EventBus                                 │
└──────────────────────────┬──────────────────────────────────────┘
                           │
                           │ Publishes to
                           ▼
┌─────────────────────────────────────────────────────────────────┐
│                    EventBus                                      │
│  • Central event distribution hub                              │
│  • Maintains ring buffer of recent events                       │
│  • Writes to JSONL (for manual runs)                            │
│  • Broadcasts to WebSocket subscribers                         │
└──────────────────────────┬──────────────────────────────────────┘
                           │
                           │ Streams to
                           ▼
┌─────────────────────────────────────────────────────────────────┐
│              WebSocket Router (/ws/events/{run_id})             │
│  • Filters events by run_id                                     │
│  • Pushes events to connected frontend clients                  │
└──────────────────────────┬──────────────────────────────────────┘
                           │
                           │ Displays in
                           ▼
┌─────────────────────────────────────────────────────────────────┐
│                    Dashboard Frontend                           │
│  • Real-time event log                                          │
│  • Live updates as pipeline runs                                │
└─────────────────────────────────────────────────────────────────┘
```

## Step-by-Step Communication

### 1. Windows Task Scheduler → Standalone Script

**Location**: Windows Task Scheduler configuration
- **Task Name**: "Pipeline Runner"
- **Trigger**: Every 15 minutes (:00, :15, :30, :45)
- **Action**: Runs `automation/run_pipeline_standalone.py`

**How it works**:
```powershell
# Windows Task Scheduler executes:
python automation/run_pipeline_standalone.py
```

**Code**: `automation/run_pipeline_standalone.py`
- Creates a new orchestrator instance
- Runs pipeline independently (no dashboard backend required)
- Writes all events to JSONL files

### 2. Standalone Script → JSONL Files

**Location**: `automation/logs/events/pipeline_{run_id}.jsonl`

**How events are written**:
```python
# In orchestrator/events.py - EventBus.publish()
event_log_file = event_logs_dir / f"pipeline_{run_id}.jsonl"
with open(event_log_file, "a", encoding="utf-8") as f:
    f.write(json.dumps(event) + "\n")
```

**Event format**:
```json
{"run_id": "abc123", "stage": "translator", "event": "start", "timestamp": "2025-12-08T21:45:00", "msg": "Starting translator"}
{"run_id": "abc123", "stage": "translator", "event": "success", "timestamp": "2025-12-08T21:45:02", "msg": "Translator completed"}
```

### 3. Dashboard Backend → JSONL Monitor

**Location**: `dashboard/backend/orchestrator/service.py`

**How it monitors** (based on JSONL_MONITOR_SUBSYSTEM.md):
- Background task runs every 2 seconds
- Scans `automation/logs/events/` directory
- Uses offset-based reading (only reads new bytes)
- Deduplicates using `(run_id, line_index)` key
- Publishes events to EventBus

**Pseudo-code**:
```python
async def _monitor_jsonl_files():
    while running:
        # Scan for JSONL files
        for jsonl_file in event_logs_dir.glob("pipeline_*.jsonl"):
            last_offset = _file_offsets.get(jsonl_file, 0)
            current_size = jsonl_file.stat().st_size
            
            if current_size > last_offset:
                # Read only new data
                with open(jsonl_file, "rb") as f:
                    f.seek(last_offset)
                    new_data = f.read()
                
                # Parse new lines
                for line in new_data.decode().split('\n'):
                    if line.strip():
                        event = json.loads(line)
                        # Deduplicate
                        key = (event['run_id'], line_index)
                        if key not in _seen_events:
                            await self.event_bus.publish(event)
                            _seen_events[key] = True
                
                # Update offset
                _file_offsets[jsonl_file] = current_size
        
        await asyncio.sleep(2)  # Check every 2 seconds
```

### 4. EventBus → WebSocket

**Location**: `dashboard/backend/routers/websocket.py`

**How events flow**:
```python
# WebSocket endpoint subscribes to EventBus
async for event in orchestrator.event_bus.subscribe():
    # Filter by run_id if specified
    if run_id is None or event.get("run_id") == run_id:
        await websocket.send_json(event)
```

**WebSocket URL**: `ws://localhost:8001/ws/events/{run_id}`

### 5. Frontend → Live Events Log

**Location**: `dashboard/frontend/src/hooks/usePipelineState.js`

**How it connects**:
```javascript
// WebSocket manager connects to backend
websocketManager.connect(runId)

// Receives events in real-time
websocketManager.on('event', (event) => {
  // Update UI with new event
  setEventsFormatted(prev => [...prev, formatEvent(event)])
})
```

## Notification Mechanism

### Standalone Script → Dashboard Backend

**Location**: `automation/run_pipeline_standalone.py`

When a scheduled run starts, the standalone script notifies the dashboard backend:

```python
async def notify_dashboard_backend(run_id: str):
    async with httpx.AsyncClient(timeout=2.0) as client:
        await client.post(
            "http://localhost:8001/api/pipeline/notify-scheduled-run",
            json={"run_id": run_id, "source": "windows_scheduler"},
            timeout=2.0
        )
```

**Purpose**: 
- Tells dashboard backend that a scheduled run has started
- Allows dashboard to actively monitor that run's JSONL file
- Enables real-time event streaming for scheduled runs

**Note**: This notification is non-blocking - if the backend is down, the scheduled run continues independently.

## Windows Task Scheduler Control

### Enabling/Disabling

**Location**: `dashboard/backend/orchestrator/scheduler.py`

**How it works**:
```python
# Enable scheduler
def enable(self):
    ps_command = f"Enable-ScheduledTask -TaskName '{self.TASK_NAME}'"
    subprocess.run(["powershell", "-Command", ps_command])

# Disable scheduler  
def disable(self):
    ps_command = f"Disable-ScheduledTask -TaskName '{self.TASK_NAME}'"
    subprocess.run(["powershell", "-Command", ps_command])
```

**API Endpoints**:
- `POST /api/scheduler/enable` - Enables Windows Task Scheduler
- `POST /api/scheduler/disable` - Disables Windows Task Scheduler
- `GET /api/scheduler/status` - Gets current status

**Status Check**:
```python
def _check_windows_task_status(self):
    ps_command = f"Get-ScheduledTask -TaskName '{self.TASK_NAME}'"
    result = subprocess.run(["powershell", "-Command", ps_command])
    # Returns: exists, enabled, state
```

## Event Flow Summary

1. **Windows Task Scheduler** triggers `run_pipeline_standalone.py` every 15 minutes
2. **Standalone script** creates orchestrator, runs pipeline, writes events to JSONL
3. **JSONL Monitor** (dashboard backend) reads new events from JSONL files every 2 seconds
4. **EventBus** receives events and broadcasts to subscribers
5. **WebSocket** streams events to connected frontend clients
6. **Frontend** displays events in real-time in the events log

## Key Features

- **Decoupled**: Scheduled runs work independently of dashboard
- **Real-time**: Events appear in dashboard within 2 seconds
- **Deduplication**: Prevents duplicate events from re-reading files
- **Offset-based**: Only reads new data, efficient for large files
- **Resilient**: Dashboard can be down, scheduled runs still work

## Troubleshooting

### Events not appearing in dashboard

1. **Check JSONL files exist**:
   ```powershell
   Get-ChildItem automation\logs\events\pipeline_*.jsonl
   ```

2. **Check JSONL Monitor is running**:
   - Look for "JSONL Event Replay Monitor started" in backend logs

3. **Check WebSocket connection**:
   - Open browser DevTools → Network → WS
   - Verify connection to `/ws/events/{run_id}`

4. **Check EventBus**:
   - Verify events are being published (check backend logs)

### Scheduler not running

1. **Check Windows Task Scheduler**:
   ```powershell
   Get-ScheduledTask -TaskName "Pipeline Runner"
   ```

2. **Check task is enabled**:
   ```powershell
   Get-ScheduledTask -TaskName "Pipeline Runner" | Select-Object Enabled
   ```

3. **Check last run time**:
   ```powershell
   Get-ScheduledTask -TaskName "Pipeline Runner" | Get-ScheduledTaskInfo
   ```

