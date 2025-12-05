# QTSW2 Project Cleanup Plan

## 🎯 Overview
This document identifies files and directories that can be safely cleaned up to reduce clutter and improve maintainability.

---

## ✅ Safe to Delete (High Confidence)

### 1. Duplicate/Unused Router Files
**Location**: `dashboard/backend/routers/`
- ❌ `pipeline_new.py` - Not imported in main.py (uses `pipeline.py`)
- ❌ `websocket_new.py` - Not imported in main.py (uses `websocket.py`)

**Verification**: `main.py` imports from `pipeline` and `websocket`, not `*_new` versions

---

### 2. Test/Diagnostic Files (Development Only)
**Location**: `dashboard/backend/`
- ❌ `test_startup.py` - Diagnostic test file
- ❌ `test_fastapi_startup.py` - Diagnostic test file
- ❌ `test_pipeline_run.py` - Diagnostic test file
- ❌ `test_translator_direct.py` - Diagnostic test file
- ❌ `test_orchestrator.py` - Diagnostic test file (if exists)
- ❌ `test_orchestrator_startup.py` - Diagnostic test file (if exists)
- ❌ `continuous_test.py` - Continuous testing script
- ❌ `diagnostic.py` - Diagnostic script
- ❌ `test_startup_simple.py` - Diagnostic test file (if exists)
- ❌ `run_with_debug.py` - Debug runner (if exists)

**Note**: Keep actual test files in `tests/` directory

---

### 3. Unused Main Files
**Location**: `dashboard/backend/`
- ❌ `main_simplified.py` - Not referenced anywhere (per CLEANUP_FILES.md)

---

### 4. Old Log Files (Keep Recent Only)
**Location**: `automation/logs/`
- ❌ **Hundreds of old pipeline log files** (e.g., `pipeline_20251101_*.log`)
- ✅ **Keep**: Last 30 days of logs
- ✅ **Keep**: `events/` directory structure

**Recommendation**: Archive or delete logs older than 30 days

---

### 5. Historical Documentation Files
**Location**: `dashboard/`

#### Scheduler Documentation (Historical)
- ❌ `SCHEDULER_COMPARISON.md` - Historical comparison
- ❌ `SCHEDULER_COMPLEXITY_ANALYSIS.md` - Historical analysis
- ❌ `SCHEDULER_EXPLAINED.md` - Old explanation
- ❌ `SCHEDULER_FIX.md` - Historical fix doc
- ❌ `SCHEDULER_FIXED.md` - Historical fix doc
- ❌ `SCHEDULER_STAGE_ANALYSIS.md` - Historical analysis
- ❌ `SCHEDULER_TRANSLATOR_ISSUE.md` - Historical issue doc

#### Refactoring Documentation (Historical)
- ❌ `REFACTORING_PLAN.md` - Historical planning
- ❌ `REFACTORING_PROGRESS.md` - Historical progress
- ❌ `REFACTORING_SUMMARY.md` - Historical summary
- ❌ `REFACTORING_FINAL_SUMMARY.md` - Historical summary
- ❌ `REFACTORING_COMPLETE.md` - Historical completion doc
- ❌ `SIMPLIFICATION_PROGRESS.md` - Historical progress

#### Migration Documentation (Historical)
- ❌ `MIGRATION_GUIDE.md` - Historical migration
- ❌ `MIGRATION_SUMMARY.md` - Historical summary

