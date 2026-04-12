@echo off
REM Simple batch file to test pipeline components
echo ============================================================
echo   PIPELINE TEST
echo ============================================================
echo.

echo [1] Checking if backend is running on port 8001...
netstat -ano | findstr :8001 | findstr LISTENING
if %errorlevel% == 0 (
    echo   [OK] Backend is running
) else (
    echo   [ERROR] Backend is NOT running!
    echo   Start it with: batch\START_DASHBOARD.bat
    pause
    exit /b 1
)

echo.
echo [2] Checking processed files...
if exist "data\processed\*.parquet" (
    echo   [OK] Processed files found
    dir /b "data\processed\*.parquet" | findstr /i "ES" | findstr /v /c:""
) else (
    echo   [WARNING] No processed parquet files found
)

echo.
echo [3] Checking analyzer output...
if exist "data\analyzer_runs\*.parquet" (
    echo   [OK] Analyzer output files found
    for /f "delims=" %%f in ('dir /b /s "data\analyzer_runs\*ES*.parquet" 2^>nul ^| findstr /v /c:""') do (
        echo   Latest: %%f
        goto :found
    )
    :found
) else (
    echo   [WARNING] No analyzer output files found
)

echo.
echo [4] Checking scheduler trigger log...
if exist "automation\logs\pipeline_trigger.log" (
    echo   [OK] Trigger log exists
    echo   Last 5 lines:
    powershell -Command "Get-Content 'automation\logs\pipeline_trigger.log' -Tail 5 -ErrorAction SilentlyContinue"
) else (
    echo   [INFO] Trigger log not found yet (will be created on first trigger)
)

echo.
echo ============================================================
echo   Test complete!
echo ============================================================
pause









