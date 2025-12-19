# Why Scheduled Runs Don't Appear in Dashboard - Complete Checklist

## Flow Overview
1. User clicks "Run" in Windows Task Scheduler
2. Task executes: `python.exe -m automation.run_pipeline_standalone`
3. `run_pipeline_standalone.py` writes events to JSONL files
4. Dashboard backend JSONL monitor reads JSONL files
5. Monitor publishes events to EventBus
6. EventBus broadcasts to WebSocket subscribers
7. Dashboard frontend receives and displays events

---

## 🔴 CATEGORY 1: Windows Task Scheduler Not Executing

### 1.1 Task Scheduler Configuration Issues
- **Task is disabled** - Check Task Scheduler: Task should show "Ready" status
- **Wrong executable path** - Verify `python.exe` path is correct in task action
- **Wrong arguments** - Must be `-m automation.run_pipeline_standalone` (not `automation\run_pipeline_standalone.py`)
- **Wrong working directory** - Should be `C:\Users\jakej\QTSW2`
- **Task fails to start** - Check Task Scheduler "Last Run Result" (should be 0x0)
- **Permissions** - Task may need to run as user, not SYSTEM

### 1.2 Python Environment Issues
- **Wrong Python version** - Task uses different Python than where modules are installed
- **Module not found** - `automation.run_pipeline_standalone` can't be imported
- **Missing dependencies** - Import errors in standalone script
- **Path issues** - `qtsw2_root` calculation fails, sys.path incorrect

### 1.3 Execution Failures
- **Script crashes immediately** - Check `automation/logs/pipeline_standalone.log`
- **Orchestrator creation fails** - Config or initialization error
- **Pipeline start fails** - Locked, validation error, or other failure

---

## 🔴 CATEGORY 2: Events Not Written to JSONL

### 2.1 File Path Issues
- **Wrong event_logs_dir** - Standalone script writes to different directory than monitor reads from
  - Standalone writes to: `qtsw2_root / "automation" / "logs" / "events"`
  - Monitor reads from: `config.event_logs_dir` (check `OrchestratorConfig`)
  - **CRITICAL**: These must match!

### 2.2 File Creation Issues
- **Directory doesn't exist** - `event_logs_dir` not created (permissions?)
- **File permissions** - Can't write JSONL files (read-only directory?)
- **Disk space** - No space to write files
- **File locked** - Another process has file open

### 2.3 Event Logger Issues
- **EventLogger not initialized** - Missing or incorrect log_file path
- **EventLogger not used** - Pipeline stages using wrong logger
- **Events not emitted** - Code paths don't call `event_logger.emit()`
- **JSON serialization fails** - Events contain non-serializable data

### 2.4 File Naming Issues
- **Wrong file pattern** - Events written to files that don't match `pipeline_*.jsonl`
  - Monitor looks for: `pipeline_*.jsonl`
  - Events should be in: `pipeline_{run_id}.jsonl`
- **Fixed run_id issue** - Initial `scheduler/start` event uses `run_id="__scheduled__"` → file: `pipeline___scheduled__.jsonl`
  - This file should be picked up by monitor (matches pattern)

---

## 🔴 CATEGORY 3: JSONL Monitor Not Running

### 3.1 Backend Startup Issues
- **Backend not started** - Dashboard backend must be running
- **Orchestrator not initialized** - `orchestrator_instance` is None
- **Orchestrator.start() not awaited** - Task created but never executes
  - **CRITICAL**: Must be `await orchestrator_instance.start()` in FastAPI lifespan
  - NOT `orchestrator_instance.start()` (missing await)
  - NOT `asyncio.create_task(orchestrator.start())` (fire-and-forget)

### 3.2 Monitor Task Creation Issues
- **Task not created** - `_jsonl_monitor_task` is None
- **Task created in wrong loop** - Task created before event loop running
- **Task cancelled immediately** - Something cancels task on startup
- **Task fails silently** - Exception in monitor startup caught and ignored

