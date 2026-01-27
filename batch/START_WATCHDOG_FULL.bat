@echo off
REM Start Watchdog UI with Backend (Full Stack)
REM Watchdog runs standalone - separate backend (8002) and frontend (5175)
REM Does NOT interfere with dashboard backend (8001) or frontend (5173)

cd /d "%~dp0\.."

title Watchdog UI (Full Stack)

echo ============================================================
echo   Starting Watchdog UI (Backend + Frontend)
echo ============================================================
echo.
echo This will start:
echo   - Watchdog Backend on http://localhost:8002 (standalone)
echo   - Watchdog Frontend on http://localhost:5175 (standalone)
echo.
echo NOTE: Dashboard (8001/5173) and Matrix (8000/5174) are unaffected
echo.
echo Press Ctrl+C to stop both services.
echo ============================================================
echo.

REM Start watchdog backend in a new window
echo [1/2] Starting watchdog backend in new window...
start "Watchdog Backend" cmd /k "cd /d %~dp0.. && python -m uvicorn modules.watchdog.backend.main:app --host 0.0.0.0 --port 8002"

REM Wait for backend to start
echo Waiting for watchdog backend to start...
timeout /t 5 /nobreak >nul

REM Start watchdog frontend
echo.
echo [2/2] Starting watchdog frontend...
echo Frontend will run in this window
echo Backend is running in another window
echo.

cd modules\dashboard\frontend

if not exist "package.json" (
    echo ERROR: Frontend not found at %CD%
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

REM Start frontend dev server (will run in this window)
echo Starting watchdog frontend dev server...
echo Frontend will be available at: http://localhost:5175
echo.
echo IMPORTANT: This will open ONLY the Watchdog UI at http://localhost:5175
echo Do NOT open dashboard (5173) or matrix (5174) - those are separate apps
echo.

REM Open ONLY watchdog URL after a delay (frontend needs time to start)
REM Wait 8 seconds for frontend to fully start, then open ONLY watchdog URL
echo Waiting 8 seconds for frontend to start, then opening watchdog...
timeout /t 8 /nobreak >nul
echo Opening watchdog UI at http://localhost:5175...
start "" "http://localhost:5175"

echo.
echo ============================================================
echo   Watchdog UI Starting!
echo ============================================================
echo.
echo Watchdog Backend:  http://localhost:8002
echo Watchdog Frontend: http://localhost:5175
echo.
echo Browser will open ONLY watchdog at http://localhost:5175 in 5 seconds
echo Press Ctrl+C to stop frontend
echo (Close other window to stop backend)
echo ============================================================
echo.

call npm run dev:watchdog
