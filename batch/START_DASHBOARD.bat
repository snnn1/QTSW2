@echo off
REM Start Pipeline Dashboard
REM This starts both the backend and frontend

echo Starting Pipeline Dashboard...
echo.

REM Start backend in new window
start "Pipeline Dashboard Backend" cmd /k "cd /d %~dp0.. && python -m uvicorn dashboard.backend.main:app --reload --port 8000"

REM Wait a bit for backend to start
timeout /t 3 /nobreak >nul

REM Start frontend in new window
start "Pipeline Dashboard Frontend" cmd /k "cd /d %~dp0..\dashboard\frontend && npm run dev"

echo.
echo Dashboard starting...
echo Backend: http://localhost:8000
echo Frontend: http://localhost:5173
echo.
echo Press any key to exit this window (dashboard will keep running)
pause >nul

