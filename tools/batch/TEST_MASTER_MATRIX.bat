@echo off
title Master Matrix Diagnostic Test
color 0B
echo.
echo ================================================
echo   Master Matrix Diagnostic Test
echo ================================================
echo.

cd /d %~dp0..

echo Running diagnostic tests...
echo.

python tools\test_matrix_diagnostic.py

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
