@echo off
REM Interactive Breakout Analyzer Runner
REM This script prompts for analyzer parameters and runs the analyzer

echo ========================================
echo Breakout Analyzer - Interactive Mode
echo ========================================
echo.

REM Change to project root
cd /d %~dp0..

REM Prompt for instrument
echo Available instruments: ES, NQ, YM, CL, NG, GC
set /p INSTRUMENT="Enter instrument (default: ES): "
if "%INSTRUMENT%"=="" set INSTRUMENT=ES

REM Prompt for data folder
set /p FOLDER="Enter data folder path (default: data\processed): "
if "%FOLDER%"=="" set FOLDER=data\processed

REM Prompt for sessions
set /p SESSIONS="Enter sessions (S1 S2) (default: S1 S2): "
if "%SESSIONS%"=="" set SESSIONS=S1 S2

REM Prompt for levels
set /p LEVELS="Enter target levels (1-7, space-separated) (default: 1): "
if "%LEVELS%"=="" set LEVELS=1

REM Prompt for days
set /p DAYS="Enter trade days (Mon Tue Wed Thu Fri) (default: Mon Tue Wed Thu Fri): "
if "%DAYS%"=="" set DAYS=Mon Tue Wed Thu Fri

echo.
echo ========================================
echo Running Analyzer with Parameters:
echo ========================================
echo Instrument: %INSTRUMENT%
echo Data Folder: %FOLDER%
echo Sessions: %SESSIONS%
echo Levels: %LEVELS%
echo Days: %DAYS%
echo ========================================
echo.

REM Run the analyzer
python scripts\breakout_analyzer\scripts\run_data_processed.py ^
    --folder "%FOLDER%" ^
    --instrument %INSTRUMENT% ^
    --sessions %SESSIONS% ^
    --levels %LEVELS% ^
    --days %DAYS%

if %ERRORLEVEL% EQU 0 (
    echo.
    echo ========================================
    echo Analyzer completed successfully!
    echo Results saved to: results\breakout_%INSTRUMENT%_*.tsv
    echo ========================================
) else (
    echo.
    echo ========================================
    echo Analyzer failed with error code: %ERRORLEVEL%
    echo ========================================
)

echo.
pause



