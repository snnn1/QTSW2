@echo off
REM Copy Robot.Core.dll to NinjaTrader Custom folder
REM ALWAYS uses OneDrive\Documents\NinjaTrader 8 (NinjaTrader loads from MyDocuments = OneDrive)
REM Close NinjaTrader before running if DLL is locked

cd /d "%~dp0\.."

set "SOURCE=RobotCore_For_NinjaTrader\bin\Release\net48\Robot.Core.dll"
set "DEST=%USERPROFILE%\OneDrive\Documents\NinjaTrader 8\bin\Custom\Robot.Core.dll"
set "PDB_SOURCE=RobotCore_For_NinjaTrader\bin\Release\net48\Robot.Core.pdb"
set "PDB_DEST=%USERPROFILE%\OneDrive\Documents\NinjaTrader 8\bin\Custom\Robot.Core.pdb"

echo ============================================================
echo   Copying Robot.Core.dll to NinjaTrader (OneDrive)
echo ============================================================
echo.

if not exist "%SOURCE%" (
    echo [ERROR] Source DLL not found: %SOURCE%
    echo Please build the DLL first: dotnet build RobotCore_For_NinjaTrader\Robot.Core.csproj -c Release
    pause
    exit /b 1
)

echo Source: %SOURCE%
echo Target: %DEST%
echo.

if exist "%USERPROFILE%\OneDrive\Documents\NinjaTrader 8\bin\Custom\" (
    copy /Y "%SOURCE%" "%DEST%"
    if errorlevel 1 (
        echo [ERROR] Copy failed - close NinjaTrader and try again.
    ) else (
        echo [OK] Copied to OneDrive folder.
        if exist "%PDB_SOURCE%" copy /Y "%PDB_SOURCE%" "%PDB_DEST%" >nul
    )
) else (
    echo [ERROR] OneDrive NinjaTrader folder not found.
)

echo.
echo [OK] DLL copied successfully!
echo.
echo IMPORTANT: Restart NinjaTrader to load the new DLL
echo.

pause
