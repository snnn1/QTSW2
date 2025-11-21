@echo off
setlocal enabledelayedexpansion
REM View Master Matrix Debug Log

cd /d %~dp0..

title Master Matrix Debug Log
color 0A

echo ========================================
echo Master Matrix Debug Log Viewer
echo ========================================
echo.

REM Set log file path
set "LOG_PATH=%CD%\logs\master_matrix.log"
echo Debug log file: %LOG_PATH%
echo.

REM Create the log file if it doesn't exist
if not exist "%LOG_PATH%" (
    echo Creating log file...
    echo [%date% %time%] Log file created by VIEW_MASTER_MATRIX_DEBUG.bat > "%LOG_PATH%"
    echo.
)

:wait_loop
if not exist "%LOG_PATH%" (
    echo Waiting for log file to be created...
    timeout /t 2 /nobreak >nul
    goto :wait_loop
)

REM Check file size
for %%A in ("%LOG_PATH%") do set "FILE_SIZE=%%~zA"
if !FILE_SIZE! EQU 0 (
    echo Log file exists but is empty.
    echo Waiting for debug messages...
    echo.
    timeout /t 2 /nobreak >nul
    goto :wait_loop
)

echo Log file found! Size: !FILE_SIZE! bytes
echo.
echo This window shows real-time debug logs from master matrix.
echo Press Ctrl+C to close this window.
echo.
echo ========================================
echo.

REM Show last 50 lines first
powershell -NoProfile -Command "Get-Content '%LOG_PATH%' -Tail 50 -ErrorAction SilentlyContinue"
echo.
echo.
echo Tailing log - new entries will appear below
echo.

REM Tail the log file - use a more reliable method
powershell -NoProfile -Command "$logPath = '%LOG_PATH%'; $lastSize = 0; while ($true) { if (Test-Path $logPath) { $currentSize = (Get-Item $logPath).Length; if ($currentSize -gt $lastSize) { $newContent = Get-Content $logPath -Tail 50; $newContent | ForEach-Object { Write-Host $_ }; $lastSize = $currentSize } } Start-Sleep -Seconds 1 }"

