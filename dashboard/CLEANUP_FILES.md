# Files Safe to Delete

## Test Files (Diagnostic - Not Needed)
- ✅ `dashboard/backend/test_orchestrator.py` - Diagnostic test
- ✅ `dashboard/backend/test_startup.py` - Diagnostic test  
- ✅ `dashboard/backend/test_fastapi.py` - Diagnostic test

## Backup Files (Old Routers - Can Remove if New Ones Work)
- ✅ `dashboard/backend/routers/pipeline_old.py` - Backup of old router
- ✅ `dashboard/backend/routers/websocket_old.py` - Backup of old router

## Unused Main File
- ✅ `dashboard/backend/main_simplified.py` - Not referenced anywhere

## Redundant Documentation (Keep Only Essential)
These are historical/outdated docs that can be consolidated:

### Scheduler Docs (Many Similar Ones)
- `SCHEDULER_COMPARISON.md` - Comparison doc (keep if useful)
- `SCHEDULER_COMPLEXITY_ANALYSIS.md` - Historical analysis
- `SCHEDULER_EXPLAINED.md` - Old explanation
- `SCHEDULER_FIX.md` - Historical fix doc
- `SCHEDULER_FIXED.md` - Historical fix doc
- `SCHEDULER_STAGE_ANALYSIS.md` - Historical analysis
- `SCHEDULER_TRANSLATOR_ISSUE.md` - Historical issue doc

### Refactoring Docs (Historical)
- `REFACTORING_PLAN.md` - Historical planning doc
- `REFACTORING_PROGRESS.md` - Historical progress doc
- `REFACTORING_SUMMARY.md` - Historical summary
- `REFACTORING_FINAL_SUMMARY.md` - Historical summary
- `SIMPLIFICATION_PROGRESS.md` - Historical progress

### Other Historical Docs
- `ANALYZER_VERBOSE_FIX.md` - Historical fix
- `DRY_RUN_RESULTS.md` - Old test results
- `EVENTS_LOG_OPTIMIZATION.md` - Historical optimization
- `IMPROVEMENTS_APPLIED.md` - Historical improvements
- `MIGRATION_GUIDE.md` - Historical migration
- `MIGRATION_SUMMARY.md` - Historical summary
- `NINJATRADER_DISABLED.md` - Historical change
- `PIPELINE_DETECTION.md` - Historical doc
- `PIPELINE_TEST_SUCCESS.md` - Old test results
- `TRANSLATOR_FIX.md` - Historical fix
- `TEST_RESULTS.md` - Old test results

### Keep These (Current/Useful)
- `HOW_TO_START.md` - Current guide
- `QUICK_TEST.md` - Current guide
- `TROUBLESHOOTING.md` - Current guide
- `FIXED_START.md` - Current guide
- `START_GUIDE.md` - Current guide
- `ORCHESTRATOR_IMPLEMENTATION.md` - Current documentation
- `ORCHESTRATOR_ACTIVATED.md` - Current status
- `FULL_DESCRIPTION.md` - Current documentation
- `DASHBOARD_DESCRIPTION_SHORT.md` - Current documentation

## Files to Keep
- All `.py` files in `orchestrator/` - Core code
- All `.py` files in `routers/` (except `*_old.py`) - Active routers
- `main.py` - Active main file
- `config.py`, `models.py` - Active config/models
- Current documentation files

