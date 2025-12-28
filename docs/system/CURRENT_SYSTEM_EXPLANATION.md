# Current System: How Dashboard & Pipeline Work

## Executive Summary

**Two Ways to Run the Pipeline:**
1. **Manual (Dashboard)**: Click "Run Now" → Backend orchestrator runs pipeline → Events stream to dashboard
2. **Automatic (Windows Task Scheduler)**: Runs `run_pipeline_standalone.py` every 15 minutes → Creates its own orchestrator → Events saved to JSONL files → Dashboard loads them on reconnect

**Key Point**: The dashboard orchestrator and standalone orchestrator are **separate instances** but share the same code and state files.

---

## How It Works Now

### 1. Dashboard Startup Flow

```
User starts: batch/START_DASHBOARD.bat
    ↓
Backend starts (port 8001)
    ↓
Orchestrator initialized in backend
    ├─ EventBus created
    ├─ StateManager created (reads/writes orchestrator_state.json)
    ├─ LockManager created (uses pipeline.lock file)
    ├─ Scheduler created (control layer for Windows Task Scheduler)
    ├─ Watchdog created (monitors for hung runs)
    └─ Runner created (executes pipeline stages)
    ↓
Orchestrator.start() called
    ├─ Scheduler.start() → NO-OP (scheduler doesn't run loops anymore)
    ├─ Watchdog.start() → Starts monitoring loop
    └─ Heartbeat loop started
    ↓
Frontend starts (port 5173)
    ↓
Frontend connects to backend
    ├─ REST API: /api/pipeline/status
    ├─ REST API: /api/scheduler/status
    └─ WebSocket: /ws/events/{run_id}
    ↓
Dashboard loads snapshot
    ├─ Gets recent events from EventBus ring buffer
    ├─ Gets events from JSONL files (if no active run)
    └─ Displays current state
```

### 2. Manual Pipeline Run (Click "Run Now")

```
User clicks "Run Now" button
    ↓
Frontend: POST /api/pipeline/start?manual=true
    ↓
Backend: orchestrator.start_pipeline(manual=True)
    ├─ Checks if pipeline already running (via state file)
    ├─ Acquires lock (pipeline.lock file)
    ├─ Creates new RunContext (generates run_id)
    ├─ Transitions state: IDLE → STARTING
    ├─ Publishes event: {"event": "start", "stage": "pipeline"}
    └─ Starts background task: _run_pipeline_background()
    ↓
Background task: runner.run_pipeline(run_ctx)
    ├─ Stage 1: Translator
    │   ├─ Transition: STARTING → RUNNING_TRANSLATOR
    │   ├─ Publishes event: {"event": "start", "stage": "translator"}
    │   ├─ Runs translator service
    │   ├─ Publishes event: {"event": "success", "stage": "translator"}
    │   └─ Validates output
    ├─ Stage 2: Analyzer
    │   ├─ Transition: RUNNING_TRANSLATOR → RUNNING_ANALYZER
    │   ├─ Publishes event: {"event": "start", "stage": "analyzer"}
    │   ├─ Runs analyzer service (parallel for each instrument)
    │   ├─ Publishes events: {"event": "file_start", "file_finish"} for each file
    │   ├─ Publishes event: {"event": "success", "stage": "analyzer"}
    │   └─ Validates output
    ├─ Stage 3: Merger
    │   ├─ Transition: RUNNING_ANALYZER → RUNNING_MERGER
    │   ├─ Publishes event: {"event": "start", "stage": "merger"}
    │   ├─ Runs merger service
    │   ├─ Publishes event: {"event": "success", "stage": "merger"}
    │   └─ Validates output
    └─ Final: Transition to SUCCESS
        ├─ Publishes event: {"event": "state_change", "new_state": "success"}
        └─ Releases lock
    ↓
All events published to EventBus
    ├─ Added to ring buffer (in-memory, last 1000 events)
    ├─ Written to JSONL file: automation/logs/events/pipeline_{run_id}.jsonl
    └─ Broadcast to WebSocket subscribers
    ↓
Frontend receives events via WebSocket
    ├─ Updates UI in real-time
    ├─ Shows current stage
    ├─ Displays event log
    └─ Updates metrics
```

