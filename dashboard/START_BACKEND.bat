@echo off
REM Start Dashboard Backend Only

cd /d %~dp0
python -m uvicorn dashboard.backend.main:app --reload --port 8000 --log-level warning







