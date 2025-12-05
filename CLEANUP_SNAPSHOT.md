# Cleanup Snapshot - Created Before File Deletion

**Date**: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
**Purpose**: Snapshot before cleanup to enable rollback

## Files to be Deleted

### Duplicate Router Files
- `dashboard/backend/routers/pipeline_new.py`
- `dashboard/backend/routers/websocket_new.py`

### Test/Diagnostic Files
- `dashboard/backend/test_startup.py`
- `dashboard/backend/test_fastapi_startup.py`
- `dashboard/backend/test_pipeline_run.py`
- `dashboard/backend/test_translator_direct.py`
- `dashboard/backend/test_orchestrator.py` (if exists)
- `dashboard/backend/test_orchestrator_startup.py` (if exists)
- `dashboard/backend/test_startup_simple.py` (if exists)
- `dashboard/backend/continuous_test.py`
- `dashboard/backend/diagnostic.py`
- `dashboard/backend/run_with_debug.py` (if exists)
- `dashboard/backend/main_simplified.py`

### Historical Documentation Files
See CLEANUP_PLAN.md for complete list.

## Rollback Instructions

If you need to rollback:
1. Use git: `git checkout HEAD -- <file>` to restore specific files
2. Use git: `git reset --hard HEAD` to restore all changes (if committed)
3. Check git log for commit before cleanup

## Git Status Before Cleanup
See git status output below.