### 3. Automatic Pipeline Run (Windows Task Scheduler)

```
Windows Task Scheduler triggers (every 15 minutes)
    ↓
Runs: python automation/run_pipeline_standalone.py
    ↓
Creates NEW orchestrator instance (separate from dashboard)
    ├─ Same code, same state files
    ├─ Creates its own EventBus
    └─ Writes to same JSONL files
    ↓
Orchestrator.start() → start_pipeline(manual=False)
    ↓
Pipeline runs (same flow as manual)
    ├─ Events published to EventBus
    ├─ Events written to JSONL files
    └─ State saved to orchestrator_state.json
    ↓
Orchestrator stops (standalone script exits)
    ↓
Dashboard (if open) can load events from JSONL files
    ├─ On WebSocket connect, gets snapshot
    ├─ Snapshot loads events from most recent JSONL file
    └─ Displays what happened while dashboard was closed
```

### 4. Scheduler Toggle (Automation ON/OFF)

```
User clicks "Automation: OFF" button
    ↓
Frontend: POST /api/scheduler/disable
    ↓
Backend: scheduler.disable()
    ├─ Calls: schtasks /change /tn "Pipeline Runner" /disable
    ├─ Saves state: scheduler_state.json {"scheduler_enabled": false}
    └─ Returns success/error
    ↓
Windows Task Scheduler task is disabled
    ├─ Task still exists but won't run
    └─ Dashboard button shows "OFF"
    ↓
State persists in scheduler_state.json
    ├─ Loaded on dashboard startup
    └─ Button shows correct state
```

---

## Key Components

### Backend Orchestrator (`dashboard/backend/orchestrator/`)

**service.py** - Main orchestrator
- Coordinates all components
- Manages pipeline lifecycle
- Provides API for starting/stopping

**state.py** - State machine
- Manages pipeline state (IDLE, STARTING, RUNNING_*, SUCCESS, FAILED)
- Persists state to `orchestrator_state.json`
- Handles state transitions

**runner.py** - Pipeline execution
- Runs stages in sequence (Translator → Analyzer → Merger)
- Handles retries
- Validates stage outputs
- Publishes events

**events.py** - Event bus
- In-memory ring buffer (last 1000 events)
- JSONL file writing
- WebSocket broadcasting
- Event subscription

**scheduler.py** - Windows Task Scheduler control
- Enables/disables Windows scheduled task
- NO timing logic (Windows Task Scheduler handles that)
- Persists state to `scheduler_state.json`

**locks.py** - Lock manager
- Prevents overlapping runs
- Uses `pipeline.lock` file
- Heartbeat mechanism

**watchdog.py** - Health monitor
- Detects hung runs
- Monitors lock heartbeat
- Can kill stuck processes

### Frontend (`dashboard/frontend/src/`)

**hooks/usePipelineState.js** - State management
- Central state for entire dashboard
- Handles WebSocket events
- Manages pipeline lifecycle
- Polls for status updates

**services/pipelineManager.js** - REST API client
- All HTTP calls to backend
- Error handling
- Timeout management

**services/websocketManager.js** - WebSocket client
- Connects to `/ws/events/{run_id}`
- Handles reconnection
- Sends ping/pong

---

## Unused Code & Complications

### ❌ UNUSED CODE

1. **`automation/trigger_pipeline.py`** - OBSOLETE
   - **Why**: Was used to call backend API from Windows Task Scheduler
   - **Replaced by**: `automation/run_pipeline_standalone.py` (runs orchestrator directly)
   - **Status**: Still exists but not referenced anywhere
   - **Action**: Can be deleted

2. **`automation/daily_data_pipeline_scheduler.py`** - OBSOLETE
   - **Why**: Old orchestrator implementation
   - **Replaced by**: `dashboard/backend/orchestrator/` (new modular orchestrator)
   - **Status**: Still exists, might be imported somewhere
   - **Action**: Check for imports, then delete if unused

3. **`automation/scheduler_simple.py`** - OBSOLETE
   - **Why**: Simple scheduler implementation
   - **Replaced by**: Windows Task Scheduler + `scheduler.py` control layer
   - **Status**: Likely unused
   - **Action**: Check for imports, then delete

