# Robot-only rebuild and deploy - run from project root
# Builds Robot.Core and deploys to NinjaTrader. Does not build Watchdog or Dashboard.
# -ForceCacheClear: Pass to deploy script to clear NinjaTrader cache (default: skip to preserve strategies)

param([switch]$ForceCacheClear)
$projectRoot = if ($PSScriptRoot) { Split-Path $PSScriptRoot -Parent } else { $PWD }
Set-Location $projectRoot

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  REBUILD AND DEPLOY (Robot Only)" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# Stage 1: Build Robot.Core
Write-Host "Rebuilding Robot.Core..." -ForegroundColor Yellow
$buildResult = dotnet build "RobotCore_For_NinjaTrader\Robot.Core.csproj" -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "[FAIL] Robot.Core build failed" -ForegroundColor Red
    exit 1
}
Write-Host "[OK] Robot.Core built" -ForegroundColor Green
Write-Host ""

# Stage 2: Deploy to NinjaTrader
Write-Host "Deploying Robot to NinjaTrader..." -ForegroundColor Yellow
$deployArgs = @()
if ($ForceCacheClear) { $deployArgs += "-ForceCacheClear" }
& "$projectRoot\scripts\deploy_to_ninjatrader.ps1" @deployArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "[FAIL] Deploy failed" -ForegroundColor Red
    exit 1
}
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  DEPLOY SUMMARY" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "Robot build:   SUCCESS" -ForegroundColor Green
Write-Host "Robot deploy:  SUCCESS" -ForegroundColor Green
Write-Host "Strategy copy: SUCCESS" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "  1. Start NinjaTrader" -ForegroundColor Gray
Write-Host "  2. Compile NinjaScript if RobotSimStrategy.cs changed" -ForegroundColor Gray
Write-Host ""
