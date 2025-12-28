# Automation Folder - Usage Analysis Report

Generated: 2025-12-11

## Summary

This report analyzes which files in the `automation/` folder are actively used vs. obsolete.

---

## ✅ KEEP - Actively Used Files

### Core Pipeline Files

1. **`pipeline_runner.py`** ✅ **REMOVED** (was empty, now deleted)
   - **Status**: Deleted (was empty)
   - **Replaced by**: `run_pipeline_standalone.py`
   - **Purpose**: Was empty file, now removed

2. **`run_pipeline_standalone.py`** ✅ **KEEP**
   - **Status**: Active entry point (PRIMARY for Windows Task Scheduler)
   - **Used by**: 
     - `setup_task_scheduler.ps1` (line 13) - **PRIMARY SETUP SCRIPT**
     - `RECREATE_TASK_LIMITED.ps1` (line 53)
     - `test_pipeline_data_range.py` (line 74)
   - **Purpose**: Async standalone runner (doesn't require backend)
   - **Size**: 4.9 KB
   - **Last Modified**: Aug 12, 2025 (but still actively used)

3. **`config.py`** ✅ **KEEP**
   - **Status**: Active - Core configuration
   - **Used by**: All pipeline modules
   - **Purpose**: Centralized configuration
   - **Size**: 3.9 KB

4. **`pipeline/orchestrator.py`** ✅ **KEEP**
   - **Status**: Active - Core orchestration
   - **Used by**: `run_pipeline_standalone.py`
   - **Purpose**: Pipeline orchestration logic

5. **`pipeline/stages/`** ✅ **KEEP**
   - **Status**: Active - Pipeline stages
   - **Files**: `translator.py`, `analyzer.py`, `merger.py`
   - **Purpose**: Individual pipeline stage implementations

6. **`services/`** ✅ **KEEP**
   - **Status**: Active - Supporting services
   - **Files**: `event_logger.py`, `file_manager.py`, `process_supervisor.py`
   - **Purpose**: Shared services for pipeline

7. **`audit.py`** ✅ **KEEP**
   - **Status**: Active
   - **Used by**: `run_pipeline_standalone.py`
   - **Purpose**: Audit reporting

8. **`logging_setup.py`** ✅ **KEEP**
   - **Status**: Active
   - **Used by**: `run_pipeline_standalone.py`
   - **Purpose**: Logging configuration

9. **`test_dry_run.py`** ✅ **KEEP**
    - **Status**: Active testing utility
    - **Used by**: Testing workflows
    - **Purpose**: Dry run testing
    - **Size**: 6.6 KB

11. **`data_lifecycle.py`** ✅ **KEEP**
    - **Status**: Active
    - **Used by**: `test_dry_run.py` (line 148)
    - **Purpose**: Data lifecycle management
    - **Size**: 4.8 KB

### Setup/Configuration Files

12. **`setup_task_scheduler.ps1`** ✅ **KEEP**
    - **Status**: Active - PRIMARY setup script
    - **Uses**: `run_pipeline_standalone.py`
    - **Purpose**: Windows Task Scheduler setup

13. **`setup_task_scheduler.bat`** ✅ **KEEP**
    - **Status**: Active - Alternative setup script
    - **Uses**: `run_pipeline_standalone.py`
    - **Purpose**: Windows Task Scheduler setup (batch version)

14. **`setup_backend_autostart.ps1`** ✅ **KEEP**
    - **Status**: Active
    - **Purpose**: Backend autostart configuration

15. **`FIX_TASK_PERMISSIONS.ps1`** ✅ **KEEP**
    - **Status**: Active utility
    - **Purpose**: Fix scheduler permissions

16. **`RECREATE_TASK_LIMITED.ps1`** ✅ **KEEP**
    - **Status**: Active utility
    - **Uses**: `run_pipeline_standalone.py`
    - **Purpose**: Recreate scheduler task

17. **`disable_scheduler.bat`** ✅ **KEEP**
    - **Status**: Active utility
    - **Purpose**: Disable scheduler

18. **`schedule_config.json`** ✅ **KEEP**
    - **Status**: Active configuration
    - **Purpose**: Schedule configuration

### Documentation

19. **`README_SCHEDULER.md`** ✅ **KEEP**
    - **Status**: Active documentation
    - **Note**: References `simple_scheduler` that doesn't exist (needs update)

---

## ⚠️ REVIEW - Potentially Obsolete Files

### Large Legacy Scheduler

1. **`daily_data_pipeline_scheduler.py`** ✅ **REMOVED**
   - **Status**: Legacy but still referenced
   - **Size**: 82.5 KB (very large)
   - **Last Modified**: Nov 12, 2025
   - **Used by**: 
     - `dashboard/backend/main.py` (line 28) - **BUT marked as "legacy" in comments**
     - Imports `debug_log_window` (line 178)
   - **Purpose**: Old scheduler implementation
   - **Recommendation**: 
     - **KEEP FOR NOW** - Still used by dashboard backend (legacy mode)
     - Consider migrating dashboard to use new orchestrator
     - Once migrated, can be removed

### Debug Utilities

2. **`debug_log_window.py`** ⚠️ **REVIEW**
   - **Status**: Used by legacy scheduler
   - **Size**: 6.6 KB
   - **Last Modified**: Dec 3, 2025
   - **Used by**: Legacy scheduler (removed)
   - **Purpose**: Debug window utility
   - **Recommendation**: 
     - ✅ **KEEP** - Used by orchestrator system
     - **REMOVE** when legacy scheduler is removed

3. **`debug_log_console.py`** ⚠️ **REVIEW**
   - **Status**: Only self-referencing
   - **Size**: 4.1 KB
   - **Last Modified**: Dec 3, 2025
   - **Used by**: Only references itself (line 55)
   - **Purpose**: Debug console utility
   - **Recommendation**: 
     - **REMOVE** - Appears unused
     - Can be safely deleted

### Documentation Issues

4. **`SIMPLE_SCHEDULER.md`** ⚠️ **FIX**
   - **Status**: References non-existent file
   - **Issue**: References `simple_scheduler.py` which doesn't exist
   - **Recommendation**: 
     - **UPDATE** - Remove references to `simple_scheduler`
     - Or **DELETE** if obsolete

---

## ❌ REMOVE - Obsolete Files

1. **`debug_log_console.py`** ❌ **REMOVE**
   - **Reason**: Only self-references, no external usage
   - **Safe to delete**: Yes

---

## 📊 File Usage Summary

### Entry Points (Multiple - Need Consolidation?)
- `run_pipeline_standalone.py` - **PRIMARY** (used by PowerShell setup)
- `automation/run_pipeline_standalone.py` - **CURRENT** (used by batch setup)
- `automation/run_pipeline_standalone.py` - **CURRENT** (entry point for Windows Task Scheduler)

### Recommendation for Entry Points
- **Current**: Windows Task Scheduler uses `run_pipeline_standalone.py` ✅
- **Dashboard**: Uses orchestrator system via `automation/run_pipeline_standalone.py` ✅
- **Action**: Migrate dashboard to use new orchestrator, then remove legacy scheduler

---

## 🎯 Action Items

### Immediate (Safe to Remove)
1. ✅ Delete `debug_log_console.py` (unused)

### Short-term (Update/Fix)
2. ✅ Update `SIMPLE_SCHEDULER.md` - Remove references to non-existent `simple_scheduler.py`
3. ✅ Update `README_SCHEDULER.md` - Remove references to non-existent `simple_scheduler.py`

### Long-term (Migration)
4. ✅ COMPLETED: Dashboard backend now uses orchestrator system exclusively
5. ✅ COMPLETED: `daily_data_pipeline_scheduler.py` has been removed

---

## 📈 Statistics

- **Total Files Analyzed**: 20
- **Keep**: 19 files
- **Review**: 4 files
- **Remove**: 1 file
- **Total Size**: ~150 KB (reasonable for automation system)

---

## 🔍 Notes

1. ✅ **RESOLVED**: `pipeline_runner.py` has been removed. Only `run_pipeline_standalone.py` remains as the entry point.

2. ✅ **RESOLVED**: Legacy scheduler `daily_data_pipeline_scheduler.py` has been removed. Dashboard now uses orchestrator system exclusively.

3. **Missing Module**: Documentation references `simple_scheduler.py` which doesn't exist. Update docs.

4. **Debug Utilities**: `debug_log_window.py` is only used by legacy scheduler. `debug_log_console.py` appears completely unused.








