@echo off
title Run Unit Tests
color 0B
echo.
echo ================================================
echo          🧪 UNIT TESTS RUNNER
echo ================================================
echo.

REM Check if pytest is installed
python -c "import pytest" 2>nul
if errorlevel 1 (
    echo ❌ pytest is not installed!
    echo.
    echo Installing pytest...
    pip install pytest pytest-cov
    echo.
)

cd /d "%~dp0\.."
echo Running all unit tests...
echo.
python -m pytest tests/ -v

echo.
echo ================================================
echo Tests complete!
echo ================================================
echo.
pause

