# Build and deploy Robot.Core + dependencies + strategy to NinjaTrader
# Run from project root
# NinjaTrader uses pre-built Robot.Core.dll (not source). Do NOT copy AddOns source.
#
# Optional: -ClearNinjaTraderCache  Deletes NinjaTrader.Custom.dll / NinjaTrader.Vendor.dll under Custom\bin
#           so NT forces a full recompile on next start. Off by default.

param(
    [switch]$ClearNinjaTraderCache
)

$ErrorActionPreference = "Stop"
$projectRoot = if ($PSScriptRoot) { $PSScriptRoot } else { $PWD }
for ($i = 0; $i -lt 6; $i++) {
    if (Test-Path (Join-Path $projectRoot "system\RobotCore_For_NinjaTrader")) { break }
    $parent = Split-Path $projectRoot -Parent
    if ([string]::IsNullOrEmpty($parent) -or $parent -eq $projectRoot) { break }
    $projectRoot = $parent
}

Write-Host "============================================================"
Write-Host "  Build and Deploy to NinjaTrader"
Write-Host "============================================================"
Write-Host ""

# Step 1: Build
Write-Host "[1/4] Building Robot.Core (Release)..." -ForegroundColor Cyan
dotnet build (Join-Path $projectRoot "system\RobotCore_For_NinjaTrader\Robot.Core.csproj") -c Release | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] Build failed" -ForegroundColor Red
    exit 1
}
Write-Host "[OK] Build succeeded" -ForegroundColor Green
Write-Host ""

# Step 2: Copy DLLs
Write-Host "[2/4] Copying DLLs to NinjaTrader Custom..." -ForegroundColor Cyan
& (Join-Path $projectRoot "tools\copy_dll_when_ready.ps1") -NoPause
if ($LASTEXITCODE -ne 0) { exit 1 }
Write-Host ""

# Step 3: Copy strategy
Write-Host "[3/4] Copying RobotSimStrategy.cs to Strategies..." -ForegroundColor Cyan
& (Join-Path $projectRoot "tools\scripts_repo\copy_strategy_to_nt.ps1")
if ($LASTEXITCODE -ne 0) { exit 1 }
Write-Host ""

# Step 4 (optional): Clear NinjaTrader cache so it recompiles (only when -ClearNinjaTraderCache)
if ($ClearNinjaTraderCache) {
    Write-Host "[4/4] Clearing NinjaTrader cache (NinjaTrader must be closed)..." -ForegroundColor Cyan
    $ntCustomDirs = @()
    $documentsPath = [Environment]::GetFolderPath("MyDocuments")
    if ([string]::IsNullOrWhiteSpace($documentsPath)) {
        $documentsPath = Join-Path $env:USERPROFILE "Documents"
    }
    foreach ($base in @(
        (Join-Path $documentsPath "NinjaTrader 8\bin\Custom"),
        (Join-Path $env:USERPROFILE "Documents\NinjaTrader 8\bin\Custom")
    ) | Select-Object -Unique) { if (Test-Path $base) { $ntCustomDirs += $base } }
    foreach ($ntCustom in $ntCustomDirs) {
        Write-Host "  Custom folder: $ntCustom"
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
    Write-Host "[4/4] Skipping Custom cache clear (default). Use -ClearNinjaTraderCache if you need to delete NinjaTrader.Custom.dll / NinjaTrader.Vendor.dll." -ForegroundColor DarkGray
}
Write-Host ""
Write-Host "[OK] Deploy complete. Restart NinjaTrader if it was running so it loads new DLLs." -ForegroundColor Green
