@echo off
echo Cleaning up unneeded files...
echo.

cd /d "%~dp0"

REM Test files
echo Removing test files...
if exist "backend\test_orchestrator.py" del "backend\test_orchestrator.py"
if exist "backend\test_startup.py" del "backend\test_startup.py"
if exist "backend\test_fastapi.py" del "backend\test_fastapi.py"

REM Backup router files
echo Removing backup router files...
if exist "backend\routers\pipeline_old.py" del "backend\routers\pipeline_old.py"
if exist "backend\routers\websocket_old.py" del "backend\routers\websocket_old.py"

REM Unused main file
echo Removing unused main file...
if exist "backend\main_simplified.py" del "backend\main_simplified.py"

REM Historical scheduler docs
echo Removing historical scheduler docs...
if exist "SCHEDULER_COMPLEXITY_ANALYSIS.md" del "SCHEDULER_COMPLEXITY_ANALYSIS.md"
if exist "SCHEDULER_EXPLAINED.md" del "SCHEDULER_EXPLAINED.md"
if exist "SCHEDULER_FIX.md" del "SCHEDULER_FIX.md"
if exist "SCHEDULER_FIXED.md" del "SCHEDULER_FIXED.md"
if exist "SCHEDULER_STAGE_ANALYSIS.md" del "SCHEDULER_STAGE_ANALYSIS.md"
if exist "SCHEDULER_TRANSLATOR_ISSUE.md" del "SCHEDULER_TRANSLATOR_ISSUE.md"

REM Historical refactoring docs
echo Removing historical refactoring docs...
if exist "REFACTORING_PLAN.md" del "REFACTORING_PLAN.md"
if exist "REFACTORING_PROGRESS.md" del "REFACTORING_PROGRESS.md"
if exist "REFACTORING_SUMMARY.md" del "REFACTORING_SUMMARY.md"
if exist "REFACTORING_FINAL_SUMMARY.md" del "REFACTORING_FINAL_SUMMARY.md"
if exist "SIMPLIFICATION_PROGRESS.md" del "SIMPLIFICATION_PROGRESS.md"

REM Other historical docs
echo Removing other historical docs...
if exist "ANALYZER_VERBOSE_FIX.md" del "ANALYZER_VERBOSE_FIX.md"
if exist "DRY_RUN_RESULTS.md" del "DRY_RUN_RESULTS.md"
if exist "EVENTS_LOG_OPTIMIZATION.md" del "EVENTS_LOG_OPTIMIZATION.md"
if exist "IMPROVEMENTS_APPLIED.md" del "IMPROVEMENTS_APPLIED.md"
if exist "MIGRATION_GUIDE.md" del "MIGRATION_GUIDE.md"
if exist "MIGRATION_SUMMARY.md" del "MIGRATION_SUMMARY.md"
if exist "NINJATRADER_DISABLED.md" del "NINJATRADER_DISABLED.md"
if exist "PIPELINE_DETECTION.md" del "PIPELINE_DETECTION.md"
if exist "PIPELINE_TEST_SUCCESS.md" del "PIPELINE_TEST_SUCCESS.md"
if exist "TRANSLATOR_FIX.md" del "TRANSLATOR_FIX.md"
if exist "TEST_RESULTS.md" del "TEST_RESULTS.md"

echo.
echo Cleanup complete!
echo.
echo Files removed:
echo   - Test files (3)
echo   - Backup router files (2)
echo   - Unused main file (1)
echo   - Historical documentation (16)
echo.
echo Total: 22 files removed
echo.
pause