### 3.3 Monitor Loop Issues
- **Monitor loop never starts** - Coroutine never enters `while self._running:`
- **Monitor loop exits immediately** - `self._running` is False
- **Monitor loop crashes** - Exception in loop, caught and retries, but keeps failing
- **Monitor loop blocked** - Infinite loop or blocking operation prevents iteration

### 3.4 Configuration Mismatch
- **Wrong event_logs_dir in config** - Monitor looking in wrong directory
  - Check: `OrchestratorConfig.from_environment()` path calculation
  - Monitor uses: `self.config.event_logs_dir`
  - Must match where standalone script writes!

---

## 🔴 CATEGORY 4: Monitor Not Finding Files

### 4.1 Directory Mismatch
- **Monitor scans wrong directory** - `config.event_logs_dir` doesn't match actual file location
- **Files in archive** - Monitor skips `archive/` subdirectory
- **Files deleted/moved** - Files removed before monitor can read them

### 4.2 File Pattern Mismatch
- **Pattern too restrictive** - Monitor uses `glob("pipeline_*.jsonl")` but files named differently
- **Case sensitivity** - Windows should be fine, but verify
- **File extensions** - Files have different extension (.json, .txt, etc.)

### 4.3 Timing Issues
- **Files written after scan** - Monitor scans before files are written (2-second interval)
- **Files processed too quickly** - Monitor reads files before all events written
- **Race condition** - Multiple runs create files faster than monitor can process

### 4.4 File Filtering Issues
- **Files skipped as "large"** - Files >50MB skipped immediately
- **Files skipped as "old"** - Offset tracking marks files as processed incorrectly
- **Files skipped as "no new data"** - Offset tracking incorrect, thinks file fully processed

---

## 🔴 CATEGORY 5: Events Not Ingested from Files

### 5.1 File Reading Issues
- **File permissions** - Monitor can't read JSONL files
- **File locked** - Standalone script still writing, file locked
- **Encoding issues** - UTF-8 decode fails, events skipped
- **Corrupt files** - Malformed JSON lines, events skipped

### 5.2 Offset Tracking Issues
- **Offset tracking wrong** - `_file_offsets` dictionary incorrect
- **Offset reset** - Offsets reset on backend restart, re-processes old events
- **Offset not updated** - Offsets never updated after reading, keeps re-reading same data
- **Partial line handling** - Incorrect handling of incomplete lines at offset boundary

### 5.3 Deduplication Issues
- **Over-aggressive dedup** - `_seen_events` marks legitimate events as duplicates
- **Dedup key collision** - Different events have same (run_id, line_index)
- **Dedup cache cleared** - Cache cleared mid-run, re-processes events

### 5.4 Event Parsing Issues
- **Invalid JSON** - JSON parsing fails, events skipped
- **Missing required fields** - Events missing `run_id`, `timestamp`, etc.
- **Timestamp parsing fails** - Can't parse ISO timestamps, events filtered out

---

## 🔴 CATEGORY 6: Events Not Published to EventBus

### 6.1 EventBus Issues
- **EventBus not initialized** - `orchestrator.event_bus` is None
- **EventBus publish fails** - Exception in `event_bus.publish()` caught and ignored
- **EventBus subscribers missing** - No WebSocket subscribers registered

### 6.2 Publish Logic Issues
- **Events filtered out** - Monitor has logic that filters events before publishing
- **Events published to wrong bus** - Different EventBus instance than dashboard uses
- **Publish silently fails** - Exception caught but not logged

---

## 🔴 CATEGORY 7: WebSocket Not Receiving Events

### 7.1 WebSocket Connection Issues
- **WebSocket not connected** - Dashboard frontend not connected to `/ws/events`
- **WebSocket disconnected** - Connection dropped, no reconnection logic
- **WebSocket connection error** - Failed to establish connection (backend down, CORS, etc.)

