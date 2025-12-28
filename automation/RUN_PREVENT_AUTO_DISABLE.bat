@echo off
REM Batch wrapper to run prevent_scheduler_auto_disable.ps1 as Administrator
REM This makes it easier to run the PowerShell script

echo ================================================
echo   Prevent Task Auto-Disable - Configuration
echo ================================================
echo.

REM Check if running as Administrator
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: This script must be run as Administrator
    echo.
    echo To fix:
    echo   1. Right-click this file (RUN_PREVENT_AUTO_DISABLE.bat)
    echo   2. Select "Run as administrator"
    echo   3. Click Yes when prompted
    echo.
    pause
    exit /b 1
)

echo Running PowerShell script...
echo.

REM Run the PowerShell script
powershell.exe -ExecutionPolicy Bypass -File "%~dp0prevent_scheduler_auto_disable.ps1"

if %errorLevel% equ 0 (
    echo.
    echo ================================================
    echo   Configuration Complete!
    echo ================================================
) else (
    echo.
    echo ================================================
    echo   Configuration Failed
    echo ================================================
)

echo.
pause







