# Deploy Robot.Core + dependencies + strategy to NinjaTrader
# Run from project root. Assumes Robot.Core is already built.
# -ForceCacheClear: Delete NinjaTrader.Custom.dll/Vendor.dll to force recompile (default: skip to preserve strategies)

param([switch]$ForceCacheClear)
$ErrorActionPreference = "Stop"
$projectRoot = if ($PSScriptRoot) { $PSScriptRoot } else { $PWD }
if (-not (Test-Path (Join-Path $projectRoot "RobotCore_For_NinjaTrader"))) {
    $projectRoot = Split-Path $projectRoot -Parent
}

Write-Host "============================================================"
Write-Host "  Deploy Robot to NinjaTrader"
Write-Host "============================================================"
Write-Host ""

# Step 1: Copy DLLs (atomic)
Write-Host "Deploying Robot.Core.dll" -ForegroundColor Cyan
Write-Host "Performing atomic DLL swap" -ForegroundColor Cyan
& (Join-Path $projectRoot "scripts\copy_dll_when_ready.ps1") -NoPause
if ($LASTEXITCODE -ne 0) { exit 1 }
Write-Host ""

# Step 2: Copy strategy
Write-Host "Copying RobotSimStrategy.cs" -ForegroundColor Cyan
& (Join-Path $projectRoot "scripts\copy_strategy_to_nt.ps1")
if ($LASTEXITCODE -ne 0) { exit 1 }
Write-Host ""

# Step 3: NinjaTrader cache
Write-Host "NinjaTrader cache..." -ForegroundColor Cyan
if ($ForceCacheClear) {
    $ntCustom = $null
    foreach ($base in @(
        (Join-Path $env:USERPROFILE "OneDrive\Documents\NinjaTrader 8\bin\Custom"),
        (Join-Path $env:USERPROFILE "Documents\NinjaTrader 8\bin\Custom")
    )) { if (Test-Path $base) { $ntCustom = $base; break } }
    if ($ntCustom) {
        $custom = Join-Path $ntCustom "NinjaTrader.Custom.dll"
        $vendor = Join-Path $ntCustom "NinjaTrader.Vendor.dll"
        foreach ($f in @($custom, $vendor)) {
            if (Test-Path $f) {
                try {
                    Remove-Item $f -Force -ErrorAction Stop
                    Write-Host "  Deleted $(Split-Path $f -Leaf)"
                } catch {
                    Write-Host "  [WARN] Could not delete $(Split-Path $f -Leaf) - close NinjaTrader and delete manually" -ForegroundColor Yellow
                }
            }
        }
    }
} else {
    Write-Host "Skipping NinjaTrader cache clear to preserve existing strategies." -ForegroundColor Gray
}
Write-Host ""

Write-Host "Robot deploy complete." -ForegroundColor Green
Write-Host "Start NinjaTrader to load the updated Robot engine." -ForegroundColor Gray