#### Other Historical Docs
- ❌ `ANALYZER_VERBOSE_FIX.md` - Historical fix
- ❌ `DRY_RUN_RESULTS.md` - Old test results
- ❌ `EVENTS_LOG_OPTIMIZATION.md` - Historical optimization
- ❌ `IMPROVEMENTS_APPLIED.md` - Historical improvements
- ❌ `NINJATRADER_DISABLED.md` - Historical change
- ❌ `PIPELINE_DETECTION.md` - Historical doc
- ❌ `PIPELINE_TEST_SUCCESS.md` - Old test results
- ❌ `TRANSLATOR_FIX.md` - Historical fix
- ❌ `TEST_RESULTS.md` - Old test results (duplicate in backend/)
- ❌ `BATCH_CLEANUP_ANALYSIS.md` - Historical cleanup doc
- ❌ `CLEANUP_FILES.md` - Historical cleanup doc
- ❌ `CLEANUP_COMPLETE.md` - Historical cleanup doc
- ❌ `COMPLEXITY_ANALYSIS.md` - Historical analysis
- ❌ `APPROACH_COMPARISON.md` - Historical comparison
- ❌ `IMPLEMENTATION_VS_DESCRIPTION.md` - Historical doc
- ❌ `WHAT_NEXT.md` - Old planning doc

#### Keep These (Current/Useful)
- ✅ `HOW_TO_START.md` - Current guide
- ✅ `QUICK_START.md` - Current guide
- ✅ `QUICK_TEST.md` - Current guide
- ✅ `QUICK_TEST_GUIDE.md` - Current guide
- ✅ `TROUBLESHOOTING.md` - Current guide
- ✅ `SETUP.md` - Current setup guide
- ✅ `README.md` - Main readme
- ✅ `USER_GUIDE.md` - Current user guide
- ✅ `DASHBOARD_DESCRIPTION_SHORT.md` - Current documentation
- ✅ `DASHBOARD_OVERVIEW.md` - Current overview
- ✅ `HOW_IT_WORKS.md` - Current explanation
- ✅ `HOW_ORCHESTRATOR_WORKS.md` - Current explanation
- ✅ `PIPELINE_WALKTHROUGH.md` - Current walkthrough
- ✅ `FULL_DESCRIPTION.md` - Current documentation

---

### 6. Backend Documentation Files
**Location**: `dashboard/backend/`
- ❌ `NEXT_STEPS.md` - Old planning doc
- ❌ `TEST_RESULTS.md` - Old test results
- ❌ `ORCHESTRATOR_IMPLEMENTATION.md` - Can archive if outdated
- ❌ `ORCHESTRATOR_ACTIVATED.md` - Can archive if outdated

---

### 7. Root Level Cleanup Docs
**Location**: Root
- ❌ `CLEANUP_REPORT.md` - Historical cleanup report
- ❌ `CLEANUP_SUMMARY.md` - Historical cleanup summary
- ❌ `BATCH_FILES_FIXED.md` - Historical fix doc
- ❌ `PROJECT_ANALYSIS.md` - Temporary analysis (can keep or delete)

---

### 8. Cache and Build Directories
**Location**: Various
- ❌ `__pycache__/` directories (can be regenerated)
- ❌ `dashboard/frontend/dist/` - Build output (can be regenerated)
- ❌ `*.pyc` files (can be regenerated)

**Note**: These are typically in `.gitignore` but may exist locally

---

## ⚠️ Review Before Deleting (Medium Confidence)

### 9. Duplicate Batch Files
**Location**: `batch/` and `dashboard/`
- ⚠️ `dashboard/START_ORCHESTRATOR.bat` vs `batch/START_ORCHESTRATOR.bat`
- ⚠️ `dashboard/START_ORCHESTRATOR.ps1` vs similar in batch/
- ⚠️ `dashboard/cleanup.bat` vs `dashboard/cleanup_batches.bat`

**Action**: Check which ones are actually used/referenced

---

### 10. Event Log Files
**Location**: `automation/logs/events/`
- ⚠️ **497 JSONL files** - May contain important historical data
- **Recommendation**: Archive old event logs (older than 90 days) instead of deleting

---

### 11. Data Files
**Location**: `data/`
- ⚠️ **720 files** (628 txt, 92 json)
- **Action**: Review if these are test data or production data
- **Recommendation**: Don't delete without user confirmation

---

## 📊 Cleanup Statistics

### Files to Delete (High Confidence)
- **Router files**: 2 files
- **Test files**: ~8-10 files
- **Historical docs**: ~30+ files
- **Old logs**: ~200+ files (keep last 30 days)
- **Total**: ~240+ files

