@echo off
REM Start Watchdog UI Frontend (Development Mode)
REM This starts the frontend dev server and opens the browser to /watchdog

cd /d "%~dp0\.."

title Watchdog UI (Development)

echo ============================================================
echo   Starting Watchdog UI Frontend (Development Mode)
echo ============================================================
echo.
echo Make sure the backend is running on http://localhost:8001
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

REM Wait a moment for server to start, then open browser
echo Starting frontend dev server...
echo.
echo Frontend will be available at: http://localhost:5173/watchdog
echo.
echo Opening browser in 3 seconds...
timeout /t 3 /nobreak >nul
start http://localhost:5173/watchdog

echo.
echo ============================================================
echo   Watchdog UI Ready!
echo ============================================================
echo.
echo Frontend: http://localhost:5173/watchdog
echo Backend:  http://localhost:8001
echo.
echo Press Ctrl+C to stop frontend
echo ============================================================
echo.

call npm run dev
