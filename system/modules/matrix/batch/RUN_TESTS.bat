@echo off
title Master Matrix Tests
color 0E
echo.
echo ================================================
echo   Master Matrix Functionality Tests
echo ================================================
echo.

REM Find project root
set "PROJECT_ROOT=%~dp0..\..\.."
cd /d "%PROJECT_ROOT%"

echo Running comprehensive matrix tests...
echo Project Root: %PROJECT_ROOT%
echo.

python -m modules.matrix.tests.test_matrix_functionality

echo.
echo ================================================
pause


