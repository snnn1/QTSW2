# Reliable robot rebuild and deploy entry point for this repo layout.
#
# Usage from QTSW2 root:
#   .\tools\rebuild_and_deploy.ps1
#   .\tools\rebuild_and_deploy.ps1 -ForceCacheClear
#
# Builds Robot.Core (Release), then deploys DLLs and RobotSimStrategy.cs to NinjaTrader.
# Close NinjaTrader before deploy if DLLs are locked.

param([switch]$ForceCacheClear)

$ErrorActionPreference = "Stop"

$projectRoot = if ($PSScriptRoot) { $PSScriptRoot } else { $PWD }
for ($i = 0; $i -lt 6; $i++) {
    if (Test-Path (Join-Path $projectRoot "system\RobotCore_For_NinjaTrader\Robot.Core.csproj")) { break }
    $parent = Split-Path $projectRoot -Parent
    if ([string]::IsNullOrEmpty($parent) -or $parent -eq $projectRoot) { break }
    $projectRoot = $parent
}

if (-not (Test-Path (Join-Path $projectRoot "system\RobotCore_For_NinjaTrader\Robot.Core.csproj"))) {
    Write-Error "Could not locate system\RobotCore_For_NinjaTrader\Robot.Core.csproj from $PSScriptRoot"
    exit 1
}

Set-Location $projectRoot

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  REBUILD AND DEPLOY (Robot Only)" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "Rebuilding Robot.Core..." -ForegroundColor Yellow
dotnet build (Join-Path $projectRoot "system\RobotCore_For_NinjaTrader\Robot.Core.csproj") -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "[FAIL] Robot.Core build failed" -ForegroundColor Red
    exit 1
}
Write-Host "[OK] Robot.Core built" -ForegroundColor Green
Write-Host ""

Write-Host "Deploying Robot to NinjaTrader..." -ForegroundColor Yellow
$deploy = Join-Path $projectRoot "tools\deploy_to_ninjatrader.ps1"
if ($ForceCacheClear) {
    & $deploy -ClearNinjaTraderCache
} else {
    & $deploy
}
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
Write-Host "  1. Restart NinjaTrader if it was running" -ForegroundColor Gray
Write-Host "  2. Compile NinjaScript" -ForegroundColor Gray
Write-Host ""
exit $LASTEXITCODE