### 7.2 EventBus Subscription Issues
- **No subscribers** - EventBus has no active subscribers
- **Subscription fails** - Exception in subscription logic
- **Subscription filter** - WebSocket filters events (e.g., by run_id) and filters out scheduled events

### 7.3 Event Broadcasting Issues
- **Events not broadcast** - EventBus has subscribers but doesn't broadcast
- **Broadcast fails silently** - Exception in broadcast caught and ignored
- **Events lost in queue** - Event queue full, events dropped

---

## 🔴 CATEGORY 8: Dashboard Frontend Not Displaying

### 8.1 Frontend Connection Issues
- **WebSocket not connected** - Frontend WebSocket connection failed
- **Wrong WebSocket URL** - Frontend connecting to wrong endpoint
- **CORS/network issues** - Browser blocking WebSocket connection

### 8.2 Event Processing Issues
- **Events not parsed** - Frontend can't parse event JSON
- **Events filtered out** - Frontend filters events (e.g., only shows manual runs)
- **Events not stored** - Frontend receives but doesn't store in state
- **State not updated** - Events received but UI doesn't re-render

### 8.3 Display Logic Issues
- **Events hidden** - UI logic hides scheduled events
- **Wrong tab/view** - Events shown in different view than user is looking at
- **Timing display** - Events show but timing/cadence display logic wrong

---

## 🔍 QUICK DIAGNOSTIC CHECKLIST

### Step 1: Verify Task Scheduler Execution
```powershell
# Check task status
schtasks /query /tn "Pipeline Runner" /v /fo list

# Check last run result (should be 0x0 = success)
# Check last run time matches when you clicked "Run"
```

### Step 2: Verify JSONL Files Created
```powershell
# Check if files exist in event logs directory
dir "C:\Users\jakej\QTSW2\automation\logs\events\pipeline_*.jsonl"

# Check file modification time (should be recent)
# Check file size (should be > 0)
```

### Step 3: Verify Backend Monitor Running
Check backend logs for:
- `[JSONL Monitor] Monitor loop STARTING`
- `[JSONL Monitor] Monitor loop STARTED`
- `[JSONL Monitor] Iteration 30: Scanning X JSONL files`

### Step 4: Verify Monitor Finding Files
Check backend logs for:
- `[JSONL Monitor] Iteration 30: Scanning X JSONL files in <directory>`
- Verify directory path matches where JSONL files are written

### Step 5: Verify Events Ingested
Check backend logs for:
- `✅ [JSONL Monitor] Ingested X new events from pipeline_*.jsonl`
- `📢 [JSONL Monitor] Published scheduler/start`

### Step 6: Verify WebSocket Connection
Check browser console for:
- WebSocket connection established
- Events received (network tab or console logs)

### Step 7: Verify Frontend Display
Check dashboard UI:
- Events appear in live events feed
- Scheduled run shows in run history

---

## 🎯 MOST LIKELY ISSUES (Based on Recent Changes)

1. **Directory Mismatch** - Monitor `event_logs_dir` doesn't match standalone script's directory
2. **Monitor Not Running** - `await orchestrator.start()` issue or task not starting
3. **File Pattern Mismatch** - Files written with different naming pattern
4. **Offset Tracking** - Monitor thinks files already processed
5. **Task Scheduler Arguments** - Wrong arguments, script fails silently

---

## 🔧 RECOMMENDED FIXES (In Priority Order)

1. **Add logging to verify directory match**:
   - Log `event_logs_dir` in standalone script
   - Log `config.event_logs_dir` in monitor startup
   - Compare and ensure they match

2. **Add logging to verify monitor is running**:
   - Verify startup logs appear
   - Verify periodic scan logs appear
   - Verify ingestion logs appear

3. **Add logging to verify file detection**:
   - Log files found by glob pattern
   - Log file sizes and modification times
   - Log offset tracking decisions

4. **Add logging to verify event publishing**:
   - Log when events published to EventBus
   - Log EventBus subscriber count
   - Log WebSocket message sends

