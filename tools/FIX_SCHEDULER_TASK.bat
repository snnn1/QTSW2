@echo off
title Fix Scheduler Task Script Path
color 0B
echo.
echo ================================================
echo   Fixing Pipeline Runner Task
echo ================================================
echo.
echo This will update the task to use the correct
echo script path: automation\run_pipeline_standalone.py
echo.
echo NOTE: You must run this as Administrator!
echo.
pause

REM Check if running as administrator
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo.
    echo ERROR: This script must be run as Administrator!
    echo.
    echo Right-click this file and select "Run as administrator"
    echo.
    pause
    exit /b 1
)

cd /d "%~dp0\.."
set PROJECT_ROOT=%CD%

echo.
echo Running PowerShell fix script...
echo.

powershell.exe -ExecutionPolicy Bypass -File "%PROJECT_ROOT%\tools\fix_scheduler_task.ps1"

if errorlevel 1 (
    echo.
    echo ERROR: Fix failed!
    echo Check the error messages above.
    pause
    exit /b 1
)

echo.
echo ================================================
echo   Fix Complete!
echo ================================================
echo.
echo The task should now work correctly.
echo You can test it by running the task manually
echo in Task Scheduler.
echo.
pause



