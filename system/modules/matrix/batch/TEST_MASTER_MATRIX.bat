@echo off
title Master Matrix Diagnostic Test
color 0B
echo.
echo ================================================
echo   Master Matrix Diagnostic Test
echo ================================================
echo.

REM Change to project root (go up 3 levels from modules/matrix/batch)
cd /d %~dp0..\..\..
set "PROJECT_ROOT=%CD%"

echo Running diagnostic tests...
echo.

python modules\matrix\tests\test_matrix_diagnostic.py

if errorlevel 1 (
    echo.
    echo ================================================
    echo Diagnostic test completed with errors
    echo ================================================
) else (
    echo.
    echo ================================================
    echo Diagnostic test completed successfully
    echo ================================================
)

echo.
pause


