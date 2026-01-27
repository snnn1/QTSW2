@echo off
REM Start Watchdog UI Frontend (Development Mode)
REM Watchdog frontend runs standalone on port 5175
REM Assumes watchdog backend is already running on port 8002

cd /d "%~dp0\.."

title Watchdog UI (Development)

echo ============================================================
echo   Starting Watchdog UI Frontend (Development Mode)
echo ============================================================
echo.
echo Make sure the watchdog backend is running on http://localhost:8002
echo.
echo Press Ctrl+C to stop the frontend.
echo ============================================================
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
echo Watchdog Frontend: http://localhost:5175
echo Watchdog Backend:  http://localhost:8002
echo.
echo Browser will open ONLY watchdog at http://localhost:5175 in 5 seconds
echo Press Ctrl+C to stop frontend
echo ============================================================
echo.

call npm run dev:watchdog
