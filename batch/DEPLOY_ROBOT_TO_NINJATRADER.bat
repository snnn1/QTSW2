@echo off
REM Build and deploy Robot to NinjaTrader (DLL-only mode)
REM 1. Build Robot.Core.dll
REM 2. Copy DLL to NinjaTrader Custom
REM 3. Copy RobotSimStrategy.cs to Strategies (no AddOns source - avoids type duplicates)

cd /d "%~dp0\.."

echo ============================================================
echo   Building and Deploying Robot to NinjaTrader (DLL-only)
echo ============================================================
echo.

REM Step 1: Build
echo [1/3] Building Robot.Core.dll...
dotnet build RobotCore_For_NinjaTrader\Robot.Core.csproj -c Release
if errorlevel 1 (
    echo [ERROR] Build failed.
    pause
    exit /b 1
)
echo [OK] Build succeeded.
echo.

REM Step 2: Copy DLL
echo [2/3] Copying DLL to NinjaTrader Custom...
set "SOURCE=RobotCore_For_NinjaTrader\bin\Release\net48\Robot.Core.dll"
set "DEST1=%USERPROFILE%\Documents\NinjaTrader 8\bin\Custom\Robot.Core.dll"
set "DEST2=%USERPROFILE%\OneDrive\Documents\NinjaTrader 8\bin\Custom\Robot.Core.dll"
set "PDB_SOURCE=RobotCore_For_NinjaTrader\bin\Release\net48\Robot.Core.pdb"
if exist "%USERPROFILE%\Documents\NinjaTrader 8\bin\Custom\" copy /Y "%SOURCE%" "%DEST1%" >nul
if exist "%USERPROFILE%\OneDrive\Documents\NinjaTrader 8\bin\Custom\" copy /Y "%SOURCE%" "%DEST2%" >nul
if exist "%PDB_SOURCE%" (
    if exist "%USERPROFILE%\Documents\NinjaTrader 8\bin\Custom\" copy /Y "%PDB_SOURCE%" "%USERPROFILE%\Documents\NinjaTrader 8\bin\Custom\Robot.Core.pdb" >nul
    if exist "%USERPROFILE%\OneDrive\Documents\NinjaTrader 8\bin\Custom\" copy /Y "%PDB_SOURCE%" "%USERPROFILE%\OneDrive\Documents\NinjaTrader 8\bin\Custom\Robot.Core.pdb" >nul
)
echo [OK] DLL copied.
echo.

REM Step 3: Copy strategy only (no AddOns source)
echo [3/3] Copying RobotSimStrategy.cs to Strategies...
set "NT_STRATEGIES=%USERPROFILE%\OneDrive\Documents\NinjaTrader 8\bin\Custom\Strategies"
set "NT_STRATEGIES2=%USERPROFILE%\Documents\NinjaTrader 8\bin\Custom\Strategies"
if exist "%NT_STRATEGIES%" (
    xcopy /Y /Q "RobotCore_For_NinjaTrader\Strategies\RobotSimStrategy.cs" "%NT_STRATEGIES%\"
    echo [OK] Strategy synced to OneDrive.
)
if exist "%NT_STRATEGIES2%" (
    xcopy /Y /Q "RobotCore_For_NinjaTrader\Strategies\RobotSimStrategy.cs" "%NT_STRATEGIES2%\"
    echo [OK] Strategy synced to Documents.
)

echo.
echo ============================================================
echo   Deployment complete! (DLL + Strategy only)
echo ============================================================
echo.
echo IMPORTANT: Restart NinjaTrader (or Tools -^> Compile) to load changes.
echo.
echo NOTE: If you had AddOns\RobotCore_For_NinjaTrader, you may want to
echo DELETE that folder to avoid NinjaTrader compiling duplicate types.
echo.
echo MANUAL STEP: Add Robot.Core.dll reference in NinjaTrader
echo 1. Open NinjaTrader -^> Control Center -^> New -^> NinjaScript Editor
echo 2. Right-click in the editor -^> References
echo 3. Click Add -^> Browse to: Documents\NinjaTrader 8\bin\Custom\Robot.Core.dll
echo 4. Click OK, then Tools -^> Compile
echo.
pause
