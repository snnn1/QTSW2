@echo off
REM Start Watchdog UI with Backend (Full Stack)
REM Starts both backend and frontend, opens browser to /watchdog

cd /d "%~dp0\.."

title Watchdog UI (Full Stack)

echo ============================================================
echo   Starting Watchdog UI (Backend + Frontend)
echo ============================================================
echo.
echo This will start:
echo   - Backend on http://localhost:8001
echo   - Frontend on http://localhost:5173
echo.
echo Press Ctrl+C to stop both services.
echo ============================================================
echo.

REM Start backend in a new window
echo [1/2] Starting backend in new window...
start "Watchdog Backend" cmd /k "cd /d %~dp0.. && python -m uvicorn modules.dashboard.backend.main:app --host 0.0.0.0 --port 8001"

REM Wait for backend to start
echo Waiting for backend to start...
timeout /t 5 /nobreak >nul

REM Start frontend
echo.
echo [2/2] Starting frontend...
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

REM Wait a moment, then open browser
echo Opening browser to Watchdog UI...
timeout /t 3 /nobreak >nul
start http://localhost:5173/watchdog

echo.
echo ============================================================
echo   Watchdog UI Ready!
echo ============================================================
echo.
echo Backend:  http://localhost:8001
echo Frontend: http://localhost:5173/watchdog
echo.
echo Browser should open automatically
echo Press Ctrl+C to stop frontend
echo (Close other window to stop backend)
echo ============================================================
echo.

call npm run dev
