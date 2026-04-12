@echo off
REM Build Watchdog UI Frontend for Production

cd /d "%~dp0\.."

title Build Watchdog UI (Production)

echo ============================================================
echo   Building Watchdog UI Frontend (Production)
echo ============================================================
echo.

cd modules\watchdog\frontend

if not exist "package.json" (
    echo ERROR: Standalone watchdog frontend not found at %CD%
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
echo Frontend built to: modules\watchdog\frontend\dist\
echo.
echo Start watchdog backend (8002) and navigate to: http://localhost:5175
echo.
pause
