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

echo [1/4] Starting backend...
cd /d "%PROJECT_ROOT%\dashboard\backend"
start "Master Matrix Backend" cmd /k "python -u main.py"
cd /d "%PROJECT_ROOT%"
timeout /t 3 /nobreak >nul

echo [2/4] Starting frontend...
cd /d "%PROJECT_ROOT%\matrix_timetable_app\frontend"
start "Master Matrix Frontend" cmd /k "npm run dev"
cd /d "%PROJECT_ROOT%"
timeout /t 5 /nobreak >nul

echo [3/4] Opening browser...
timeout /t 2 /nobreak >nul
start http://localhost:5174

echo [4/4] Opening debug log viewer...
cd /d "%PROJECT_ROOT%\batch"
start "Master Matrix Debug Log" cmd /k "VIEW_MASTER_MATRIX_DEBUG.bat"
cd /d "%PROJECT_ROOT%"

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
