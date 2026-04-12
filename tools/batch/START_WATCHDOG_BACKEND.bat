@echo off
REM Start Watchdog Backend (Separate from Pipeline Dashboard)
REM This starts the watchdog backend on port 8002

cd /d "%~dp0\.."

title Watchdog Backend (Port 8002)

echo ============================================================
echo   Starting Watchdog Backend
echo ============================================================
echo.
echo Backend will be available at: http://localhost:8002
echo.
echo Press Ctrl+C to stop the backend.
echo ============================================================
echo.

python -m uvicorn modules.watchdog.backend.main:app --host 0.0.0.0 --port 8002

pause
