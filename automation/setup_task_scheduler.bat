@echo off
REM Batch script to set up Windows Task Scheduler for pipeline runner
REM Run this script as Administrator

echo ================================================
echo   Pipeline Runner - Task Scheduler Setup
echo ================================================
echo.

REM Configuration
set TASK_NAME=Pipeline Runner
set PYTHON_CMD=C:\Users\jakej\AppData\Local\Programs\Python\Python313\python.exe
set WORKING_DIR=%~dp0..
set ARGUMENTS=-m automation.run_pipeline_standalone
set DESCRIPTION=Runs data pipeline every 15 minutes at :00, :15, :30, :45

echo Task Name: %TASK_NAME%
echo Python: %PYTHON_CMD%
echo Working Directory: %WORKING_DIR%
echo Arguments: %ARGUMENTS%
echo.

REM Check if running as Administrator
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: This script must be run as Administrator
    echo Right-click and select "Run as administrator"
    pause
    exit /b 1
)

REM Check if task already exists
schtasks /query /tn "%TASK_NAME%" >nul 2>&1
if %errorLevel% equ 0 (
    echo Task '%TASK_NAME%' already exists.
    set /p RESPONSE="Do you want to remove and recreate it? (Y/N): "
    if /i "%RESPONSE%"=="Y" (
        schtasks /delete /tn "%TASK_NAME%" /f >nul 2>&1
        echo Existing task removed.
    ) else (
        echo Exiting. Task not modified.
        pause
        exit /b 0
    )
)

REM Verify Python is available
echo Verifying Python installation...
%PYTHON_CMD% --version >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: Python not found at: %PYTHON_CMD%
    echo Please specify the full path to python.exe
    pause
    exit /b 1
)
for /f "tokens=*" %%i in ('%PYTHON_CMD% --version 2^>^&1') do set PYTHON_VERSION=%%i
echo Python found: %PYTHON_VERSION%

REM Verify working directory exists
if not exist "%WORKING_DIR%" (
    echo ERROR: Working directory does not exist: %WORKING_DIR%
    pause
    exit /b 1
)
echo Working directory exists: %WORKING_DIR%
echo.

REM Calculate start time (next 15-minute mark)
for /f "tokens=1-3 delims=: " %%a in ('echo %time%') do (
    set CURRENT_HOUR=%%a
    set CURRENT_MIN=%%b
    set CURRENT_SEC=%%c
)

REM Remove leading space from hour if present
set CURRENT_HOUR=%CURRENT_HOUR: =%

REM Round down to nearest 15-minute mark
set /a ROUNDED_MIN=(%CURRENT_MIN% / 15) * 15
set /a START_MIN=%ROUNDED_MIN%

REM Format start time (HH:MM)
if %START_MIN% LSS 10 set START_MIN=0%START_MIN%
if %CURRENT_HOUR% LSS 10 set CURRENT_HOUR=0%CURRENT_HOUR%

set START_TIME=%CURRENT_HOUR%:%START_MIN%

echo Creating scheduled task...
echo   Name: %TASK_NAME%
echo   Schedule: Every 15 minutes starting at %START_TIME%
echo   Command: %PYTHON_CMD% %ARGUMENTS%
echo   Working Directory: %WORKING_DIR%
echo.

REM Create the task using schtasks
REM Note: schtasks doesn't support repetition intervals directly in one command
REM We'll create a daily task that repeats every 15 minutes

schtasks /create ^
    /tn "%TASK_NAME%" ^
    /tr "%PYTHON_CMD% %ARGUMENTS%" ^
    /sc daily ^
    /st %START_TIME% ^
    /ri 15 ^
    /du 1440 ^
    /f ^
    /rl HIGHEST ^
    /ru "%USERDOMAIN%\%USERNAME%" ^
    /rp "" ^
    /it ^
    /sd %date:~-4,4%-%date:~-10,2%-%date:~-7,2% ^
    /ed 12/31/2099

if %errorLevel% equ 0 (
    echo.
    echo ================================================
    echo   Task '%TASK_NAME%' created successfully!
    echo ================================================
    echo.
    echo Task Details:
    echo   Name: %TASK_NAME%
    echo   Schedule: Every 15 minutes starting at %START_TIME%
    echo   Command: %PYTHON_CMD% %ARGUMENTS%
    echo   Working Directory: %WORKING_DIR%
    echo.
    echo Next Steps:
    echo   1. Open Task Scheduler to verify the task
    echo   2. Right-click the task and select 'Run' to test
    echo   3. Check 'Last Run Result' - should be 0x0 (success)
    echo   4. Monitor via dashboard or log files
    echo.
) else (
    echo.
    echo ERROR: Failed to create task
    echo Make sure you're running as Administrator
    echo.
    pause
    exit /b 1
)

REM Set working directory for the task (requires additional command)
REM Note: schtasks /create doesn't support working directory directly
REM We need to modify the task after creation or use a wrapper script
echo Note: Working directory may need to be set manually in Task Scheduler
echo       or use the PowerShell script for full automation
echo.

echo ================================================
echo   Setup Complete
echo ================================================
echo.
pause
























