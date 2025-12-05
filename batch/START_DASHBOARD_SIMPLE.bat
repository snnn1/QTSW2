@echo off
title Pipeline Dashboard - Backend + Frontend
color 0B
echo.
echo ================================================
echo   Starting Pipeline Dashboard
echo   (Backend + Frontend)
echo ================================================
echo.

cd /d "%~dp0\.."

REM Start backend in a new window
echo [1/2] Starting backend in new window...
start "Dashboard Backend" cmd /k "cd /d %CD% && set PYTHONPATH=%CD%;%PYTHONPATH% && python -m uvicorn dashboard.backend.main:app --reload --host 0.0.0.0 --port 8001"

REM Wait for backend to start
echo Waiting for backend to start...
timeout /t 5 /nobreak >nul

REM Open browser
echo Opening browser...
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

cd dashboard\frontend
npm run dev

