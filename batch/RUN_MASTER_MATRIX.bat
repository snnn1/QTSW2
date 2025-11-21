@echo off
setlocal enabledelayedexpansion
title Master Matrix
color 0A

echo.
echo ================================================
echo   Master Matrix
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
    echo [%date% %time%] Log file created by RUN_MASTER_MATRIX.bat > "%MASTER_MATRIX_LOG%"
)

REM Check if backend is already running
netstat -an | findstr ":8000" >nul 2>&1
if errorlevel 1 (
    echo [1/4] Starting backend...
    start "Master Matrix Backend" cmd /k "cd /d \"%PROJECT_ROOT%\dashboard\backend\" && python -u main.py"
    echo Waiting for backend to start...
    timeout /t 5 /nobreak >nul
) else (
    echo [1/4] Backend already running on port 8000
)

REM Check if frontend is already running
netstat -an | findstr ":5174" >nul 2>&1
if errorlevel 1 (
    echo [2/4] Starting frontend...
    start "Master Matrix Frontend" cmd /k "cd /d \"%PROJECT_ROOT%\matrix_timetable_app\frontend\" && npm run dev"
    echo Waiting for frontend to start...
    timeout /t 8 /nobreak >nul
) else (
    echo [2/4] Frontend already running on port 5174
)

REM Open browser
echo [3/4] Opening browser...
timeout /t 2 /nobreak >nul
start http://localhost:5174

REM Open debug log viewer
echo [4/4] Opening debug log viewer...
start "Master Matrix Debug Log" cmd /k "cd /d \"%PROJECT_ROOT%\" && batch\VIEW_MASTER_MATRIX_DEBUG.bat"

echo.
echo ================================================
echo Master Matrix is ready!
echo.
echo Backend:  http://localhost:8000
echo Frontend: http://localhost:5174
echo.
echo Browser should open automatically.
echo Debug log viewer opened in separate window.
echo.
echo Press any key to close this window...
echo (Backend and frontend will keep running)
echo ================================================
pause >nul