4. **`automation/simple_scheduler.py`** - OBSOLETE
   - **Why**: Another simple scheduler
   - **Replaced by**: Windows Task Scheduler
   - **Action**: Check for imports, then delete

5. **Legacy scheduler endpoints in `main.py`**
   - Old schedule management code (lines 215-341)
   - **Status**: Partially used (some endpoints still work)
   - **Action**: Review and clean up unused endpoints

### ⚠️ COMPLICATIONS

1. **Two Orchestrator Instances**
   - **Problem**: Dashboard has one orchestrator, Windows Task Scheduler creates another
   - **Impact**: Events from scheduled runs only appear in dashboard if it loads from JSONL files
   - **Why**: Standalone script needs to work without backend
   - **Status**: Working as designed, but can be confusing

2. **State File Sharing**
   - **Problem**: Both orchestrator instances write to same `orchestrator_state.json`
   - **Impact**: Last writer wins (can cause race conditions)
   - **Why**: Both need to know if pipeline is running
   - **Status**: Lock file prevents actual conflicts, but state file can be overwritten

3. **Lock File Mechanism**
   - **Problem**: File-based locking can fail if process crashes
   - **Impact**: Lock might not be released, preventing new runs
   - **Why**: Simple file-based approach
   - **Status**: Watchdog should detect and clean up, but not perfect

4. **Scheduler Requires Admin**
   - **Problem**: Dashboard button to toggle scheduler requires backend to run as admin
   - **Impact**: User must run backend as administrator
   - **Why**: Windows Task Scheduler security boundary
   - **Status**: Documented, but not ideal

5. **Event Bus Ring Buffer**
   - **Problem**: In-memory buffer, lost on restart
   - **Impact**: Recent events lost if backend restarts
   - **Why**: Performance (fast in-memory access)
   - **Status**: JSONL files provide persistence, but snapshot might miss recent events

6. **WebSocket Reconnection**
   - **Problem**: If WebSocket disconnects during run, events might be missed
   - **Impact**: Dashboard might not show all events
   - **Why**: Events are published to EventBus, but if WebSocket isn't subscribed, they're lost
   - **Status**: Snapshot loads from JSONL files on reconnect, but not perfect

7. **Port Configuration**
   - **Problem**: Backend uses port 8001, but some docs/scripts reference 8000
   - **Impact**: Confusion
   - **Status**: Mostly fixed, but some references might remain

---

## Data Flow Summary

### Manual Run (Dashboard)
```
User → Frontend → REST API → Orchestrator → Runner → Services → Events → EventBus → WebSocket → Frontend
                                                                    ↓
                                                              JSONL Files
```

### Automatic Run (Scheduler)
```
Windows Task Scheduler → run_pipeline_standalone.py → Orchestrator → Runner → Services → Events → JSONL Files
                                                                                              ↓
                                                                                    Dashboard loads on reconnect
```

### State Persistence
```
Orchestrator State: orchestrator_state.json (shared by both instances)
Scheduler State: scheduler_state.json (dashboard only)
Lock: pipeline.lock (shared by both instances)
Events: pipeline_{run_id}.jsonl (shared, one file per run)
```

---

## Recommendations

### Clean Up
1. Delete `automation/trigger_pipeline.py` (replaced by `run_pipeline_standalone.py`)
2. Check and delete old scheduler files if unused
3. Clean up legacy endpoints in `main.py`

### Simplify
1. Consider making standalone orchestrator share EventBus with dashboard (complex, but would unify events)
2. Improve lock cleanup mechanism (watchdog should be more aggressive)
3. Add event replay from JSONL files on WebSocket reconnect

### Document
1. Clear explanation that scheduler runs independently
2. Document that events from scheduled runs appear after dashboard reconnects
3. Explain admin requirement for scheduler toggle

---

## Current Status: ✅ WORKING

The system works as designed:
- ✅ Manual runs work via dashboard
- ✅ Automatic runs work via Windows Task Scheduler
- ✅ Events are persisted to JSONL files
- ✅ Dashboard can load events from files
- ✅ Scheduler toggle works (with admin)
- ✅ State persists across restarts

**Main complexity**: Two orchestrator instances, but this is intentional for independence.









