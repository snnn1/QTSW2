@echo off
title Dashboard API :8000
REM Run from tools\ — repo root is parent directory
cd /d "%~dp0.."
set "PYTHONPATH=%CD%\system"

echo PYTHONPATH=%PYTHONPATH%
python -m uvicorn modules.dashboard.backend.main:app --host 127.0.0.1 --port 8000

pause
