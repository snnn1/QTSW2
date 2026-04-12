@echo off
setlocal EnableExtensions EnableDelayedExpansion
title Dashboard API :8000 + Pipeline UI (Administrator)
REM Elevated backend so schtasks enable/disable works from the UI. Vite does not need admin.

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

REM API on 8000 (inherits elevation — required for Task Scheduler endpoints)
powershell -NoProfile -Command "$p = Get-NetTCPConnection -LocalPort 8000 -State Listen -ErrorAction SilentlyContinue; if ($p) { exit 0 } else { exit 1 }"
if errorlevel 1 (
  echo Starting Dashboard API :8000 ^(modules.dashboard.backend.main:app^)...
  start "Dashboard API :8000 (Admin)" cmd /k "cd /d ""%ROOT%"" && set PYTHONPATH=%ROOT%\system && python -m uvicorn modules.dashboard.backend.main:app --host 127.0.0.1 --port 8000 --reload"
  timeout /t 2 /nobreak >nul
) else (
  echo Port 8000 already listening — skipping API start.
)

REM React UI is Vite on 5173 — not the same as :8000
powershell -NoProfile -Command "$p = Get-NetTCPConnection -LocalPort 5173 -State Listen -ErrorAction SilentlyContinue; if ($p) { exit 0 } else { exit 1 }"
if errorlevel 1 (
  echo Starting Pipeline Dashboard UI ^(Vite^) on :5173 ...
  start "Pipeline Dashboard UI (Vite 5173)" cmd /k "cd /d ""%ROOT%\system\modules\dashboard\frontend"" && npm run dev"
  timeout /t 5 /nobreak >nul
) else (
  echo Port 5173 already listening — skipping Vite start.
  timeout /t 2 /nobreak >nul
)

echo Opening browser: http://localhost:5173/
start "" "http://localhost:5173/"

echo.
echo Done. Use the Pipeline Dashboard at the URL above ^(Vite proxies /api to :8000^).
pause
endlocal
exit /b 0
