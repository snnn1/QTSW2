@echo off
setlocal enabledelayedexpansion
title Master Matrix Frontend
color 0A

REM Prevent window from closing on error
set "ERROR_OCCURRED=0"

echo.
echo ================================================
echo   Master Matrix
echo ================================================
echo.

REM Change to project root (go up 3 levels from modules/matrix/batch)
cd /d %~dp0..\..\..
set "PROJECT_ROOT=%CD%"

REM Create logs directory if it doesn't exist
if not exist "%PROJECT_ROOT%\logs" mkdir "%PROJECT_ROOT%\logs" 2>nul

REM Create master matrix log file if it doesn't exist
set "MASTER_MATRIX_LOG=%PROJECT_ROOT%\logs\master_matrix.log"
if not exist "%MASTER_MATRIX_LOG%" (
    echo [%date% %time%] Log file created by RUN_MASTER_MATRIX.bat > "%MASTER_MATRIX_LOG%"
)

REM Find available port starting from 8000
set "BACKEND_PORT=8000"
set "PORT_FOUND=0"

:check_port
netstat -ano | findstr ":%BACKEND_PORT%" >nul 2>&1
if errorlevel 1 (
    set "PORT_FOUND=1"
    echo Found available port: %BACKEND_PORT%
) else (
    echo Port %BACKEND_PORT% is in use, trying next port...
    set /a BACKEND_PORT+=1
    if %BACKEND_PORT% GTR 8010 (
        echo ERROR: Could not find available port between 8000-8010
        echo Please close existing backend instances or manually specify a port
        echo.
        echo Window will stay open. Close manually when done.
        pause
        REM Do not exit - keep window open
        REM exit /b 1
        goto :end
    )
    goto check_port
)

echo.
echo [1/3] Starting backend on port %BACKEND_PORT% in visible window...
start "Master Matrix Backend (Port %BACKEND_PORT%)" cmd /k "cd /d %PROJECT_ROOT% && python -m uvicorn modules.dashboard.backend.main:app --host 0.0.0.0 --port %BACKEND_PORT%"
timeout /t 3 /nobreak >nul

echo [2/3] Opening browser...
timeout /t 2 /nobreak >nul
start http://localhost:5174

echo [3/3] Starting frontend...
echo Frontend will run in this window...
echo.
echo Setting VITE_API_PORT=%BACKEND_PORT% for frontend...
set "VITE_API_PORT=%BACKEND_PORT%"
echo.

echo.
echo ================================================
echo Master Matrix is ready!
echo.
echo Backend:  http://localhost:%BACKEND_PORT% (running in separate window)
echo Frontend: http://localhost:5174 (output shown below)
echo.
echo Browser should open automatically.
echo.
echo Press Ctrl+C to stop frontend (backend will keep running).
echo Close the "Master Matrix Backend" window to stop backend.
echo ================================================
echo.

REM Change to frontend directory - with error checking
echo.
echo ================================================
echo Changing to frontend directory...
echo ================================================
echo Project root: %PROJECT_ROOT%
echo Target: %PROJECT_ROOT%\modules\matrix_timetable_app\frontend
echo.

cd /d "%PROJECT_ROOT%\modules\matrix_timetable_app\frontend"
if errorlevel 1 (
    echo.
    echo ================================================
    echo ERROR: Failed to change to frontend directory!
    echo ================================================
    echo.
    echo Tried to change to: %PROJECT_ROOT%\modules\matrix_timetable_app\frontend
    echo Current directory: %CD%
    echo Project root: %PROJECT_ROOT%
    echo.
    echo Listing project root contents:
    dir /b "%PROJECT_ROOT%\modules" 2>nul
    echo.
    echo Please check that the frontend directory exists.
    echo.
    set "ERROR_OCCURRED=1"
    goto :error_exit
)
echo Successfully changed to: %CD%

REM Check if frontend directory exists
echo.
echo Checking for package.json...
if not exist "package.json" (
    echo.
    echo ================================================
    echo ERROR: Frontend not found!
    echo ================================================
    echo.
    echo Expected location: %CD%
    echo Current directory: %CD%
    echo.
    echo Frontend directory or package.json is missing.
    echo.
    echo Listing current directory contents:
    dir /b
    echo.
    set "ERROR_OCCURRED=1"
    goto :error_exit
)
echo [OK] package.json found!

REM Check if node_modules exists
if not exist "node_modules" (
    echo.
    echo ================================================
    echo WARNING: node_modules not found!
    echo ================================================
    echo.
    echo Installing dependencies first...
    echo This may take a few minutes...
    echo.
    call npm install
    if errorlevel 1 (
        echo.
        echo ERROR: npm install failed!
        echo.
        echo Window will stay open. Close manually when done.
        pause
        REM Do not exit - keep window open
        REM exit /b 1
        goto :end
    )
    echo.
    echo Dependencies installed successfully!
    echo.
)

REM Set environment variable for frontend BEFORE starting npm
REM Vite reads environment variables at startup, so this must be set before npm run dev
set "VITE_API_PORT=%BACKEND_PORT%"

REM Verify backend is responding before starting frontend
echo.
echo Verifying backend is responding on port %BACKEND_PORT%...
timeout /t 2 /nobreak >nul
netstat -ano | findstr ":%BACKEND_PORT%" >nul 2>&1
if errorlevel 1 (
    echo [WARNING] Backend may not be ready yet. Waiting 3 more seconds...
    timeout /t 3 /nobreak >nul
)

REM Run frontend - this will keep the window open while running
echo.
echo ================================================
echo Starting frontend development server...
echo ================================================
echo.
echo Frontend will run in this window (do not close it)
echo If you see errors, they will be displayed below.
echo Press Ctrl+C to stop the frontend.
echo.
echo Running: npm run dev
echo Environment: VITE_API_PORT=%VITE_API_PORT%
echo Backend should be on: http://localhost:%BACKEND_PORT%
echo Frontend will connect to: http://localhost:%VITE_API_PORT%/api
echo Current directory: %CD%
echo.
echo ================================================
echo.

REM Use call to ensure batch file continues and window stays open
REM npm run dev should keep running and block, keeping window open
REM IMPORTANT: VITE_API_PORT must be set in the environment before npm starts
call npm run dev

REM If we reach here, npm run dev has exited
echo.
echo ================================================
echo npm run dev has exited
echo ================================================
set "ERROR_OCCURRED=1"
goto :error_exit

:error_exit
echo.
echo ================================================
if "%ERROR_OCCURRED%"=="1" (
    echo An error occurred or frontend stopped
) else (
    echo Frontend has stopped normally
)
echo ================================================
echo.
echo Check the error messages above for details.
echo.
echo Current directory: %CD%
echo Backend port: %BACKEND_PORT%
echo.
if "%ERROR_OCCURRED%"=="1" (
    echo To troubleshoot:
    echo   1. Check if npm is installed: npm --version
    echo   2. Check if node_modules exists: dir node_modules
    echo   3. Try running manually: npm run dev
    echo   4. Check if port 5174 is in use: netstat -ano ^| findstr :5174
    echo.
) else (
    echo Possible reasons:
    echo   - You pressed Ctrl+C to stop it
    echo   - Port 5174 was already in use
    echo   - npm run dev exited
    echo.
    echo Backend is still running in the other window.
    echo.
)
echo Window will stay open. Close manually when done.
echo.
:end
pause
REM Do not exit - keep window open so user can see any errors
REM exit /b %ERROR_OCCURRED%
