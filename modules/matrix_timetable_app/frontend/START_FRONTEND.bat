@echo off
title Master Matrix & Timetable App - Frontend
color 0A
echo.
echo ================================================
echo   Master Matrix & Timetable App - Frontend
echo ================================================
echo.

cd /d "%~dp0"

REM Check if node_modules exists, if not install dependencies
if not exist "node_modules" (
    echo Installing dependencies...
    call npm install
)

echo.
echo Starting frontend development server...
echo Browser will open automatically at:
echo    http://localhost:5174
echo.

REM Start frontend and open browser
start "" /min "Frontend Dev Server" cmd /c "npm run dev"
timeout /t 5 /nobreak >nobreak >nul
start http://localhost:5174

echo.
echo ================================================
echo Frontend is starting!
echo Browser should open automatically.
echo Close this window when done.
echo ================================================
timeout /t 3 /nobreak >nul
exit

