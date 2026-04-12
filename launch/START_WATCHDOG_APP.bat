@echo off
setlocal EnableExtensions EnableDelayedExpansion
title Start Watchdog (API 8002 + Vite 5175)
REM Parent of launch\ is repo root — required so PYTHONPATH=...\system exists and uvicorn watches the repo
cd /d "%~dp0.."
set "PYTHONPATH=%CD%\system"

echo [%TIME%] Repo: %CD%
echo [%TIME%] PYTHONPATH=%PYTHONPATH%

powershell -NoProfile -Command "$p = Get-NetTCPConnection -LocalPort 8002 -State Listen -ErrorAction SilentlyContinue; if ($p) { exit 0 } else { exit 1 }"
if errorlevel 1 (
  echo [%TIME%] Port 8002 free — starting Watchdog API ^(modules.watchdog.backend.main:app^)...
  start "Watchdog API :8002" cmd /k "cd /d ""%CD%"" && set PYTHONPATH=%CD%\system && python -m uvicorn modules.watchdog.backend.main:app --host 127.0.0.1 --port 8002 --reload"
  timeout /t 2 /nobreak >nul
) else (
  echo [%TIME%] Port 8002 already listening — skipping Watchdog API start.
)

echo [%TIME%] Starting Watchdog frontend (system\modules\watchdog\frontend) on :5175 ...
start "Watchdog UI (Vite 5175)" cmd /k "cd /d ""%CD%\system\modules\watchdog\frontend"" && npm run dev"

timeout /t 5 /nobreak >nul
echo [%TIME%] Opening http://localhost:5175/watchdog
start "" "http://localhost:5175/watchdog"

echo [%TIME%] Done.
endlocal
exit /b 0
