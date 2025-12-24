# Scheduler Architecture - Standalone vs Backend-Dependent

## Current Architecture (Backend-Dependent)

```
Windows Task Scheduler
    ↓
trigger_pipeline.py
    ↓ (HTTP Request)
Backend API (http://localhost:8001)
    ↓
PipelineOrchestrator
    ↓
Pipeline Execution
```

**Problem:** Requires backend to be running

## New Architecture (Standalone)

```
Windows Task Scheduler
    ↓
run_pipeline_standalone.py
    ↓ (Direct)
PipelineOrchestrator
    ↓
Pipeline Execution
```

**Benefits:**
- ✅ **No backend required** - runs completely independently
- ✅ **Backend optional** - can still be used for tracking/monitoring
- ✅ **More reliable** - no HTTP dependency
- ✅ **Faster** - no network overhead

## How It Works

### Standalone Runner (`run_pipeline_standalone.py`)
1. Directly instantiates `PipelineOrchestrator`
2. Starts the orchestrator
3. Triggers pipeline run
4. Waits for completion
5. Stops orchestrator

### Backend (Optional - for Tracking)
- Can still run separately for dashboard/monitoring
- Reads same state files and event logs
- Provides WebSocket streaming for real-time updates
- **Not required** for scheduled runs to work

## Migration

The scheduler setup script has been updated to use the standalone runner by default.

**To switch:**
1. Run `batch\SETUP_WINDOWS_SCHEDULER.bat` (as Administrator)
2. This will update the task to use `run_pipeline_standalone.py`
3. Scheduled runs will now work independently

## Benefits

✅ **Scheduler is truly independent** - no backend dependency
✅ **Backend is optional** - only needed for dashboard
✅ **More reliable** - no network failures
✅ **Simpler** - fewer moving parts









