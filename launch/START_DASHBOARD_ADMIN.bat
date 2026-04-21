@echo off
setlocal EnableExtensions EnableDelayedExpansion
title Dashboard API :8000 + Pipeline UI (Administrator)
REM Elevated backend so schtasks enable/disable works from the UI.
REM The backend now serves the prebuilt dashboard UI directly on :8000.

net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Requesting Administrator permission ^(needed for Windows Task Scheduler control from the dashboard^)...
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs -WorkingDirectory '%~dp0'"
  exit /b 0
)

cd /d "%~dp0.."
set "PYTHONPATH=%CD%\system"
set "ROOT=%CD%"

echo Running elevated.
echo Repo: %ROOT%
echo PYTHONPATH=%PYTHONPATH%
echo.

REM API on 8000 (inherits elevation - required for Task Scheduler endpoints)
REM If an old non-admin dashboard backend is already listening on 8000, stop it and
REM restart cleanly so the UI really talks to the elevated backend.
powershell -NoProfile -Command ^
  "$conn = Get-NetTCPConnection -LocalPort 8000 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1; " ^
  "if (-not $conn) { exit 1 }; " ^
  "$proc = Get-CimInstance Win32_Process -Filter ('ProcessId = ' + $conn.OwningProcess) -ErrorAction SilentlyContinue; " ^
  "$cmd = ''; if ($proc) { $cmd = [string]$proc.CommandLine }; " ^
  "if ($cmd -match 'uvicorn.*modules\.dashboard\.backend\.main:app') { Stop-Process -Id $conn.OwningProcess -Force -ErrorAction Stop; Start-Sleep -Seconds 1; exit 0 }; " ^
  "exit 2"
set "API_ACTION=%errorlevel%"

if "%API_ACTION%"=="0" (
  echo Found an existing Dashboard API on :8000. Restarting it elevated...
)

if "%API_ACTION%"=="2" (
  echo Port 8000 is in use by a non-dashboard process.
  echo Close that process or move it off :8000, then rerun this launcher.
  pause
  endlocal
  exit /b 1
)

echo Starting Dashboard API :8000 ^(modules.dashboard.backend.main:app^)...
start "Dashboard API :8000 (Admin)" cmd /k "cd /d ""%ROOT%"" && set PYTHONPATH=%ROOT%\system && python -m uvicorn modules.dashboard.backend.main:app --host 127.0.0.1 --port 8000 --reload"
timeout /t 2 /nobreak >nul

echo Opening browser: http://127.0.0.1:8000/pipeline
start "" "http://127.0.0.1:8000/pipeline"

echo.
echo Done. Use the Pipeline Dashboard at the URL above.
pause
endlocal
exit /b 0
