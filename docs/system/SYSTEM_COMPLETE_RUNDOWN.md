# QTSW2 System - Complete Rundown & File Analysis

**Generated**: 2025-12-11  
**Purpose**: Comprehensive analysis of all files and folders to identify what's needed vs. obsolete

---

## 📋 Executive Summary

**QTSW2** is a quantitative trading data pipeline system with:
- **Data Translator**: Converts raw trading data to standardized formats
- **Pipeline Dashboard**: Real-time web dashboard for monitoring and control
- **Automated Scheduler**: Runs data pipeline every 15 minutes via Windows Task Scheduler
- **Breakout Analyzer**: Analyzes trading data for patterns
- **Master Matrix**: Matrix and timetable engine for analysis
- **Multiple Streamlit Apps**: Translator, Analyzer, Sequential Processor

**Current Status**: ✅ System is working and functional

---

## 🏗️ System Architecture

### Core Data Flow

```
Raw CSV Files (data/raw/)
    ↓
Translator (translator/) → Processed Parquet (data/processed/)
    ↓
Analyzer (scripts/breakout_analyzer/) → Analysis Results (data/analyzer_runs/)
    ↓
Data Merger (tools/data_merger.py) → Consolidated Data
    ↓
Master Matrix (master_matrix/) → Matrix Analysis
```

### Two Entry Points

1. **Manual (Dashboard)**: 
   - Start: `batch/START_DASHBOARD_AS_ADMIN.bat`
   - Backend: Port 8001 (FastAPI)
   - Frontend: Port 5173 (React + Vite)
   - Orchestrator runs pipeline on demand

2. **Automatic (Windows Task Scheduler)**:
   - Runs: `automation/run_pipeline_standalone.py` every 15 minutes
   - Creates separate orchestrator instance
   - Events saved to JSONL files
   - Dashboard loads events on reconnect

---

## 📁 Folder Analysis

### ✅ CORE FOLDERS (Keep All)

#### 1. **`automation/`** - Pipeline Automation
- **Purpose**: Pipeline orchestration, scheduling, services
- **Key Files**:
  - `automation/run_pipeline_standalone.py` - Main entry point (asynchronous orchestrator)
  - `run_pipeline_standalone.py` - Standalone entry point (async, used by scheduler)
  - `config.py` - Configuration
  - `pipeline/orchestrator.py` - Pipeline orchestration
  - `pipeline/stages/` - Pipeline stages (translator, analyzer, merger)
  - `services/` - Supporting services (event logger, file manager, process supervisor)
- **Status**: ✅ All core files needed
- **Issues**: 
  - `automation/run_pipeline_standalone.py` - Current entry point for Windows Task Scheduler
  - `debug_log_window.py` - Only used by legacy scheduler
  - `SIMPLE_SCHEDULER.md` - References non-existent `simple_scheduler.py`

#### 2. **`batch/`** - Batch Scripts
- **Purpose**: Windows batch launchers for all components
- **Files**: 25 batch files + 1 analysis doc
- **Status**: ✅ All files useful, no deletions needed
- **Issue**: Code references `START_DASHBOARD.bat` but actual file is `START_DASHBOARD_AS_ADMIN.bat`

#### 3. **`dashboard/`** - Web Dashboard
- **Purpose**: Real-time monitoring and control interface
- **Structure**:
  - `backend/` - FastAPI server (port 8001)
    - `orchestrator/` - Pipeline orchestration
    - `routers/` - API endpoints
  - `frontend/` - React app (port 5173)
- **Status**: ✅ Core system component

#### 4. **`translator/`** - Data Translation
- **Purpose**: Core data translation logic
- **Files**: `core.py`, `file_loader.py`, `schema.py`, `contract_rollover.py`, `frequency_detector.py`
- **Status**: ✅ Core system component

#### 5. **`scripts/`** - Streamlit Applications
- **Purpose**: Web UIs for various components
- **Key**: `breakout_analyzer/` - Main analyzer application
- **Status**: ✅ Core system component

#### 6. **`tools/`** - Command-Line Tools
- **Purpose**: CLI utilities
- **Key**: `data_merger.py`, `translate_raw.py`, `run_analyzer_parallel.py`
- **Status**: ✅ Core system component

#### 7. **`master_matrix/`** - Matrix Engine
- **Purpose**: Matrix analysis engine
- **Files**: `master_matrix.py` (1712 lines)
- **Status**: ✅ Core system component

#### 8. **`matrix_timetable_app/`** - Matrix UI
- **Purpose**: Frontend for matrix visualization
- **Status**: ✅ Core system component

