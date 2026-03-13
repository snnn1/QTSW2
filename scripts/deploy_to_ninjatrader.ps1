# Build and deploy Robot.Core + dependencies + strategy to NinjaTrader
# Run from project root
# -SkipCacheClear: Skip deleting NinjaTrader.Custom.dll/Vendor.dll (avoids recompile; use when strategies disappear after deploy)

param([switch]$SkipCacheClear)
$ErrorActionPreference = "Stop"
# Resolve project root: script may be in project root or scripts/
$projectRoot = if ($PSScriptRoot) { $PSScriptRoot } else { $PWD }
if (-not (Test-Path (Join-Path $projectRoot "RobotCore_For_NinjaTrader"))) {
    $projectRoot = Split-Path $projectRoot -Parent
}

Write-Host "============================================================"
Write-Host "  Build and Deploy to NinjaTrader"
Write-Host "============================================================"
Write-Host ""

# Step 1: Build
Write-Host "[1/4] Building Robot.Core (Release)..." -ForegroundColor Cyan
dotnet build (Join-Path $projectRoot "RobotCore_For_NinjaTrader\Robot.Core.csproj") -c Release | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] Build failed" -ForegroundColor Red
    exit 1
}
Write-Host "[OK] Build succeeded" -ForegroundColor Green
Write-Host ""

# Step 2: Copy DLLs
Write-Host "[2/4] Copying DLLs to NinjaTrader Custom..." -ForegroundColor Cyan
& (Join-Path $projectRoot "copy_dll_when_ready.ps1") -NoPause
if ($LASTEXITCODE -ne 0) { exit 1 }
Write-Host ""

# Step 3: Copy strategy
Write-Host "[3/4] Copying RobotSimStrategy.cs to Strategies..." -ForegroundColor Cyan
& (Join-Path $projectRoot "copy_strategy_to_nt.ps1")
if ($LASTEXITCODE -ne 0) { exit 1 }
Write-Host ""

# Step 4: Clear NinjaTrader cache so it recompiles and loads new DLLs (optional)
Write-Host "[4/4] NinjaTrader cache..." -ForegroundColor Cyan
if ($SkipCacheClear) {
    Write-Host "  [SKIP] Cache clear skipped (-SkipCacheClear). NinjaTrader will use existing compiled strategies." -ForegroundColor Yellow
    Write-Host "  Note: If you changed RobotSimStrategy.cs, run Tools -> Compile in NinjaScript Editor after starting NinjaTrader." -ForegroundColor Gray
} else {
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
    Write-Host "  If strategies disappear after restart: Open NinjaScript Editor -> Tools -> Compile and fix any errors." -ForegroundColor Gray
}
Write-Host ""
Write-Host "[OK] Deploy complete. Start NinjaTrader to load the new strategy and DLLs." -ForegroundColor Green
