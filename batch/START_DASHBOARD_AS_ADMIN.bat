@echo off
REM ============================================================
REM   Pipeline Dashboard - Single Entry Point
REM   This batch file will request admin elevation automatically
REM ============================================================

REM Check if running as admin
net session >nul 2>&1
if %errorLevel% == 0 (
    REM Already running as admin, just start normally
    goto :start
) else (
    REM Not running as admin, request elevation
    echo Requesting administrator privileges...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

:start
REM Now running as admin - start the dashboard
REM Get absolute path to project root
cd /d "%~dp0\.."
set PROJECT_ROOT=%CD%

title Pipeline Dashboard (Admin Mode)

echo ============================================================
echo   Starting Pipeline Dashboard (Administrator Mode)
echo ============================================================
echo.
echo This is the ONLY batch file you need to start the dashboard.
echo It starts: Backend + Frontend + Scheduler Monitor
echo.
echo Project root: %PROJECT_ROOT%
echo ============================================================
echo.

REM Verify we're in the right place
if not exist "dashboard\backend\main.py" (
    echo ERROR: Backend file not found at %PROJECT_ROOT%\dashboard\backend\main.py
    pause
    exit /b 1
)

if not exist "dashboard\frontend\package.json" (
    echo ERROR: Frontend not found at %PROJECT_ROOT%\dashboard\frontend
    pause
    exit /b 1
)

REM Start backend in a new window with explicit path and PYTHONPATH
REM Using port 8000 (correct port from main.py) with auto-reload for development
echo [1/3] Starting backend in new window (port 8000, auto-reload enabled)...
start "Pipeline Backend (Admin)" cmd /k "cd /d "%PROJECT_ROOT%" && set PYTHONPATH=%PROJECT_ROOT% && python -m uvicorn dashboard.backend.main:app --reload --host 0.0.0.0 --port 8000"

REM Start scheduler monitor in a new window
echo [2/3] Starting scheduler monitor in new window...
timeout /t 2 /nobreak >nul
start "Scheduler Monitor" cmd /k "cd /d "%PROJECT_ROOT%" && batch\VIEW_SCHEDULER_TERMINAL_LIVE.bat"

REM Wait for backend to start
echo Waiting for backend to start...
timeout /t 5 /nobreak >nul

REM Open browser
echo Opening browser...
timeout /t 1 /nobreak >nul
start http://localhost:5173

REM Start frontend
echo.
echo [3/3] Starting frontend...
echo Frontend will run in this window
echo Backend is running in another window (port 8000)
echo Scheduler monitor is running in another window
echo.
echo ============================================================
echo   Dashboard Ready! (Administrator Mode)
echo ============================================================
echo.
echo Backend:  http://localhost:8000
echo Frontend: http://localhost:5173
echo API Docs: http://localhost:8000/docs
echo.
echo Browser should open automatically
echo Press Ctrl+C to stop frontend
echo (Close other windows to stop backend/scheduler monitor)
echo ============================================================
echo.

cd "%PROJECT_ROOT%\dashboard\frontend"
npm run dev

