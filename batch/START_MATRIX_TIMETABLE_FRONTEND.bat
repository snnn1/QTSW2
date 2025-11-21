@echo off
title Matrix Timetable Frontend
color 0A
echo.
echo ================================================
echo   Starting Matrix Timetable Frontend
echo ================================================
echo.

cd /d "%~dp0\.."
set PROJECT_ROOT=%CD%

cd /d "%PROJECT_ROOT%\matrix_timetable_app\frontend"

if not exist "package.json" (
    echo ERROR: Frontend not found at %PROJECT_ROOT%\matrix_timetable_app\frontend
    pause
    exit /b 1
)

if not exist "node_modules" (
    echo Installing dependencies...
    call npm install
    if errorlevel 1 (
        echo ERROR: npm install failed
        pause
        exit /b 1
    )
)

echo.
echo Starting frontend dev server...
echo Frontend will be available at: http://localhost:5174
echo.
echo Press Ctrl+C to stop the server
echo ================================================
echo.

npm run dev

