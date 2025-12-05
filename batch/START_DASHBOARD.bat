@echo off
title Pipeline Dashboard - Backend + Frontend
color 0B
echo.
echo ================================================
echo   Starting Pipeline Dashboard
echo   (Backend + Frontend)
echo ================================================
echo.

REM Get absolute path to project root
cd /d "%~dp0\.."
set PROJECT_ROOT=%CD%

REM Verify we're in the right place
if not exist "dashboard\backend\main.py" (
    echo ERROR: Backend file not found at %PROJECT_ROOT%\dashboard\backend\main.py
    pause
    exit /b 1
)

REM Start backend in a new window with explicit path
echo [1/2] Starting backend in new window...
echo Project root: %PROJECT_ROOT%
echo.

start "Dashboard Backend" cmd /k "cd /d "%PROJECT_ROOT%" && set PYTHONPATH=%PROJECT_ROOT% && python -m uvicorn dashboard.backend.main:app --reload --host 0.0.0.0 --port 8001"

REM Wait for backend to start
echo Waiting for backend to start...
timeout /t 5 /nobreak >nul

REM Open browser
echo Opening browser...
timeout /t 1 /nobreak >nul
start http://localhost:5173

REM Start frontend in this window
echo.
echo [2/2] Starting frontend...
echo Frontend will run in this window
echo Backend is running in the other window
echo.
echo ================================================
echo   Dashboard Ready!
echo ================================================
echo.
echo Backend:  http://localhost:8001
echo Frontend: http://localhost:5173
echo.
echo Browser should open automatically
echo Press Ctrl+C to stop frontend
echo (Close backend window to stop backend)
echo ================================================
echo.

cd "%PROJECT_ROOT%\dashboard\frontend"
if not exist "package.json" (
    echo ERROR: Frontend not found at %PROJECT_ROOT%\dashboard\frontend
    pause
    exit /b 1
)

npm run dev
