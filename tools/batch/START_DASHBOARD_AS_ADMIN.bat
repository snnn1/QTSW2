@echo off
setlocal EnableExtensions
title Pipeline Dashboard Admin Launcher

REM Compatibility wrapper for old shortcuts.
REM Canonical dashboard launcher is launch\START_DASHBOARD_ADMIN.bat.
REM It owns the shared Dashboard/Matrix API on 127.0.0.1:8000.
cd /d "%~dp0..\.."
if errorlevel 1 (
  echo Failed to resolve QTSW2 repository root from %~dp0
  pause
  exit /b 1
)

if not exist "launch\START_DASHBOARD_ADMIN.bat" (
  echo Missing canonical launcher: %CD%\launch\START_DASHBOARD_ADMIN.bat
  pause
  exit /b 1
)

call "launch\START_DASHBOARD_ADMIN.bat"
set "RC=%ERRORLEVEL%"
endlocal & exit /b %RC%
