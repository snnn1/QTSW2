@echo off
setlocal EnableExtensions EnableDelayedExpansion
title Start Master Matrix UI (Vite 5174)
REM Parent of launch\ is repo root - PYTHONPATH must point at repo\system
cd /d "%~dp0.."
set "PYTHONPATH=%CD%\system"
set "BACKEND_PROBE_URL=http://127.0.0.1:8000/api/matrix/test"

echo [%TIME%] Repo: %CD%
echo [%TIME%] PYTHONPATH=%PYTHONPATH%

REM Matrix UI uses Pipeline Dashboard API on :8000 only - do not start a second matrix backend.
REM Probe the actual HTTP endpoint, not just the port, because a stale uvicorn reloader can leave
REM :8000 looking busy while the child server is not accepting requests anymore.
powershell -NoProfile -Command ^
  "$probeUrl = '%BACKEND_PROBE_URL%'; " ^
  "try { $resp = Invoke-WebRequest -UseBasicParsing $probeUrl -TimeoutSec 2; if ($resp.StatusCode -ge 200 -and $resp.StatusCode -lt 300) { exit 0 } } catch { }; " ^
  "$conn = Get-NetTCPConnection -LocalPort 8000 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1; " ^
  "if (-not $conn) { exit 1 }; " ^
  "$cmd = ''; try { $proc = Get-CimInstance Win32_Process -Filter ('ProcessId = ' + $conn.OwningProcess) -ErrorAction Stop; $cmd = [string]$proc.CommandLine } catch { }; " ^
  "if ($cmd -match 'uvicorn.*modules\.dashboard\.backend\.main:app') { try { Stop-Process -Id $conn.OwningProcess -Force -ErrorAction Stop; Start-Sleep -Seconds 1; exit 2 } catch { exit 3 } }; " ^
  "exit 4"
set "API_ACTION=%errorlevel%"

if "%API_ACTION%"=="0" (
  echo [%TIME%] Dashboard API probe succeeded - reusing existing backend.
)

if "%API_ACTION%"=="1" (
  echo [%TIME%] Dashboard API not running - starting backend on 127.0.0.1:8000...
  start "Dashboard API :8000" cmd /k "cd /d ""%CD%"" && set PYTHONPATH=%CD%\system && python -m uvicorn modules.dashboard.backend.main:app --host 127.0.0.1 --port 8000"
  timeout /t 2 /nobreak >nul
)

if "%API_ACTION%"=="2" (
  echo [%TIME%] Replaced an unresponsive Dashboard API listener on :8000.
  start "Dashboard API :8000" cmd /k "cd /d ""%CD%"" && set PYTHONPATH=%CD%\system && python -m uvicorn modules.dashboard.backend.main:app --host 127.0.0.1 --port 8000"
  timeout /t 2 /nobreak >nul
)

if "%API_ACTION%"=="3" (
  echo [%TIME%] Found an unresponsive Dashboard API on :8000 but could not stop it automatically.
  echo [%TIME%] Close the old backend window, then rerun this launcher.
  endlocal
  exit /b 1
)

if "%API_ACTION%"=="4" (
  echo [%TIME%] Port 8000 is occupied by a non-dashboard process.
  echo [%TIME%] Move that process off :8000 or stop it, then rerun this launcher.
  endlocal
  exit /b 1
)

echo [%TIME%] Starting Master Matrix frontend (system\modules\matrix_timetable_app\frontend) on :5174 ...
start "Master Matrix UI (Vite 5174)" cmd /k "cd /d ""%CD%\system\modules\matrix_timetable_app\frontend"" && npm run dev"

timeout /t 5 /nobreak >nul
echo [%TIME%] Opening http://localhost:5174/
start "" "http://localhost:5174/"

echo [%TIME%] Done.
endlocal
exit /b 0
