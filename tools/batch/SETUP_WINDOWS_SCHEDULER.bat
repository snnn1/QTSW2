@echo off
title Setup Windows Task Scheduler for Pipeline
color 0B
echo.
echo ================================================
echo   Setting Up Windows Task Scheduler
echo   (Pipeline Runner - Every 15 Minutes)
echo ================================================
echo.
echo This will create a Windows scheduled task that:
echo   - Runs at :00, :15, :30, :45 of every hour
echo   - Runs at system startup
echo.
echo NOTE: You must run this as Administrator!
echo.
pause

REM Get absolute path to project root
cd /d "%~dp0\.."
set PROJECT_ROOT=%CD%

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

echo.
echo Running PowerShell setup script...
echo Project root: %PROJECT_ROOT%
echo.

REM Run PowerShell script
powershell.exe -ExecutionPolicy Bypass -File "%PROJECT_ROOT%\automation\setup_task_scheduler.ps1"

if errorlevel 1 (
    echo.
    echo ERROR: Setup failed!
    echo Check the error messages above.
    pause
    exit /b 1
)

echo.
echo ================================================
echo   Setup Complete!
echo ================================================
echo.
echo Next steps:
echo   1. Open Task Scheduler to verify the task
echo   2. The task should show 5 triggers
echo   3. You can test by right-clicking and selecting "Run"
echo   4. Use the dashboard to enable/disable automation
echo.
pause




















