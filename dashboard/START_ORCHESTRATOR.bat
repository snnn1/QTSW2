@echo off
title Dashboard Backend with Orchestrator
color 0A
echo.
echo ================================================
echo   Starting Dashboard Backend with Orchestrator
echo ================================================
echo.

cd /d "%~dp0"

if not exist "backend\main.py" (
    echo ERROR: Backend file not found
    pause
    exit /b 1
)

echo Starting backend server with orchestrator...
echo Backend will be available at: http://localhost:8000
echo API docs at: http://localhost:8000/docs
echo.
echo Keep this window open!
echo Press Ctrl+C to stop the server
echo ================================================
echo.

REM Run from project root as module to fix relative imports
cd /d "%~dp0\.."
set PYTHONPATH=%CD%;%PYTHONPATH%
python -m uvicorn dashboard.backend.main:app --reload --host 0.0.0.0 --port 8000

if errorlevel 1 (
    echo.
    echo ERROR: Backend failed to start!
    echo Check the error messages above.
    pause
)

