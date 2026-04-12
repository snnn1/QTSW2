@echo off
REM Start Standalone Watchdog App (modules/watchdog/frontend)
REM Backend: port 8002 | Frontend: port 5175
REM Uses the standalone watchdog frontend, NOT the dashboard's AppWatchdog

cd /d "%~dp0\.."

title Watchdog Standalone

echo ============================================================
echo   Starting Standalone Watchdog App
echo ============================================================
echo.
echo This will start:
echo   - Watchdog Backend on http://localhost:8002
echo   - Standalone Watchdog Frontend on http://localhost:5175
echo.
echo NOTE: Uses modules/watchdog/frontend (standalone app)
echo       NOT the dashboard's watchdog (modules/dashboard/frontend)
echo.
echo Press Ctrl+C to stop frontend. Close other window to stop backend.
echo ============================================================
echo.

REM Start watchdog backend in a new window
echo [1/2] Starting watchdog backend in new window...
start "Watchdog Backend" cmd /k "cd /d %~dp0.. && python -m uvicorn modules.watchdog.backend.main:app --host 0.0.0.0 --port 8002"

REM Wait for backend to start
echo Waiting for backend to start...
timeout /t 5 /nobreak >nul

REM Start standalone watchdog frontend
echo.
echo [2/2] Starting standalone watchdog frontend...
echo.

cd modules\watchdog\frontend

if not exist "package.json" (
    echo ERROR: Standalone watchdog frontend not found at %CD%
    pause
    exit /b 1
)

REM Check if node_modules exists, if not, install dependencies
if not exist "node_modules" (
    echo Installing dependencies...
    call npm install
    if errorlevel 1 (
        echo ERROR: npm install failed
        pause
        exit /b 1
    )
    echo.
)

echo Waiting 8 seconds for frontend to start, then opening browser...
timeout /t 8 /nobreak >nul
start "" "http://localhost:5175"

echo.
echo ============================================================
echo   Standalone Watchdog Starting!
echo ============================================================
echo.
echo Backend:  http://localhost:8002
echo Frontend: http://localhost:5175
echo.
echo Press Ctrl+C to stop frontend
echo ============================================================
echo.

call npm run dev
