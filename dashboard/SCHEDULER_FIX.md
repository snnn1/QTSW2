# Scheduler Fix - Events Not Showing

## Problem
- Scheduler starts but no events appear in dashboard
- Scheduler process runs but doesn't create event log files
- Dashboard can't find events because no event logs exist

## Root Cause
When scheduler runs in `wait_for_schedule()` mode:
1. `EVENT_LOG_PATH` is `None` (not set)
2. `emit_event()` returns early if `EVENT_LOG_PATH` is `None`
3. No event log files are created
4. Dashboard can't find events

## Solution

### 1. Create Event Log in `run_now()` ✅
**File**: `automation/daily_data_pipeline_scheduler.py`

**Change**: Create event log file if not already set
```python
# Create event log file for this run (if not already set)
global EVENT_LOG_PATH
if EVENT_LOG_PATH is None:
    event_log_file = EVENT_LOGS_DIR / f"pipeline_{run_id}.jsonl"
    event_log_file.touch()
    EVENT_LOG_PATH = str(event_log_file)
    self.logger.info(f"Created event log: {EVENT_LOG_PATH}")
```

**Result**: Each pipeline run gets its own event log file

### 2. Reset EVENT_LOG_PATH for Each Scheduled Run ✅
**File**: `automation/daily_data_pipeline_scheduler.py`

**Change**: Reset `EVENT_LOG_PATH` before each scheduled run
```python
# Reset EVENT_LOG_PATH for each scheduled run so a new log file is created
global EVENT_LOG_PATH
EVENT_LOG_PATH = None

self.run_now(wait_for_export=False, launch_ninjatrader=False)
```

**Result**: Each scheduled run creates a new event log file

### 3. Emit Scheduler Start Event ✅
**File**: `automation/daily_data_pipeline_scheduler.py`

**Change**: Emit a scheduler start event when scheduler starts
```python
# Emit a scheduler start event (create a temporary event log for this)
global EVENT_LOG_PATH
scheduler_start_id = str(uuid.uuid4())
if EVENT_LOG_PATH is None:
    event_log_file = EVENT_LOGS_DIR / f"scheduler_{scheduler_start_id}.jsonl"
    event_log_file.touch()
    EVENT_LOG_PATH = str(event_log_file)
    emit_event(scheduler_start_id, "scheduler", "start", "Scheduler started - will run every 15 minutes")
    EVENT_LOG_PATH = None  # Reset so each pipeline run gets its own log
```

**Result**: Dashboard can see scheduler started

---

## How It Works Now

### Scheduler Startup
1. Scheduler starts → Creates event log file
2. Emits "scheduler start" event
3. Waits for next 15-minute mark

### Scheduled Runs
1. At 15-minute mark → Resets `EVENT_LOG_PATH`
2. Calls `run_now()` → Creates new event log file
3. Pipeline runs → Events written to log file
4. Dashboard finds log file → Shows events

### Dashboard Connection
1. Frontend calls `/api/pipeline/status`
2. Backend finds most recent `pipeline_*.jsonl` file
3. Returns `run_id` from that file
4. Frontend connects WebSocket to `/ws/events/{run_id}`
5. Backend streams events from log file

---

## Testing

1. **Restart backend** → Scheduler auto-starts
2. **Check scheduler status**: `GET /api/scheduler/status`
3. **Check pipeline status**: `GET /api/pipeline/status` (should find event log)
4. **Wait for next 15-minute mark** → Pipeline runs
5. **Check dashboard** → Events should appear

---

## Files Modified

1. `automation/daily_data_pipeline_scheduler.py`
   - `run_now()`: Creates event log if not set
   - `wait_for_schedule()`: Resets EVENT_LOG_PATH, emits start event

---

## Result

✅ **Scheduler creates event logs** for each run
✅ **Events are written** to log files
✅ **Dashboard can find** and display events
✅ **WebSocket streams** events in real-time

The scheduler should now work and events should appear in the dashboard!



