@echo off
REM Start Pipeline Dashboard
REM This starts both the backend and frontend and opens the browser automatically
REM Also opens a debug log terminal window for the conductor

echo Starting Pipeline Dashboard...
echo.

REM Start backend in new window
start "Pipeline Dashboard Backend" cmd /k "cd /d %~dp0.. && python -m uvicorn dashboard.backend.main:app --reload --port 8000"

REM Wait for backend to start
echo Waiting for backend to start...
timeout /t 5 /nobreak >nul

REM Start frontend in new window
start "Pipeline Dashboard Frontend" cmd /k "cd /d %~dp0..\dashboard\frontend && npm run dev"

REM Wait for frontend to start
echo Waiting for frontend to start...
timeout /t 8 /nobreak >nul

REM Open browser automatically
echo Opening browser...
start http://localhost:5173

REM Open debug log terminal window (shows conductor logs)
echo Opening debug log terminal...
start "Pipeline Conductor - Debug Log" cmd /k "cd /d %~dp0.. && call batch\TAIL_CONDUCTOR_LOG.bat"

echo.
echo Dashboard started!
echo Backend: http://localhost:8000
echo Frontend: http://localhost:5173
echo Browser should open automatically...
echo Debug log terminal opened for conductor logs...
echo.
echo Press any key to exit this window (dashboard will keep running)
pause >nul

