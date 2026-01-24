@echo off
REM Build Watchdog UI Frontend for Production

cd /d "%~dp0\.."

title Build Watchdog UI (Production)

echo ============================================================
echo   Building Watchdog UI Frontend (Production)
echo ============================================================
echo.

cd modules\dashboard\frontend

if not exist "package.json" (
    echo ERROR: Frontend not found at %CD%
    pause
    exit /b 1
)

echo Installing dependencies (if needed)...
call npm install
if errorlevel 1 (
    echo ERROR: npm install failed
    pause
    exit /b 1
)

echo.
echo Building production bundle...
call npm run build
if errorlevel 1 (
    echo ERROR: Build failed
    pause
    exit /b 1
)

echo.
echo ============================================================
echo   Build Complete!
echo ============================================================
echo.
echo Frontend built to: modules\dashboard\frontend\dist\
echo.
echo The backend will serve the frontend automatically.
echo Start the backend and navigate to: http://localhost:8001/watchdog
echo.
pause
