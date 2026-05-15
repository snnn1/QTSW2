@echo off
setlocal EnableExtensions
title Master Matrix Launcher

REM Canonical launcher lives under launch\.
REM Matrix is a frontend on :5174 and must reuse the Pipeline Dashboard API on
REM 127.0.0.1:8000. Do not allocate a fallback backend port here; doing so
REM builds the UI against the wrong API and breaks timetable publish.
cd /d "%~dp0..\..\..\.."
if errorlevel 1 (
  echo Failed to resolve QTSW2 repository root from %~dp0
  pause
  exit /b 1
)

if not exist "launch\START_MASTER_MATRIX_APP.bat" (
  echo Missing canonical launcher: %CD%\launch\START_MASTER_MATRIX_APP.bat
  pause
  exit /b 1
)

call "launch\START_MASTER_MATRIX_APP.bat"
set "RC=%ERRORLEVEL%"
endlocal & exit /b %RC%
