@echo off
title Dashboard Backend
color 0A
echo.
echo ================================================
echo   Starting Dashboard Backend
echo ================================================
echo.

cd /d "%~dp0\.."
set PROJECT_ROOT=%CD%

cd /d "%PROJECT_ROOT%\dashboard\backend"

if not exist "main.py" (
    echo ERROR: Backend file not found
    pause
    exit /b 1
)

echo Starting backend server...
echo Backend will be available at: http://localhost:8000
echo.
echo Press Ctrl+C to stop the server
echo ================================================
echo.

python main.py

