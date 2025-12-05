# Pipeline Orchestrator Implementation

## Overview

A complete Pipeline Orchestrator subsystem has been implemented in `dashboard/backend/orchestrator/`. This provides a single authority for pipeline runs, state management, scheduling, and event emission.

## Structure

```
dashboard/backend/orchestrator/
├── __init__.py          # Package exports
├── config.py            # Configuration (timeouts, retry policy, paths, stages)
├── state.py             # FSM, PipelineState, RunContext
├── events.py            # EventBus abstraction
├── locks.py             # Distributed/lockfile logic
├── runner.py            # Stage runners + retry logic
├── scheduler.py          # Cron-like schedule executor
├── watchdog.py           # Monitoring & self-healing
└── service.py            # Orchestrator facade
```

## Key Components

### 1. State Management (state.py)
- **PipelineRunState**: FSM with states (IDLE, STARTING, RUNNING_*, SUCCESS, FAILED, RETRYING, STOPPED)
- **PipelineStage**: Enum for stages (TRANSLATOR, ANALYZER, MERGER)
- **RunContext**: Immutable context for each run
- **PipelineStateManager**: Enforces valid state transitions with asyncio.Lock

### 2. Event Bus (events.py)
- **EventBus**: Centralized event publishing/subscription
- Ring buffer of recent events (configurable size, default 1000)
- Writes to JSONL files for archival
- Async iterator for WebSocket subscribers
- No more file tailing - all events go through the bus

### 3. Lock Management (locks.py)
- **LockManager**: File-based locks to prevent overlapping runs
- Handles stale locks (based on timeout/heartbeat)
- Lock files include run_id, timestamps, heartbeat

### 4. Pipeline Runner (runner.py)
- **PipelineRunner**: Runs stages with retry logic
- Integrates with existing automation pipeline services
- Validates stage output after execution
- Exponential backoff for retries

### 5. Scheduler (scheduler.py)
- **Scheduler**: Background async task for scheduled runs
- Loads schedule from config file
- Calculates next run time
- Triggers orchestrator.start_pipeline(manual=False)
- Reloads config when schedule changes

### 6. Watchdog (watchdog.py)
- **Watchdog**: Periodic health checks
- Detects hung runs (timeout/heartbeat checks)
- Emits heartbeat events
- Can transition stuck runs to FAILED

### 7. Orchestrator Service (service.py)
- **PipelineOrchestrator**: Main facade that coordinates all components
- Methods:
  - `start()`: Start scheduler and watchdog
  - `start_pipeline(manual)`: Start new run
  - `stop_pipeline()`: Stop current run
  - `get_status()`: Get current RunContext
  - `get_snapshot()`: Get status + recent events + metrics
  - `run_single_stage(stage)`: Run individual stage

## Integration

### Main App (main.py)
- Creates orchestrator instance on startup
- Calls `orchestrator.start()` in lifespan handler
- Calls `orchestrator.stop()` on shutdown
- Stores orchestrator in global variable for router access

### Routers

#### pipeline.py (NEW)
- `POST /api/pipeline/start` → `orchestrator.start_pipeline(manual=True)`
- `GET /api/pipeline/status` → `orchestrator.get_status()`
- `GET /api/pipeline/snapshot` → `orchestrator.get_snapshot()`
- `POST /api/pipeline/stop` → `orchestrator.stop_pipeline()`
- `POST /api/pipeline/stage/{stage}` → `orchestrator.run_single_stage(stage)`
- `POST /api/pipeline/reset` → `orchestrator.state_manager.clear_run()`

#### websocket.py (NEW)
- `WS /ws/events` → Subscribe to EventBus
- `WS /ws/events/{run_id}` → Subscribe to EventBus (run_id for filtering)
- Sends snapshot on connect
- Streams events from EventBus (no file tailing)

#### schedule.py (TO UPDATE)
- `GET /api/schedule` → Read config file
- `POST /api/schedule` → Update config file + `scheduler.reload()`

## Migration Notes

### Old vs New

**Old Approach:**
- Routers directly called scripts via subprocess
- WebSocket tailed JSONL files
- No centralized state management
- No retry logic
- No scheduling in backend

**New Approach:**
- Routers call orchestrator methods
- WebSocket subscribes to EventBus
- Centralized FSM state management
- Retry logic with exponential backoff
- Built-in scheduler and watchdog

### Backward Compatibility

- Old scheduler script still exists (can be removed later)
- Event log files still written (for archival)
- API endpoints maintain same structure (different implementation)

## Next Steps

1. **Replace old routers**: Rename `pipeline_new.py` → `pipeline.py`, `websocket_new.py` → `websocket.py`
2. **Update schedule.py**: Add `scheduler.reload()` call on schedule update
3. **Test integration**: Verify orchestrator starts/stops correctly
4. **Remove legacy code**: Remove old scheduler process management
5. **Update frontend**: Ensure frontend works with new API responses

## Configuration

Orchestrator config is created via `OrchestratorConfig.from_environment()`:
- Reads `QTSW2_ROOT` from environment or uses default
- Sets up event logs directory
- Configures stage timeouts and retry policies
- Defaults can be overridden

## Event Format

Events follow existing JSON schema:
```json
{
  "run_id": "uuid",
  "stage": "translator|analyzer|merger|pipeline|system",
  "event": "start|success|failure|log|metric|error|state_change|heartbeat",
  "timestamp": "ISO8601",
  "msg": "Optional message",
  "data": {"key": "value"}
}
```

## State Transitions

Valid transitions are enforced:
- IDLE → SCHEDULED, STARTING
- STARTING → RUNNING_TRANSLATOR, FAILED, STOPPED
- RUNNING_TRANSLATOR → RUNNING_ANALYZER, FAILED, RETRYING, STOPPED
- RUNNING_ANALYZER → RUNNING_MERGER, FAILED, RETRYING, STOPPED
- RUNNING_MERGER → SUCCESS, FAILED, RETRYING, STOPPED
- RETRYING → RUNNING_*, FAILED, STOPPED
- SUCCESS → IDLE
- FAILED → IDLE, RETRYING
- STOPPED → IDLE

## Error Handling

- All async operations wrapped in try/except
- Errors logged but don't crash orchestrator
- Failed runs transition to FAILED state
- Watchdog detects hung runs and transitions to FAILED
- Lock management prevents overlapping runs

