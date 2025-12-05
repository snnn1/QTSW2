# How The Dashboard Detects Pipeline Runs

## Short Answer

**Yes, the dashboard knows when the pipeline runs, but with a delay.**

The dashboard **polls every 30 seconds** to check for new pipeline runs. So if the pipeline starts automatically (via Task Scheduler), the dashboard will detect it within **0-30 seconds**.

## How It Works

### Detection Mechanism

1. **Frontend Polls Backend** (every 30 seconds)
   - Calls `/api/pipeline/status` endpoint
   - Checks if there's an active pipeline run

2. **Backend Finds Latest Run**
   - Scans `automation/logs/events/` for `pipeline_*.jsonl` files
   - Sorts by modification time (most recent first)
   - Checks if the latest file is "active" (recently modified)

3. **Backend Determines Status**
   - If file is **< 1 hour old** and **< 2 minutes since last event** → Active
   - If file is **> 1 hour old** or **> 2 minutes since last event** → Inactive
   - Reads last event to check if pipeline completed

4. **Frontend Auto-Connects**
   - If new run detected → Automatically connects WebSocket
   - If same run, different stage → Updates stage
   - If same run, same stage → Reconnects if needed

### Timeline Example

```
00:00:00 - Task Scheduler runs pipeline
00:00:01 - Pipeline creates event log: pipeline_abc123.jsonl
00:00:01 - Pipeline writes first event: {"stage": "pipeline", "event": "start"}
00:00:15 - Pipeline writes: {"stage": "translator", "event": "start"}
...
00:00:30 - Dashboard polls backend (checks for new runs)
00:00:30 - Backend finds pipeline_abc123.jsonl (most recent, active)
00:00:30 - Backend returns: {"active": true, "run_id": "abc123", "stage": "translator"}
00:00:30 - Frontend detects new run → Auto-connects WebSocket
00:00:30 - Dashboard starts displaying events in real-time
```

**Delay**: 0-30 seconds (depending on when polling happens)

## Code Flow

### Backend Status Check (`/api/pipeline/status`)

```python
# Find most recent event log
event_logs = sorted(EVENT_LOGS_DIR.glob("pipeline_*.jsonl"), 
                   key=lambda p: p.stat().st_mtime, reverse=True)

latest_log = event_logs[0]

# Check if active (recently modified)
file_age_seconds = time.time() - latest_log.stat().st_mtime
if file_age_seconds > 3600:  # 1 hour
    return {"active": False}

# Read last event
last_event = json.loads(lines[-1])
if is_complete:
    return {"active": False}

return {"active": True, "run_id": run_id, "stage": stage}
```

### Frontend Polling (`usePipelineState.js`)

```javascript
// Poll pipeline status every 30 seconds
const statusInterval = setInterval(checkPipelineStatus, 30000)

// When status check returns new run
if (status.active) {
  if (!state.currentRunId || state.currentRunId !== status.run_id) {
    // New run detected - auto-connect
    websocketManager.connect(status.run_id, handleWebSocketEvent, ...)
  }
}
```

## Detection Scenarios

### Scenario 1: Pipeline Starts Automatically (Task Scheduler)

1. **Task Scheduler** runs `python -m automation.pipeline_runner` at :00, :15, :30, :45
2. **Pipeline** creates event log file immediately
3. **Dashboard** polls every 30 seconds
4. **Within 0-30 seconds**, dashboard detects new run
5. **Dashboard** auto-connects WebSocket
6. **Events** start appearing in real-time

**Result**: ✅ Dashboard detects automatically, with 0-30s delay

### Scenario 2: Pipeline Starts Manually (Dashboard Button)

1. **User** clicks "Run Now" in dashboard
2. **Frontend** calls `/api/pipeline/start`
3. **Backend** starts pipeline and returns `run_id`
4. **Frontend** immediately connects WebSocket (no delay)
5. **Events** appear in real-time

**Result**: ✅ Dashboard knows immediately (no delay)

### Scenario 3: Pipeline Already Running When Dashboard Opens

1. **User** opens dashboard
2. **Frontend** polls status on page load
3. **Backend** finds active run (if file is recent)
4. **Frontend** auto-connects to active run
5. **Events** appear (catches up on existing events first)

**Result**: ✅ Dashboard connects to active run immediately

### Scenario 4: Pipeline Completes

1. **Pipeline** writes final event: `{"stage": "pipeline", "event": "success"}`
2. **Backend** detects completion (checks last event)
3. **Frontend** polls status → `{"active": false}`
4. **Frontend** disconnects WebSocket (after 2 minutes of inactivity)
5. **Dashboard** shows completed state

**Result**: ✅ Dashboard knows when pipeline completes

## Limitations

### 1. **30-Second Polling Delay**

- Dashboard polls every 30 seconds
- Maximum delay: 30 seconds before detection
- Average delay: ~15 seconds

**Impact**: Events may start appearing 0-30 seconds after pipeline starts

### 2. **File Age Check**

- Backend considers runs inactive if file is > 1 hour old
- If you open dashboard after pipeline completed > 1 hour ago, it won't show as active

**Impact**: Only recent runs are detected as "active"

### 3. **No Push Notification**

- Dashboard doesn't get "push" notification when pipeline starts
- Must poll to detect new runs

**Impact**: Requires polling (not instant detection)

## Improvements (Future)

### Option 1: Reduce Polling Interval

```javascript
// Poll every 5 seconds instead of 30
const statusInterval = setInterval(checkPipelineStatus, 5000)
```

**Pros**: Faster detection (0-5s delay)  
**Cons**: More backend requests

### Option 2: File System Watcher

Backend could watch `automation/logs/events/` directory for new files:

```python
from watchdog.observers import Observer
from watchdog.events import FileSystemEventHandler

class EventLogHandler(FileSystemEventHandler):
    def on_created(self, event):
        if event.src_path.endswith('.jsonl'):
            # Notify connected WebSocket clients
            await manager.broadcast_new_run(run_id)
```

**Pros**: Instant detection (no polling)  
**Cons**: More complex, requires file system watcher

### Option 3: Pipeline Notifies Dashboard

Pipeline could send HTTP request to dashboard when it starts:

```python
# In pipeline_runner.py
import requests
requests.post("http://localhost:8000/api/pipeline/notify", 
              json={"run_id": run_id, "status": "started"})
```

**Pros**: Instant notification  
**Cons**: Requires dashboard to be running, adds dependency

## Current Behavior Summary

| Scenario | Detection | Delay | Auto-Connect |
|----------|-----------|-------|--------------|
| Manual start (dashboard button) | ✅ Immediate | 0s | ✅ Yes |
| Automatic start (Task Scheduler) | ✅ Automatic | 0-30s | ✅ Yes |
| Already running when dashboard opens | ✅ Immediate | 0s | ✅ Yes |
| Pipeline completes | ✅ Automatic | 0-30s | ✅ Disconnects |

## Summary

**The dashboard DOES know when the pipeline runs:**

1. ✅ **Automatic detection** via polling (every 30 seconds)
2. ✅ **Auto-connects** to new runs via WebSocket
3. ✅ **Real-time events** once connected
4. ⚠️ **0-30 second delay** for automatic runs (polling interval)

**For manual runs** (via dashboard button), detection is **instant** (no delay).

**For automatic runs** (Task Scheduler), detection happens **within 30 seconds** of pipeline start.


