@echo off
setlocal enabledelayedexpansion
title Master Matrix (Backend Visible)
color 0A

echo.
echo ================================================
echo   Master Matrix (Backend Visible)
echo ================================================
echo.

REM Change to project root
cd /d %~dp0..
set "PROJECT_ROOT=%CD%"

REM Create logs directory if it doesn't exist
if not exist "%PROJECT_ROOT%\logs" mkdir "%PROJECT_ROOT%\logs" 2>nul

REM Create master matrix log file if it doesn't exist
set "MASTER_MATRIX_LOG=%PROJECT_ROOT%\logs\master_matrix.log"
if not exist "%MASTER_MATRIX_LOG%" (
    echo [%date% %time%] Log file created by RUN_MASTER_MATRIX_VISIBLE.bat > "%MASTER_MATRIX_LOG%"
)

REM ================================================
REM Step 1: Check port availability
REM ================================================
echo [1/5] Checking port availability...
echo.

REM Check if port 8000 (backend) is already in use
REM Try PowerShell method first (provides process info), fallback to netstat
powershell -Command "$port8000 = Get-NetTCPConnection -LocalPort 8000 -ErrorAction SilentlyContinue; if ($port8000) { $process = Get-Process -Id $port8000.OwningProcess -ErrorAction SilentlyContinue; if ($process) { Write-Host 'ERROR: Port 8000 is already in use by process:' $process.ProcessName '(' $process.Id ')'; exit 1 } else { Write-Host 'ERROR: Port 8000 is already in use'; exit 1 } } else { exit 0 }" >nul 2>&1
if %ERRORLEVEL% equ 0 (
    REM PowerShell check passed (port not in use), continue
    goto :check_port_5174
)

REM PowerShell found port in use OR PowerShell command failed - check with netstat as fallback
netstat -an | findstr ":8000" | findstr "LISTENING" >nul 2>&1
if %ERRORLEVEL% equ 0 (
    echo.
    echo ================================================
    echo ERROR: Port 8000 (backend) is already in use!
    echo.
    echo Please close the application using port 8000
    echo or stop any existing backend instances.
    echo ================================================
    echo.
    pause
    exit /b 1
)

:check_port_5174

REM Check if port 5174 (frontend) is already in use
REM Try PowerShell method first (provides process info), fallback to netstat
powershell -Command "$port5174 = Get-NetTCPConnection -LocalPort 5174 -ErrorAction SilentlyContinue; if ($port5174) { $process = Get-Process -Id $port5174.OwningProcess -ErrorAction SilentlyContinue; if ($process) { Write-Host 'WARNING: Port 5174 is already in use by process:' $process.ProcessName '(' $process.Id ')'; exit 1 } else { Write-Host 'WARNING: Port 5174 is already in use'; exit 1 } } else { exit 0 }" >nul 2>&1
if %ERRORLEVEL% equ 0 (
    REM PowerShell check passed (port not in use), continue
    goto :ports_ok
)

REM PowerShell found port in use OR PowerShell command failed - check with netstat as fallback
netstat -an | findstr ":5174" | findstr "LISTENING" >nul 2>&1
if %ERRORLEVEL% equ 0 (
    echo.
    echo ================================================
    echo WARNING: Port 5174 (frontend) is already in use!
    echo.
    echo Frontend may not start properly.
    echo Consider closing the application using port 5174.
    echo ================================================
    echo.
    timeout /t 3 /nobreak >nul
)

:ports_ok

echo Ports are available.
echo.

REM ================================================
REM Step 2: Start backend
REM ================================================
echo [2/5] Starting backend...
start "Master Matrix Backend" cmd /k "cd /d %PROJECT_ROOT% && python -m uvicorn modules.dashboard.backend.main:app --host 0.0.0.0 --port 8000"
echo Backend process started. Waiting for it to initialize...
timeout /t 2 /nobreak >nul
echo.

REM ================================================
REM Step 3: Health check with retries
REM ================================================
echo [3/5] Waiting for backend to be ready...
set "BACKEND_READY=0"
set "MAX_ATTEMPTS=60"
set "ATTEMPT=0"

:health_check_loop
set /a ATTEMPT+=1

REM Show progress indicator
set /a PROGRESS=ATTEMPT*100/MAX_ATTEMPTS
if %ATTEMPT% leq 10 (
    echo Checking backend health... (attempt !ATTEMPT!/!MAX_ATTEMPTS!)
) else if %ATTEMPT% equ 20 (
    echo Still waiting for backend... (attempt !ATTEMPT!/!MAX_ATTEMPTS!)
) else if %ATTEMPT% equ 40 (
    echo Backend is taking longer than expected... (attempt !ATTEMPT!/!MAX_ATTEMPTS!)
)

REM Check health endpoint
powershell -Command "$response = try { Invoke-WebRequest -Uri 'http://localhost:8000/health' -Method GET -TimeoutSec 2 -UseBasicParsing -ErrorAction Stop; $response.StatusCode } catch { $null }; if ($response -eq 200) { exit 0 } else { exit 1 }" >nul 2>&1

if %ERRORLEVEL% equ 0 (
    echo Backend is ready!
    set "BACKEND_READY=1"
    goto :backend_ready
)

REM Check if we've exceeded max attempts
if %ATTEMPT% geq %MAX_ATTEMPTS% (
    echo.
    echo ================================================
    echo ERROR: Backend failed to start!
    echo.
    echo Backend did not respond after 30 seconds.
    echo.
    echo Troubleshooting:
    echo   1. Check the "Master Matrix Backend" window for errors
    echo   2. Verify Python and dependencies are installed
    echo   3. Check if port 8000 is accessible
    echo   4. Review logs in: %MASTER_MATRIX_LOG%
    echo ================================================
    echo.
    pause
    exit /b 1
)

REM Wait 500ms before retry
timeout /t 1 /nobreak >nul
goto :health_check_loop

:backend_ready
echo.

REM ================================================
REM Step 4: Open browser (only after backend ready)
REM ================================================
echo [4/5] Opening browser...
start http://localhost:5174
timeout /t 1 /nobreak >nul
echo.

REM ================================================
REM Step 5: Start frontend (only after backend ready)
REM ================================================
echo [5/5] Starting frontend...
echo Frontend will run in this window...
echo.

echo.
echo ================================================
echo Master Matrix is ready!
echo.
echo Backend:  http://localhost:8000 (running in separate window)
echo Frontend: http://localhost:5174 (output shown below)
echo.
echo Browser should open automatically.
echo.
echo Press Ctrl+C to stop frontend (backend will keep running).
echo Close the "Master Matrix Backend" window to stop backend.
echo ================================================
echo.

cd /d "%PROJECT_ROOT%\matrix_timetable_app\frontend"
npm run dev