#### 9. **`timetable_engine/`** - Timetable Engine
- **Purpose**: Timetable generation
- **Status**: ✅ Core system component

#### 10. **`sequential_processor/`** - Sequential Processing
- **Purpose**: Sequential data processing
- **Status**: ✅ Core system component

#### 11. **`market_data_etl/`** - Market Data ETL
- **Purpose**: External data source integration (Tradovate API)
- **Status**: ✅ Core system component

#### 12. **`tests/`** - Unit Tests
- **Purpose**: Automated testing
- **Status**: ✅ Keep - Essential for quality

#### 13. **`data/`** - Data Storage
- **Purpose**: All data files (raw, processed, analyzer runs, etc.)
- **Status**: ✅ Keep - Contains actual data

#### 14. **`logs/`** - Log Files
- **Purpose**: System logs
- **Status**: ✅ Keep - Runtime logs

#### 15. **`docs/`** - Documentation
- **Purpose**: System documentation
- **Status**: ✅ Keep - Useful documentation

---

### ⚠️ CONFIGURATION FOLDERS (Review)

#### 16. **`.cursor/`** - Cursor IDE Config
- **Contents**: `prompt` file (workspace instructions)
- **Status**: ⚠️ Optional - Not used by system, just IDE context
- **Recommendation**: Keep if useful, delete if not

#### 17. **`.pytest_cache/`** - Pytest Cache
- **Contents**: Test cache (7.5 KB)
- **Status**: ⚠️ Auto-generated - Can be deleted, will be recreated
- **Recommendation**: Keep (speeds up tests)

---

## 📄 Root-Level Files Analysis

### ✅ CORE FILES (Keep)

1. **`requirements.txt`** - Python dependencies
2. **`README.md`** - Main project readme (outdated, needs update)
3. **`run_matrix_and_timetable.py`** - Matrix runner utility

### ❌ TEST/DEBUG FILES (Remove - 50+ files)

These are one-off test/debug scripts created during development. They can be safely removed:

#### Debug Scripts (Remove)
- `analyze_event_structure.py`
- `analyze_jsonl.py`
- `analyze_jsonl_events.py`
- `debug_actual_data_range.py`
- `debug_timezone_normalization.py`
- `diagnose_analyzer_time_usage.py`
- `diagnose_analyzer_timezone.py`
- `find_scheduler_events.py`
- `find_timezone_bug.py`
- `trace_analyzer_timezone.py`
- `determine_export_timezone.py`

#### Check Scripts (Remove)
- `check_1830_run.py`
- `check_1830_trigger.py`
- `check_actual_export_format.py`
- `check_analyzer_timezone_handling.py`
- `check_analyzer_timezone_runtime.py`
- `check_backend.py`
- `check_completed_run.py`
- `check_export_timezone.py`
- `check_latest_run_events.py`
- `check_latest_run_scheduler.py`
- `check_live_events.py`
- `check_missed_run.py`
- `check_missed_runs.py`
- `check_scheduler_config.py`
- `check_scheduler_events_detailed.py`

#### Verify Scripts (Remove)
- `verify_dashboard_events.py`
- `verify_fix_working.py`
- `verify_pipeline_analyzer_fix.py`
- `verify_scheduler.py`
- `verify_translator_output.py`

#### Test Scripts (Remove - Keep only if actively used)
- `test_analyzer_timezone_issue.py`
- `test_backend_health.py`
- `test_complete_scheduler_run.py`
- `test_endpoint_directly.py`
- `test_full_pipeline.py` ⚠️ **KEEP** - Comprehensive test
- `test_pipeline_data_range.py` ⚠️ **KEEP** - Useful test
- `test_pipeline_simple.py`
- `test_pipeline_start_timeout.py`
- `test_pipeline_stress.py` ⚠️ **KEEP** - Stress test
- `test_range_calculation_fix.py`
- `test_range_logic_bug.py`
- `test_scheduler_logging.py`
- `test_scheduler_quick.py`
- `test_start_error.py`
- `test_timezone_fix.py`
- `test_timezone_preservation.py`
- `test_tradovate_example.py`
- `test_translator_utc_fix.py`
- `test_with_data_date.py`
- `test_with_detailed_logging.py`
- `test_with_your_data.py`

#### Utility Scripts (Review)
- `cleanup_and_reset_jsonl.py` - ⚠️ **KEEP** - Useful utility
- `reduce_jsonl_file.py` - ⚠️ **KEEP** - Useful utility
- `restart_backend.py` - ⚠️ **KEEP** - Useful utility
- `fix_translator_utc.py` - ❌ **REMOVE** - One-time fix script
- `final_scheduler_test.py` - ❌ **REMOVE** - One-time test

