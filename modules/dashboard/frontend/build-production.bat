@echo off
REM Production build script for dashboard frontend
echo ========================================
echo Building Dashboard Frontend (Production)
echo ========================================
echo.

cd /d "%~dp0"

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
echo ========================================
echo Build complete!
echo ========================================
echo.
echo Frontend built to: dist/
echo You can now start the backend and it will serve the frontend.
echo.
pause






