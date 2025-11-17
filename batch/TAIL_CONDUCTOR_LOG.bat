@echo off
REM Tail the most recent Pipeline Conductor log file
REM This is called from START_DASHBOARD.bat

cd /d %~dp0..

title Pipeline Conductor - Debug Log
color 0A

echo ========================================
echo Pipeline Conductor - Debug Log
echo ========================================
echo.

REM Wait a moment for log file to be created if conductor just started
timeout /t 2 /nobreak >nul

REM Find most recent log file
set LATEST_LOG=
for /f "delims=" %%i in ('dir /b /o-d "automation\logs\pipeline_*.log" 2^>nul') do (
    set LATEST_LOG=%%i
    goto :found
)

:found
if defined LATEST_LOG (
    echo Log file: automation\logs\%LATEST_LOG%
    echo.
    echo This window shows real-time logs from the conductor.
    echo Press Ctrl+C to close this window.
    echo.
    echo ========================================
    echo.
    REM Tail the log file using PowerShell
    powershell -Command "Get-Content 'automation\logs\%LATEST_LOG%' -Wait -Tail 100"
) else (
    echo No log file found yet.
    echo.
    echo Waiting for conductor to create log file...
    echo This window will show logs when conductor runs.
    echo.
    REM Keep checking for log file
    :wait_loop
    timeout /t 2 /nobreak >nul
    for /f "delims=" %%i in ('dir /b /o-d "automation\logs\pipeline_*.log" 2^>nul') do (
        set LATEST_LOG=%%i
        goto :found
    )
    goto :wait_loop
)



