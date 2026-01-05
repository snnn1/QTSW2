@echo off
REM Run analyzer for ALL instruments with ALL time slots
REM Processes all instruments found in data/translated/ with all time slots:
REM   S1: 07:30, 08:00, 09:00
REM   S2: 09:30, 10:00, 10:30, 11:00
REM   All trading days: Mon-Fri

cd /d "%~dp0.."

echo ========================================
echo Running Analyzer - ALL Instruments, ALL Time Slots
echo ========================================
echo.
echo This will process ALL instruments with ALL time slots:
echo   Instruments: ES, NQ, YM, CL, NG, GC, RTY
echo   S1 slots: 07:30, 08:00, 09:00
echo   S2 slots: 09:30, 10:00, 10:30, 11:00
echo   All trading days: Mon-Fri
echo.
echo Data folder: data/translated
echo Output folder: data/analyzer_temp/%DATE%
echo.
echo This will run all 7 instruments in parallel for faster processing.
echo.

REM Set environment variable to mark this as a pipeline run (so output goes to analyzer_temp)
set PIPELINE_RUN=1

python tools\run_analyzer_parallel.py ^
    --folder data\translated ^
    --instruments ES NQ YM CL NG GC RTY

if %ERRORLEVEL% EQU 0 (
    echo.
    echo ========================================
    echo All Instruments Analysis Complete!
    echo ========================================
) else (
    echo.
    echo ========================================
    echo Analysis Failed with error code %ERRORLEVEL%
    echo ========================================
    pause
)
