@echo off
title Parallel Analyzer Runner
color 0B
echo.
echo ================================================
echo   Parallel Analyzer Runner
echo ================================================
echo.
echo This will run the analyzer for multiple instruments in parallel
echo to speed up processing significantly.
echo.

cd /d %~dp0..

echo Available instruments: ES, NQ, YM, CL, GC, NG
echo.
set /p INSTRUMENTS="Enter instruments (space-separated, e.g., ES NQ YM CL): "

if "%INSTRUMENTS%"=="" (
    echo No instruments specified. Using default: ES NQ YM CL GC NG
    set INSTRUMENTS=ES NQ YM CL GC NG
)

echo.
echo Running analyzer for: %INSTRUMENTS%
echo.

python tools\run_analyzer_parallel.py --instruments %INSTRUMENTS%

if errorlevel 1 (
    echo.
    echo ================================================
    echo Analyzer completed with errors
    echo ================================================
) else (
    echo.
    echo ================================================
    echo Analyzer completed successfully
    echo ================================================
)

echo.
pause