#### Batch Files (Review)
- `test_pipeline_batch.bat` - ⚠️ **KEEP** - Test utility
- `TEST_TRADOVATE.bat` - ⚠️ **KEEP** - Test utility
- `QUICK_CHECK.bat` - ⚠️ **KEEP** - Quick check utility

#### PowerShell Scripts (Review)
- `check_duplicate_tasks.ps1` - ⚠️ **KEEP** - Useful utility
- `FIX_TASK_PERMISSIONS.ps1` - ⚠️ **KEEP** - Useful utility
- `monitor_tests.ps1` - ❌ **REMOVE** - One-time script

#### Data Files
- `data_merger.log` - ❌ **REMOVE** - Log file (can be regenerated)
- `test_stress_results.json` - ⚠️ **KEEP** - Test results (if useful)

### 📚 DOCUMENTATION FILES (Review)

#### ✅ KEEP - Important Documentation
- `README.md` - Main readme (needs update)
- `CURRENT_SYSTEM_EXPLANATION.md` - **CRITICAL** - System architecture
- `PROJECT_ANALYSIS.md` - Project overview
- `MASTER_MATRIX_AND_TIMETABLE_README.md` - Matrix documentation
- `SCHEDULER_ARCHITECTURE.md` - Scheduler docs
- `SCHEDULER_COMMUNICATION_FLOW.md` - Communication flow
- `OBSOLETE_CODE_REMOVED.md` - Cleanup history

#### ⚠️ REVIEW - Historical/Phase Documentation
These document past phases/cleanups - can be archived or removed:
- `PHASE1_SIMPLIFICATION_COMPLETE.md`
- `PHASE2_SIMPLIFICATION_COMPLETE.md`
- `PHASE3_SIMPLIFICATION_COMPLETE.md`
- `PHASE4_SIMPLIFICATION_COMPLETE.md`
- `PHASE5_SIMPLIFICATION_COMPLETE.md`
- `PHASE6_SIMPLIFICATION_COMPLETE.md`
- `CLEANUP_COMPLETED.md`
- `CLEANUP_PLAN.md`
- `CLEANUP_QUICK_REFERENCE.md`
- `CLEANUP_SNAPSHOT.md`
- `SIMPLIFICATION_COMPLETE_RUNDOWN.md`

#### ⚠️ REVIEW - Issue-Specific Documentation
These document specific issues/fixes - can be archived:
- `BACKEND_DISCONNECT_ANALYSIS.md`
- `DASHBOARD_BUTTON_FIX.md`
- `EVENT_STREAMING_ANALYSIS.md`
- `LIVE_EVENTS_FEED_EXPLANATION.md`
- `RESTART_BACKEND_REQUIRED.md`
- `SCHEDULER_EVENTS_WORKING.md`
- `SCHEDULER_PERMISSIONS_EXPLAINED.md`
- `SCHEDULER_PERMISSIONS_FIX.md`
- `SCHEDULER_PERMISSIONS_PROBLEM.md`
- `SCHEDULER_RANDOM_START_FIX.md`
- `SCHEDULER_REQUIREMENTS.md`
- `SCHEDULER_STATUS.md`
- `SCHEDULER_STATUS_CHECK.md`
- `TIMEZONE_BUG_FIXED.md`
- `TIMEZONE_FIX.md`
- `TIMEZONE_FIX_VERIFICATION.md`
- `VERIFY_MONITOR_WORKING.md`

#### ⚠️ REVIEW - How-To Documentation
Keep if still relevant:
- `HOW_TO_CHECK_ANALYZER_TIME.md`
- `HOW_TO_CONTROL_SCHEDULER.md`
- `HOW_TO_CREATE_TASK_LIMITED_PRIVILEGES.md`
- `HOW_TO_FIX_SCHEDULER_PERMISSIONS.md`

#### ⚠️ REVIEW - Analysis Documentation
- `COMPLEXITY_ANALYSIS.md`
- `MATRIX_OPTIMIZATIONS.md`
- `MATRIX_OVERVIEW_AND_ERRORS.md`
- `MATRIX_WALKTHROUGH.md`
- `PIPELINE_TEST_FEATURE.md`
- `PROBLEM_SUMMARY_FOR_CHATGPT.md`
- `REAL_TIME_INGESTION_IMPLEMENTATION.md`
- `STRESS_TEST_RESULTS.md`

