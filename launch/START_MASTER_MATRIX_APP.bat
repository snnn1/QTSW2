@echo off
setlocal EnableExtensions EnableDelayedExpansion
title Start Master Matrix UI (Vite 5174)
REM Parent of launch\ is repo root — PYTHONPATH must point at repo\system
cd /d "%~dp0.."
set "PYTHONPATH=%CD%\system"

echo [%TIME%] Repo: %CD%
echo [%TIME%] PYTHONPATH=%PYTHONPATH%

REM Matrix UI uses Pipeline Dashboard API on :8000 only — do not start a second matrix backend.
powershell -NoProfile -Command "$p = Get-NetTCPConnection -LocalPort 8000 -State Listen -ErrorAction SilentlyContinue; if ($p) { exit 0 } else { exit 1 }"
if errorlevel 1 (
  echo [%TIME%] Port 8000 free — starting Dashboard API ^(modules.dashboard.backend.main:app^)...
  start "Dashboard API :8000" cmd /k "cd /d ""%CD%"" && set PYTHONPATH=%CD%\system && python -m uvicorn modules.dashboard.backend.main:app --host 127.0.0.1 --port 8000 --reload"
  timeout /t 2 /nobreak >nul
) else (
  echo [%TIME%] Port 8000 already listening — skipping Dashboard API start.
)

echo [%TIME%] Starting Master Matrix frontend (system\modules\matrix_timetable_app\frontend) on :5174 ...
start "Master Matrix UI (Vite 5174)" cmd /k "cd /d ""%CD%\system\modules\matrix_timetable_app\frontend"" && npm run dev"

timeout /t 5 /nobreak >nul
echo [%TIME%] Opening http://localhost:5174/
start "" "http://localhost:5174/"

echo [%TIME%] Done.
endlocal
exit /b 0
