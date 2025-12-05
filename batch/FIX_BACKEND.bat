@echo off
title Fix Backend - Kill Stuck Processes
color 0C
echo.
echo ================================================
echo   Fixing Backend - Killing Stuck Processes
echo ================================================
echo.

REM Kill any Python processes that might be using port 8001
echo Checking for processes on port 8001...
for /f "tokens=5" %%a in ('netstat -aon ^| findstr :8001 ^| findstr LISTENING') do (
    echo Found process %%a on port 8001
    taskkill /F /PID %%a >nul 2>&1
    if errorlevel 1 (
        echo   Could not kill process %%a (may need admin rights)
    ) else (
        echo   Killed process %%a
    )
)

echo.
echo Waiting 3 seconds for ports to free up...
timeout /t 3 /nobreak >nul

echo.
echo ================================================
echo   Port 8001 should now be free
echo ================================================
echo.
echo Now you can start the backend with:
echo   batch\START_DASHBOARD.bat
echo.
pause

