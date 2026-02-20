# Build and deploy Robot.Core + dependencies + strategy to NinjaTrader
# Run from project root

$ErrorActionPreference = "Stop"
$projectRoot = if ($PSScriptRoot) { $PSScriptRoot } else { $PWD }

Write-Host "============================================================"
Write-Host "  Build and Deploy to NinjaTrader"
Write-Host "============================================================"
Write-Host ""

# Step 1: Build
Write-Host "[1/3] Building Robot.Core (Release)..." -ForegroundColor Cyan
dotnet build (Join-Path $projectRoot "RobotCore_For_NinjaTrader\Robot.Core.csproj") -c Release | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] Build failed" -ForegroundColor Red
    exit 1
}
Write-Host "[OK] Build succeeded" -ForegroundColor Green
Write-Host ""

# Step 2: Copy DLLs
Write-Host "[2/3] Copying DLLs to NinjaTrader Custom..." -ForegroundColor Cyan
& (Join-Path $projectRoot "copy_dll_when_ready.ps1") -NoPause
if ($LASTEXITCODE -ne 0) { exit 1 }
Write-Host ""

# Step 3: Copy strategy
Write-Host "[3/3] Copying RobotSimStrategy.cs to Strategies..." -ForegroundColor Cyan
& (Join-Path $projectRoot "copy_strategy_to_nt.ps1")
