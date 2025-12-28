@echo off
title Scheduler Diagnostic Tool
color 0B
echo.
echo ================================================
echo   Windows Task Scheduler Diagnostic
echo ================================================
echo.
echo This will check if the Pipeline Runner task is
echo configured correctly and executing.
echo.
pause

cd /d "%~dp0\.."
set PROJECT_ROOT=%CD%

echo.
echo Running diagnostic...
echo.

python "%PROJECT_ROOT%\tools\diagnose_scheduler.py"

echo.
echo ================================================
echo   Diagnostic Complete
echo ================================================
echo.
pause



