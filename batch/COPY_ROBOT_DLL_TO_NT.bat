@echo off
REM Copy Robot.Core.dll to NinjaTrader Custom folder
REM This ensures NinjaTrader uses the latest DLL

cd /d "%~dp0\.."

set "SOURCE=RobotCore_For_NinjaTrader\bin\Release\net48\Robot.Core.dll"
set "DEST1=%USERPROFILE%\Documents\NinjaTrader 8\bin\Custom\Robot.Core.dll"
set "DEST2=%USERPROFILE%\OneDrive\Documents\NinjaTrader 8\bin\Custom\Robot.Core.dll"
set "PDB_SOURCE=RobotCore_For_NinjaTrader\bin\Release\net48\Robot.Core.pdb"
set "PDB_DEST1=%USERPROFILE%\Documents\NinjaTrader 8\bin\Custom\Robot.Core.pdb"
set "PDB_DEST2=%USERPROFILE%\OneDrive\Documents\NinjaTrader 8\bin\Custom\Robot.Core.pdb"

echo ============================================================
echo   Copying Robot.Core.dll to NinjaTrader
echo ============================================================
echo.

if not exist "%SOURCE%" (
    echo [ERROR] Source DLL not found: %SOURCE%
    echo Please build the DLL first: dotnet build RobotCore_For_NinjaTrader\Robot.Core.csproj -c Release
    pause
    exit /b 1
)

echo Source: %SOURCE%
echo.

REM Copy to regular Documents folder
if exist "%DEST1%" (
    echo Copying to: %DEST1%
    copy /Y "%SOURCE%" "%DEST1%"
    if exist "%PDB_SOURCE%" (
        copy /Y "%PDB_SOURCE%" "%PDB_DEST1%"
    )
    echo [OK] Copied to Documents folder
) else (
    echo [SKIP] Documents folder not found: %DEST1%
)

echo.

REM Copy to OneDrive folder
if exist "%DEST2%" (
    echo Copying to: %DEST2%
    copy /Y "%SOURCE%" "%DEST2%"
    if exist "%PDB_SOURCE%" (
        copy /Y "%PDB_SOURCE%" "%PDB_DEST2%"
    )
    echo [OK] Copied to OneDrive folder
) else (
    echo [SKIP] OneDrive folder not found: %DEST2%
)

echo.
echo [OK] DLL copied successfully!
echo.
echo IMPORTANT: Restart NinjaTrader to load the new DLL
echo.

pause
