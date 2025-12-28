@echo off
title Master Matrix & Timetable App - Frontend
color 0A
echo.
echo ================================================
echo   Master Matrix & Timetable App - Frontend
echo ================================================
echo.

cd /d "%~dp0"

REM Check if node_modules exists, if not install dependencies
if not exist "node_modules" (
    echo Installing dependencies...
    call npm install
)

echo.
echo Starting frontend development server...
echo Browser will open automatically at:
echo    http://localhost:5174
echo.

REM Start frontend
start "" /min "Frontend Dev Server" cmd /c "npm run dev"

REM Wait for frontend dev server to start (give it a moment)
timeout /t 3 /nobreak >nul

REM Wait for backend to be ready before opening browser (optional improvement)
echo.
echo Waiting for backend to be ready...
set BACKEND_READY=0
set MAX_ATTEMPTS=24
set ATTEMPT=0

:check_backend
set /a ATTEMPT+=1
if %ATTEMPT% gtr %MAX_ATTEMPTS% (
    echo Backend not ready after 12 seconds, opening browser anyway...
    echo Frontend will show "Connecting to backend..." until backend is ready.
    goto :open_browser
)

REM Use PowerShell to check if backend test endpoint responds
powershell -Command "$response = try { Invoke-WebRequest -Uri 'http://localhost:8000/api/matrix/test' -Method GET -TimeoutSec 2 -UseBasicParsing -ErrorAction Stop; $response.StatusCode } catch { $null }; if ($response -eq 200) { exit 0 } else { exit 1 }" >nul 2>&1

if %ERRORLEVEL% equ 0 (
    echo Backend is ready!
    set BACKEND_READY=1
    goto :open_browser
) else (
    REM Backend not ready yet, wait 500ms and retry
    timeout /t 1 /nobreak >nul
    goto :check_backend
)

:open_browser
start http://localhost:5174

echo.
echo ================================================
echo Frontend is starting!
echo Browser should open automatically.
echo Close this window when done.
echo ================================================
timeout /t 3 /nobreak >nul
exit

