@echo off
REM Simple Watchdog Starter - Starts backend and frontend, opens browser

REM Change to project root directory
cd /d "%~dp0\.."

REM Verify we're in the right directory
if not exist "modules\watchdog\backend\main.py" (
    echo ERROR: Cannot find watchdog backend files
    echo Current directory: %CD%
    echo Expected: QTSW2 project root
    pause
    exit /b 1
)

title Watchdog

echo ============================================================
echo   Starting Watchdog
echo ============================================================
echo.
echo Project directory: %CD%
echo.

REM Start backend in new window (use full path to ensure correct directory)
echo [1/2] Starting backend...
start "Watchdog Backend" cmd /k "cd /d %CD% && python -m uvicorn modules.watchdog.backend.main:app --host 0.0.0.0 --port 8002"

REM Wait for backend
timeout /t 3 /nobreak >nul

REM Start frontend
echo [2/2] Starting frontend...
cd /d "%CD%\modules\watchdog\frontend"

if not exist "package.json" (
    echo ERROR: Frontend not found at %CD%
    pause
    exit /b 1
)

if not exist "node_modules" (
    echo Installing dependencies...
    call npm install
)

REM Open browser after delay
start /B cmd /c "timeout /t 6 /nobreak >nul && start http://localhost:5175/"

echo.
echo Watchdog will open at http://localhost:5175
echo Press Ctrl+C to stop frontend
echo ============================================================
echo.

call npm run dev
