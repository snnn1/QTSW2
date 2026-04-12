@echo off
REM Setup Eligibility Builder - runs once daily at 18:00 (6 PM)
REM Run as Administrator

echo ================================================
echo   Eligibility Builder - Task Scheduler Setup
echo ================================================
echo.
echo This fixes the task to run ONCE daily at 18:00,
echo instead of every hour.
echo.

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: Run as Administrator
    echo Right-click and select "Run as administrator"
    pause
    exit /b 1
)

powershell -ExecutionPolicy Bypass -File "%~dp0..\automation\setup_eligibility_builder_task.ps1"
pause