#### ⚠️ REVIEW - Other Documentation
- `CREATE_ADMIN_SHORTCUT.md`
- `GIT_WORKFLOW.md`
- `STARTUP_SCRIPTS_UPDATE.md`

---

## 🎯 Cleanup Recommendations

### Immediate Actions (Safe to Delete)

#### 1. Remove Debug/Check Scripts (30+ files)
```bash
# Debug scripts
analyze_*.py
debug_*.py
diagnose_*.py
find_*.py
trace_*.py
determine_*.py

# Check scripts
check_*.py

# Verify scripts (except keep verify_scheduler.py if useful)
verify_*.py
```

#### 2. Remove One-Time Fix Scripts
- `fix_translator_utc.py`
- `final_scheduler_test.py`
- `monitor_tests.ps1`

#### 3. Remove Test Scripts (Keep only comprehensive ones)
Keep:
- `test_full_pipeline.py`
- `test_pipeline_data_range.py`
- `test_pipeline_stress.py`

Remove all other `test_*.py` files

#### 4. Remove Log Files
- `data_merger.log`

### Archive Actions (Move to `docs/archive/`)

#### 1. Historical Phase Documentation
- All `PHASE*_SIMPLIFICATION_COMPLETE.md` files
- `CLEANUP_*.md` files
- `SIMPLIFICATION_COMPLETE_RUNDOWN.md`

#### 2. Issue-Specific Documentation
- All `*_FIX.md`, `*_PROBLEM.md`, `*_ANALYSIS.md` files
- `SCHEDULER_*.md` files (except architecture docs)
- `TIMEZONE_*.md` files

### Update Actions

#### 1. Update Core Documentation
- `README.md` - Update to reflect current system
- `automation/SIMPLE_SCHEDULER.md` - Remove references to non-existent files
- `automation/README_SCHEDULER.md` - Remove references to non-existent files

#### 2. Fix Code References
- Update references from `START_DASHBOARD.bat` to `START_DASHBOARD_AS_ADMIN.bat`

---

## 📊 Summary Statistics

### Files by Category

| Category | Count | Status |
|----------|-------|--------|
| **Core System Files** | ~200 | ✅ Keep |
| **Test/Debug Scripts** | ~50 | ❌ Remove |
| **Documentation** | ~50 | ⚠️ Review/Archive |
| **Batch Scripts** | 25 | ✅ Keep |
| **Data Files** | 1000+ | ✅ Keep |
| **Log Files** | 100+ | ✅ Keep (runtime) |

### Estimated Cleanup

- **Files to Delete**: ~50 test/debug scripts
- **Files to Archive**: ~30 documentation files
- **Files to Update**: ~5 core documentation files
- **Space Saved**: ~500 KB (mostly Python scripts)

---

## 🔍 Key Findings

### 1. System is Well-Organized
- Core components are in proper folders
- Clear separation of concerns
- Good modular structure

### 2. Many One-Time Scripts
- ~50 test/debug scripts in root
- Created during development/debugging
- No longer needed for production

### 3. Documentation Bloat
- ~50 markdown files in root
- Many document past issues/fixes
- Should be archived or consolidated

### 4. Legacy Code Still Present
- `automation/run_pipeline_standalone.py` - Entry point for scheduled runs (uses orchestrator)
- Should be migrated to new orchestrator

### 5. Missing File References
- Code references `START_DASHBOARD.bat` but file doesn't exist
- Docs reference `simple_scheduler.py` but file doesn't exist

---

## ✅ Final Recommendations

### Priority 1: Remove Test/Debug Scripts
Delete ~50 test/debug scripts from root - they're one-time development tools

### Priority 2: Archive Historical Docs
Move ~30 historical/issue-specific docs to `docs/archive/` folder

### Priority 3: Update Core Documentation
- Update `README.md` with current system info
- Fix references to non-existent files
- Consolidate duplicate documentation

### Priority 4: Migrate Legacy Code
- ✅ COMPLETED: Dashboard now uses orchestrator system exclusively
- Remove legacy scheduler once migration complete

### Priority 5: Organize Remaining Files
- Move utility scripts to `tools/` folder
- Organize test files into `tests/` folder structure

---

## 📝 Notes

- **System Status**: ✅ Working and functional
- **Cleanup Impact**: Low risk - mostly removing unused scripts
- **Documentation**: Keep important docs, archive historical ones
- **Core System**: No changes needed - system is well-architected

---

**Next Steps**: 
1. Review this document
2. Approve cleanup actions
3. Execute cleanup in phases
4. Update documentation

