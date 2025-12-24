@echo off
REM Start Dashboard as Administrator
REM This batch file will request admin elevation automatically

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
cd /d "%~dp0\.."

title Pipeline Dashboard (Admin Mode)

echo ============================================================
echo   Starting Pipeline Dashboard (Administrator Mode)
echo ============================================================
echo.
echo This allows the scheduler toggle button to work.
echo.
echo Press Ctrl+C to stop both backend and frontend.
echo ============================================================
echo.

REM Start backend in a new window
echo [1/2] Starting backend in new window...
start "Pipeline Backend (Admin)" cmd /k "cd /d %~dp0.. && python -m uvicorn modules.dashboard.backend.main:app --host 0.0.0.0 --port 8001"

REM Wait for backend to start
echo Waiting for backend to start...
timeout /t 5 /nobreak >nul

REM Open browser
echo Opening browser...
timeout /t 1 /nobreak >nul
start http://localhost:5173

REM Start frontend
echo.
echo [2/2] Starting frontend...
echo Frontend will run in this window
echo Backend is running in another window
echo.
echo ============================================================
echo   Dashboard Ready! (Administrator Mode)
echo ============================================================
echo.
echo Backend:  http://localhost:8001
echo Frontend: http://localhost:5173
echo.
echo Browser should open automatically
echo Press Ctrl+C to stop frontend
echo (Close other window to stop backend)
echo ============================================================
echo.

cd modules\dashboard\frontend
if not exist "package.json" (
    echo ERROR: Frontend not found at %CD%\dashboard\frontend
    pause
    exit /b 1
)

call npm run dev

