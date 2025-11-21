@echo off
title Master Matrix Builder
color 0A

REM Prevent immediate exit on error
setlocal enabledelayedexpansion

echo.
echo ================================================
echo       MASTER MATRIX BUILDER
echo ================================================
echo.

REM Get script directory and go to project root
cd /d "%~dp0\.."
if errorlevel 1 (
    echo ERROR: Failed to change to project directory
    echo Current directory: %CD%
    pause
    exit /b 1
)

set PROJECT_ROOT=%CD%
echo Project Root: %PROJECT_ROOT%
echo.

REM Check if backend is running
echo [1/3] Checking backend...
netstat -an | findstr ":8000" >nul 2>&1
if errorlevel 1 (
    echo Backend not running. Starting...
    cd /d "%PROJECT_ROOT%\dashboard\backend"
    if errorlevel 1 (
        echo ERROR: Cannot access backend directory
        echo Path: %PROJECT_ROOT%\dashboard\backend
        echo Current directory: %CD%
        pause
        exit /b 1
    )
    if exist "main.py" (
        echo Starting backend server...
        echo Backend window will open - keep it open!
        start "" cmd /k "title Dashboard Backend && cd /d \"%PROJECT_ROOT%\dashboard\backend\" && python main.py"
        echo Waiting 8 seconds for backend to start...
        timeout /t 8 /nobreak >nul
        
        REM Check if it started
        netstat -an | findstr ":8000" >nul 2>&1
        if errorlevel 1 (
            echo.
            echo WARNING: Backend may not have started yet.
            echo Check the "Dashboard Backend" window for errors.
            echo Waiting 5 more seconds...
            timeout /t 5 /nobreak >nul
            netstat -an | findstr ":8000" >nul 2>&1
            if errorlevel 1 (
                echo ERROR: Backend failed to start. Check the backend window.
            ) else (
                echo Backend started successfully!
            )
        ) else (
            echo Backend started successfully!
        )
    ) else (
        echo ERROR: main.py not found at %PROJECT_ROOT%\dashboard\backend\main.py
        pause
        exit /b 1
    )
) else (
    echo Backend already running on port 8000
)

REM Check if frontend is running
echo [2/3] Checking frontend...
netstat -an | findstr ":5174" >nul 2>&1
if errorlevel 1 (
    echo Frontend not running. Starting...
    cd /d "%PROJECT_ROOT%\matrix_timetable_app\frontend"
    if errorlevel 1 (
        echo ERROR: Cannot access frontend directory
        echo Path: %PROJECT_ROOT%\matrix_timetable_app\frontend
        pause
        exit /b 1
    )
    if exist "package.json" (
        if not exist "node_modules" (
            echo Installing dependencies...
            call npm install
            if errorlevel 1 (
                echo ERROR: npm install failed
                pause
                exit /b 1
            )
        )
        echo Starting frontend server...
        start "" cmd /k "cd /d \"%PROJECT_ROOT%\matrix_timetable_app\frontend\" && npm run dev"
        echo Waiting 15 seconds for server to start...
        timeout /t 15 /nobreak >nul
        
        REM Check if it started
        netstat -an | findstr ":5174" >nul 2>&1
        if errorlevel 1 (
            echo WARNING: Frontend may not have started. Check the frontend window for errors.
        ) else (
            echo Frontend started successfully!
        )
    ) else (
        echo ERROR: package.json not found
        pause
        exit /b 1
    )
) else (
    echo Frontend already running
)

REM Open browser
echo [3/3] Opening browser...
timeout /t 2 /nobreak >nul
start http://localhost:5174

echo.
echo ================================================
echo Done!
echo Frontend: http://localhost:5174
echo Backend: http://localhost:8000
echo.
echo Check the frontend window for any errors.
echo Press any key to close...
echo ================================================
pause
