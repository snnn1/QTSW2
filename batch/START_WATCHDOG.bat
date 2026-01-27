@echo off
REM Start Watchdog - Standalone Application
REM Watchdog runs completely independently:
REM   - Backend: http://localhost:8002
REM   - Frontend: http://localhost:5175
REM Does NOT interfere with Dashboard (8001/5173) or Matrix (8000/5174)

cd /d "%~dp0\.."

title Watchdog

echo ============================================================
echo   Starting Watchdog
echo ============================================================
echo.
echo Watchdog Backend:  http://localhost:8002
echo Watchdog Frontend: http://localhost:5175
echo.
echo Press Ctrl+C to stop.
echo ============================================================
echo.

REM Check if watchdog backend is already running
netstat -an | findstr ":8002" | findstr "LISTENING" >nul 2>&1
if %ERRORLEVEL% equ 0 (
    echo WARNING: Port 8002 is already in use!
    echo Watchdog backend may already be running.
    echo.
    choice /C YN /M "Continue anyway"
    if errorlevel 2 exit /b
    echo.
)

REM Check if watchdog frontend port is already in use
netstat -an | findstr ":5175" | findstr "LISTENING" >nul 2>&1
if %ERRORLEVEL% equ 0 (
    echo WARNING: Port 5175 is already in use!
    echo Watchdog frontend may already be running.
    echo.
    choice /C YN /M "Continue anyway"
    if errorlevel 2 exit /b
    echo.
)

REM Start watchdog backend in a new window
echo [1/2] Starting watchdog backend...
start "Watchdog Backend" cmd /k "cd /d %~dp0.. && python -m uvicorn modules.watchdog.backend.main:app --host 0.0.0.0 --port 8002"

REM Wait for backend to start
echo Waiting for backend to start...
timeout /t 5 /nobreak >nul

REM Check if backend started successfully
powershell -Command "$response = try { Invoke-WebRequest -Uri 'http://localhost:8002/health' -Method GET -TimeoutSec 2 -UseBasicParsing -ErrorAction Stop; $response.StatusCode } catch { $null }; if ($response -eq 200) { exit 0 } else { exit 1 }" >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo WARNING: Backend health check failed. It may still be starting...
    echo Continuing anyway...
    echo.
)

REM Start watchdog frontend
echo [2/2] Starting watchdog frontend...
cd modules\dashboard\frontend

if not exist "package.json" (
    echo ERROR: Frontend not found at %CD%
    pause
    exit /b 1
)

REM Check if node_modules exists
if not exist "node_modules" (
    echo Installing dependencies...
    call npm install
    if errorlevel 1 (
        echo ERROR: npm install failed
        pause
        exit /b 1
    )
    echo.
)

REM Start frontend dev server
echo Starting watchdog frontend...
echo.
echo Frontend will be available at: http://localhost:5175
echo.

REM Open browser after delay (frontend needs time to start)
REM Open index-watchdog.html explicitly - Vite can serve any HTML file directly
start /B cmd /c "timeout /t 8 /nobreak >nul && start http://localhost:5175/index-watchdog.html"

echo ============================================================
echo   Watchdog Starting
echo ============================================================
echo.
echo Backend:  http://localhost:8002
echo Frontend: http://localhost:5175
echo.
echo Browser will open automatically in 8 seconds
echo Press Ctrl+C to stop frontend
echo (Close "Watchdog Backend" window to stop backend)
echo ============================================================
echo.

call npm run dev:watchdog
