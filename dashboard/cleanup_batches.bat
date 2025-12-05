@echo off
REM Cleanup obsolete batch files that are no longer needed
REM These have been replaced by the orchestrator system

echo ================================================
echo   Cleaning Up Obsolete Batch Files
echo ================================================
echo.

set DELETED=0
set FAILED=0

REM Old scheduler files
if exist "automation\start_simple_scheduler.bat" (
    echo Deleting: automation\start_simple_scheduler.bat
    del /f "automation\start_simple_scheduler.bat" 2>nul
    if %errorlevel% equ 0 (set /a DELETED+=1) else (set /a FAILED+=1)
)

if exist "automation\setup_task_scheduler_simple.bat" (
    echo Deleting: automation\setup_task_scheduler_simple.bat
    del /f "automation\setup_task_scheduler_simple.bat" 2>nul
    if %errorlevel% equ 0 (set /a DELETED+=1) else (set /a FAILED+=1)
)

if exist "batch\START_SCHEDULER.bat" (
    echo Deleting: batch\START_SCHEDULER.bat
    del /f "batch\START_SCHEDULER.bat" 2>nul
    if %errorlevel% equ 0 (set /a DELETED+=1) else (set /a FAILED+=1)
)

REM Old backend/dashboard files
if exist "dashboard\START_BACKEND.bat" (
    echo Deleting: dashboard\START_BACKEND.bat
    del /f "dashboard\START_BACKEND.bat" 2>nul
    if %errorlevel% equ 0 (set /a DELETED+=1) else (set /a FAILED+=1)
)

if exist "dashboard\RUN_DIAGNOSTIC.bat" (
    echo Deleting: dashboard\RUN_DIAGNOSTIC.bat
    del /f "dashboard\RUN_DIAGNOSTIC.bat" 2>nul
    if %errorlevel% equ 0 (set /a DELETED+=1) else (set /a FAILED+=1)
)

if exist "dashboard\backend\START_AND_SHOW_ERRORS.bat" (
    echo Deleting: dashboard\backend\START_AND_SHOW_ERRORS.bat
    del /f "dashboard\backend\START_AND_SHOW_ERRORS.bat" 2>nul
    if %errorlevel% equ 0 (set /a DELETED+=1) else (set /a FAILED+=1)
)

REM Old conductor log file
if exist "batch\TAIL_CONDUCTOR_LOG.bat" (
    echo Deleting: batch\TAIL_CONDUCTOR_LOG.bat
    del /f "batch\TAIL_CONDUCTOR_LOG.bat" 2>nul
    if %errorlevel% equ 0 (set /a DELETED+=1) else (set /a FAILED+=1)
)

echo.
echo ================================================
echo   Cleanup Complete
echo ================================================
echo.
echo Files deleted: %DELETED%
if %FAILED% gtr 0 (
    echo Files failed to delete: %FAILED%
)
echo.
echo These files have been replaced by the orchestrator system.
echo Use dashboard\START_ORCHESTRATOR.bat to start the backend.
echo.
pause

