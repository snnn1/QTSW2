@echo off
REM Run analyzer for ES with ALL time slots (S1: 07:30, 08:00, 09:00; S2: 09:30, 10:00, 10:30, 11:00)
REM Processes all dates found in data/translated/ES/

cd /d "%~dp0.."

echo ========================================
echo Running Analyzer for ES - ALL Time Slots
echo ========================================
echo.
echo This will process ES with:
echo   S1 slots: 07:30, 08:00, 09:00
echo   S2 slots: 09:30, 10:00, 10:30, 11:00
echo   All trading days: Mon-Fri
echo.
echo Data folder: data/translated
echo Output folder: data/analyzer_temp/%DATE%
echo.

REM Set environment variable to mark this as a pipeline run (so output goes to analyzer_temp)
set PIPELINE_RUN=1

python modules\analyzer\scripts\run_data_processed.py ^
    --folder data\translated ^
    --instrument ES ^
    --sessions S1 S2 ^
    --slots S1:07:30 S1:08:00 S1:09:00 S2:09:30 S2:10:00 S2:10:30 S2:11:00 ^
    --days Mon Tue Wed Thu Fri

if %ERRORLEVEL% EQU 0 (
    echo.
    echo ========================================
    echo ES Analysis Complete!
    echo ========================================
) else (
    echo.
    echo ========================================
    echo ES Analysis Failed with error code %ERRORLEVEL%
    echo ========================================
    pause
)
