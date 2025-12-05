@echo off
title Analyzer & Dashboard Integration Test
color 0B
echo.
echo ================================================
echo   Analyzer & Dashboard Integration Test
echo ================================================
echo.

cd /d %~dp0..

echo Running integration tests...
echo.

python tools\test_analyzer_dashboard_integration.py

if errorlevel 1 (
    echo.
    echo ================================================
    echo Integration test completed with errors
    echo ================================================
) else (
    echo.
    echo ================================================
    echo Integration test completed successfully
    echo ================================================
)

echo.
pause



