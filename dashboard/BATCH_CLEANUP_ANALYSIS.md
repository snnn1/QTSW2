# Batch File Cleanup Analysis

## Files to DELETE (Obsolete - Replaced by Orchestrator)

### 1. Old Scheduler Files
- `automation/start_simple_scheduler.bat` - Old simple scheduler (replaced by orchestrator)
- `automation/setup_task_scheduler_simple.bat` - Old simple scheduler setup (replaced by orchestrator)
- `batch/START_SCHEDULER.bat` - Old daily_data_pipeline_scheduler.py runner (replaced by orchestrator)

### 2. Old Backend/Dashboard Files
- `dashboard/START_BACKEND.bat` - Old backend startup (replaced by START_ORCHESTRATOR.bat)
- `dashboard/RUN_DIAGNOSTIC.bat` - References deleted test_orchestrator.py
- `dashboard/backend/START_AND_SHOW_ERRORS.bat` - Diagnostic file, no longer needed

### 3. Old Conductor/Log Files
- `batch/TAIL_CONDUCTOR_LOG.bat` - Old conductor log tailer (pipeline_*.log files replaced by orchestrator event logs)

## Files to KEEP (Still Needed)

### Core Orchestrator/Dashboard
- `dashboard/START_ORCHESTRATOR.bat` ✅ **MAIN STARTUP FILE**
- `dashboard/START_ORCHESTRATOR.ps1` ✅ PowerShell version
- `dashboard/cleanup.bat` ✅ Cleanup script
- `batch/START_DASHBOARD.bat` ✅ Dashboard startup
- `batch/START_DASHBOARD.ps1` ✅ Dashboard PowerShell version

### Task Scheduler Setup (Optional but useful)
- `automation/setup_task_scheduler.bat` ✅ Setup Windows Task Scheduler
- `automation/setup_task_scheduler.ps1` ✅ PowerShell version
- `automation/disable_scheduler.bat` ✅ Disable task scheduler

### Component Runners (Still used)
- `batch/RUN_TRANSLATOR_APP.bat` ✅ Run translator
- `batch/RUN_ANALYZER_APP.bat` ✅ Run analyzer
- `batch/RUN_ANALYZER_PARALLEL.bat` ✅ Run parallel analyzer
- `batch/RUN_DATA_MERGER.bat` ✅ Run data merger
- `batch/RUN_MASTER_MATRIX.bat` ✅ Run master matrix
- `batch/RUN_SEQUENTIAL_PROCESSOR.bat` ✅ Run sequential processor
- `batch/RUN_TESTS.bat` ✅ Run tests

### Testing/Debugging
- `batch/TEST_ANALYZER_DASHBOARD.bat` ✅ Test analyzer dashboard
- `batch/TEST_MASTER_MATRIX.bat` ✅ Test master matrix
- `batch/VIEW_MASTER_MATRIX_DEBUG.bat` ✅ View matrix debug
- `TEST_TRADOVATE.bat` ✅ Test Tradovate

### Frontend
- `dashboard/frontend/START_FRONTEND.bat` ✅ Dashboard frontend
- `matrix_timetable_app/frontend/START_FRONTEND.bat` ✅ Matrix frontend

## Summary

**Total batch files:** 25
**Files to delete:** 7
**Files to keep:** 18

## Recommended Action

Delete the 7 obsolete files listed above. They reference old systems that have been replaced by the orchestrator.

