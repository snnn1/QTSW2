@echo off
title Pipeline Stress Test

REM Get the project root directory
set "PROJECT_ROOT=%~dp0.."
cd /d "%PROJECT_ROOT%"

echo ============================================================
echo   Pipeline Stress Test
echo ============================================================
echo.
echo This will run 3 consecutive full test rounds of the pipeline
echo Make sure the backend is running before starting!
echo.
echo Press any key to continue or Ctrl+C to cancel...
pause > NUL

python tests\integration\test_pipeline_stress.py

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ============================================================
    echo   STRESS TEST FAILED!
    echo ============================================================
    pause
) else (
    echo.
    echo ============================================================
    echo   STRESS TEST PASSED!
    echo ============================================================
    pause
)









