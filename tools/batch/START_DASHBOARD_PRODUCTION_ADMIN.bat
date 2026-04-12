@echo off
REM Start Dashboard in Production Mode as Administrator
REM This is the main entry point from the batch/ directory

REM Check if running as admin
net session >nul 2>&1
if %errorLevel% == 0 (
    REM Already running as admin, just start normally
    goto :start
) else (
    REM Not running as admin, request elevation
    echo Requesting administrator privileges...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

:start
REM Now running as admin - navigate to dashboard directory
cd /d "%~dp0\..\modules\dashboard"

REM Check if frontend is built
set FRONTEND_BUILD=frontend\dist\index.html
if not exist "%FRONTEND_BUILD%" (
    echo ERROR: Frontend not built for production!
    echo.
    echo Please run the build script first:
    echo   modules\dashboard\frontend\build-production.bat
    echo.
    echo This will create the optimized production build.
    echo.
    pause
    exit /b 1
)

REM Call the production admin script
call START_PRODUCTION_ADMIN.bat







