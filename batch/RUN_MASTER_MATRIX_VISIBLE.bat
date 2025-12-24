@echo off
setlocal enabledelayedexpansion
title Master Matrix (Backend Visible)
color 0A

echo.
echo ================================================
echo   Master Matrix (Backend Visible)
echo ================================================
echo.

REM Change to project root
cd /d %~dp0..
set "PROJECT_ROOT=%CD%"

REM Create logs directory if it doesn't exist
if not exist "%PROJECT_ROOT%\logs" mkdir "%PROJECT_ROOT%\logs" 2>nul

REM Create master matrix log file if it doesn't exist
set "MASTER_MATRIX_LOG=%PROJECT_ROOT%\logs\master_matrix.log"
if not exist "%MASTER_MATRIX_LOG%" (
    echo [%date% %time%] Log file created by RUN_MASTER_MATRIX_VISIBLE.bat > "%MASTER_MATRIX_LOG%"
)

echo [1/3] Starting backend in visible window...
start "Master Matrix Backend" cmd /k "cd /d %PROJECT_ROOT% && python -m uvicorn modules.dashboard.backend.main:app --host 0.0.0.0 --port 8000"
timeout /t 3 /nobreak >nul

echo [2/3] Opening browser...
timeout /t 2 /nobreak >nul
start http://localhost:5174

echo [3/3] Starting frontend...
echo Frontend will run in this window...
echo.

echo.
echo ================================================
echo Master Matrix is ready!
echo.
echo Backend:  http://localhost:8000 (running in separate window)
echo Frontend: http://localhost:5174 (output shown below)
echo.
echo Browser should open automatically.
echo.
echo Press Ctrl+C to stop frontend (backend will keep running).
echo Close the "Master Matrix Backend" window to stop backend.
echo ================================================
echo.

cd /d "%PROJECT_ROOT%\matrix_timetable_app\frontend"
npm run dev

