@echo off
setlocal enabledelayedexpansion
title Master Matrix
color 0A

REM Change to project root
cd /d "%~dp0.."
set "PROJECT_ROOT=%CD%"

echo.
echo ================================================
echo   Master Matrix
echo ================================================
echo.

REM [1/6] Check port 8000
echo [1/6] Checking port availability...
netstat -an | findstr ":8000" | findstr "LISTENING" >nul 2>&1
if %ERRORLEVEL% equ 0 (
    echo WARNING: Port 8000 may be in use. Continuing anyway...
)
echo.

REM [2/6] Start backend
echo [2/6] Starting backend...
start "Master Matrix Backend" cmd /k "cd /d %PROJECT_ROOT% && python -m uvicorn modules.dashboard.backend.main:app --host 0.0.0.0 --port 8000"
timeout /t 3 /nobreak >nul
echo.

REM [3/6] Wait for backend health
echo [3/6] Waiting for backend to be ready...
set "ATTEMPT=0"
:backend_loop
set /a ATTEMPT+=1
powershell -Command "try { (Invoke-WebRequest -Uri 'http://localhost:8000/health' -Method GET -TimeoutSec 2 -UseBasicParsing -ErrorAction Stop).StatusCode -eq 200 } catch { exit 1 }" >nul 2>&1
if %ERRORLEVEL% equ 0 goto backend_ready
if %ATTEMPT% geq 30 (
    echo ERROR: Backend did not start. Check the backend window for errors.
    pause
    exit /b 1
)
timeout /t 1 /nobreak >nul
goto backend_loop
:backend_ready
echo Backend is ready.
echo.

REM [4/6] Start frontend in new window (BEFORE opening browser)
echo [4/6] Starting frontend...
start "Master Matrix Frontend" cmd /k "cd /d %PROJECT_ROOT%\modules\matrix_timetable_app\frontend && set VITE_API_PORT=8000 && npm run dev"
timeout /t 2 /nobreak >nul
echo.

REM [5/6] Wait for frontend (port 5174)
echo [5/6] Waiting for frontend to be ready...
set "ATTEMPT=0"
:frontend_loop
set /a ATTEMPT+=1
netstat -an | findstr ":5174" | findstr "LISTENING" >nul 2>&1
if %ERRORLEVEL% equ 0 goto frontend_ready
if %ATTEMPT% geq 60 (
    echo WARNING: Frontend may not be ready. Opening browser anyway...
    goto open_browser
)
timeout /t 1 /nobreak >nul
goto frontend_loop
:frontend_ready
echo Frontend is ready.
timeout /t 3 /nobreak >nul
echo.

REM [6/6] Open browser (only after frontend is ready - fixes initial load not connecting)
:open_browser
echo [6/6] Opening browser...
start http://localhost:5174
echo.

echo ================================================
echo Master Matrix is ready!
echo.
echo Backend:  http://localhost:8000
echo Frontend: http://localhost:5174
echo.
echo Browser should open automatically.
echo Close the backend/frontend windows to stop.
echo ================================================
echo.
pause