### Estimated Space Savings
- Documentation: ~500KB - 1MB
- Log files: ~50-100MB (depending on size)
- Test files: ~50-100KB
- **Total**: ~50-100MB

---

## 🚀 Cleanup Script

### Manual Cleanup Steps

1. **Delete duplicate router files**:
   ```bash
   del dashboard\backend\routers\pipeline_new.py
   del dashboard\backend\routers\websocket_new.py
   ```

2. **Delete test files**:
   ```bash
   del dashboard\backend\test_*.py
   del dashboard\backend\continuous_test.py
   del dashboard\backend\diagnostic.py
   del dashboard\backend\main_simplified.py
   ```

3. **Delete historical documentation** (see list above)

4. **Clean old logs** (keep last 30 days):
   ```powershell
   # PowerShell script to delete logs older than 30 days
   Get-ChildItem automation\logs\pipeline_*.log | 
     Where-Object {$_.LastWriteTime -lt (Get-Date).AddDays(-30)} | 
     Remove-Item
   ```

---

## ✅ Files to Keep

### Essential Documentation
- `README.md` (root)
- `dashboard/README.md`
- `dashboard/SETUP.md`
- `dashboard/HOW_TO_START.md`
- `dashboard/QUICK_START.md`
- `dashboard/TROUBLESHOOTING.md`
- `dashboard/USER_GUIDE.md`
- `dashboard/DASHBOARD_DESCRIPTION_SHORT.md`
- `automation/README_SCHEDULER.md`
- `automation/HOW_TO_RUN_SCHEDULER.md`

### Essential Code
- All files in `dashboard/backend/orchestrator/`
- All active router files (not `*_new.py`)
- All files in `dashboard/backend/routers/` (except `*_new.py`)
- `dashboard/backend/main.py`
- All frontend source files

### Essential Data
- Recent logs (last 30 days)
- Event log structure
- Configuration files

---

## 📝 Post-Cleanup Actions

1. **Update `.gitignore`** to exclude:
   - Old log files
   - Cache directories
   - Build outputs

2. **Create archive** for deleted historical docs (optional):
   - Create `docs/archive/` directory
   - Move historical docs there instead of deleting

3. **Document cleanup** in a changelog or commit message

---

## ⚡ Quick Cleanup Commands

### Windows PowerShell (Run from project root)

```powershell
# Delete duplicate router files
Remove-Item dashboard\backend\routers\pipeline_new.py -ErrorAction SilentlyContinue
Remove-Item dashboard\backend\routers\websocket_new.py -ErrorAction SilentlyContinue

# Delete test files
Remove-Item dashboard\backend\test_*.py -ErrorAction SilentlyContinue
Remove-Item dashboard\backend\continuous_test.py -ErrorAction SilentlyContinue
Remove-Item dashboard\backend\diagnostic.py -ErrorAction SilentlyContinue
Remove-Item dashboard\backend\main_simplified.py -ErrorAction SilentlyContinue

# Delete old logs (older than 30 days)
Get-ChildItem automation\logs\pipeline_*.log | 
  Where-Object {$_.LastWriteTime -lt (Get-Date).AddDays(-30)} | 
  Remove-Item

# Clean Python cache
Get-ChildItem -Path . -Include __pycache__ -Recurse -Directory | Remove-Item -Recurse -Force
Get-ChildItem -Path . -Include *.pyc -Recurse -File | Remove-Item -Force
```

---

## 🎯 Priority Order

1. **High Priority** (Safe, immediate cleanup):
   - Duplicate router files
   - Test/diagnostic files
   - Unused main files

2. **Medium Priority** (Review first):
   - Historical documentation
   - Duplicate batch files

3. **Low Priority** (Archive instead of delete):
   - Old log files
   - Event log files

---

## ⚠️ Warnings

- **Backup before cleanup**: Consider creating a backup or commit before deleting
- **Test after cleanup**: Verify the application still works after cleanup
- **Review event logs**: Don't delete event logs without reviewing their importance
- **Keep recent logs**: Always keep recent logs for debugging

