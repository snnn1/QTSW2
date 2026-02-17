@echo off
REM Build and deploy Robot to NinjaTrader (DLL-only mode)
REM ALWAYS uses OneDrive\Documents\NinjaTrader 8 (NinjaTrader loads from MyDocuments = OneDrive)
REM 1. Build Robot.Core.dll
REM 2. Copy DLL to NinjaTrader Custom
REM 3. Copy RobotSimStrategy.cs to Strategies (no AddOns source - avoids type duplicates)

cd /d "%~dp0\.."

echo ============================================================
echo   Building and Deploying Robot to NinjaTrader (DLL-only)
echo   Target: OneDrive\Documents\NinjaTrader 8 (always)
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

REM Step 2: Copy DLL (OneDrive only - close NinjaTrader first if DLL is locked)
echo [2/3] Copying DLL to NinjaTrader Custom (OneDrive)...
set "SOURCE=RobotCore_For_NinjaTrader\bin\Release\net48\Robot.Core.dll"
set "NT_CUSTOM=%USERPROFILE%\OneDrive\Documents\NinjaTrader 8\bin\Custom"
set "PDB_SOURCE=RobotCore_For_NinjaTrader\bin\Release\net48\Robot.Core.pdb"
if exist "%NT_CUSTOM%" (
    copy /Y "%SOURCE%" "%NT_CUSTOM%\Robot.Core.dll"
    if errorlevel 1 (
        echo [WARN] DLL copy failed - close NinjaTrader and run this script again.
    ) else (
        echo [OK] DLL copied.
    )
    if exist "%PDB_SOURCE%" copy /Y "%PDB_SOURCE%" "%NT_CUSTOM%\Robot.Core.pdb" >nul
) else (
    echo [ERROR] OneDrive NinjaTrader folder not found: %NT_CUSTOM%
)
echo.

REM Step 3: Copy strategy (OneDrive only)
echo [3/3] Copying RobotSimStrategy.cs to Strategies (OneDrive)...
set "NT_STRATEGIES=%USERPROFILE%\OneDrive\Documents\NinjaTrader 8\bin\Custom\Strategies"
if exist "%NT_STRATEGIES%" (
    xcopy /Y /Q "RobotCore_For_NinjaTrader\Strategies\RobotSimStrategy.cs" "%NT_STRATEGIES%\"
    echo [OK] Strategy synced to OneDrive.
) else (
    echo [ERROR] OneDrive Strategies folder not found: %NT_STRATEGIES%
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
echo 3. Click Add -^> Browse to: OneDrive\Documents\NinjaTrader 8\bin\Custom\Robot.Core.dll
echo 4. Click OK, then Tools -^> Compile
echo.
pause
