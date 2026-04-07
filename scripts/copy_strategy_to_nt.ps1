# Copy RobotSimStrategy.cs to NinjaTrader Strategies folder
# Source of truth: modules\robot\ninjatrader (editorial); fallback RobotCore_For_NinjaTrader\Strategies

$ErrorActionPreference = "Stop"
$projectRoot = if ($PSScriptRoot) { $PSScriptRoot } else { $PWD }
if (-not (Test-Path (Join-Path $projectRoot "RobotCore_For_NinjaTrader\Strategies\RobotSimStrategy.cs"))) {
    $projectRoot = Split-Path $projectRoot -Parent
}
$sourceModules = Join-Path $projectRoot "modules\robot\ninjatrader\RobotSimStrategy.cs"
$sourceRobotCore = Join-Path $projectRoot "RobotCore_For_NinjaTrader\Strategies\RobotSimStrategy.cs"
if (Test-Path $sourceModules) {
    $source = $sourceModules
    Write-Host "Strategy source: modules\robot\ninjatrader (canonical)" -ForegroundColor Gray
} else {
    $source = $sourceRobotCore
    Write-Host "Strategy source: RobotCore_For_NinjaTrader\Strategies" -ForegroundColor Gray
}

# Resolve NinjaTrader Strategies path(s) — deploy to every Custom folder that exists
$ntStrategyDirs = @()
foreach ($base in @(
    (Join-Path $env:USERPROFILE "OneDrive\Documents\NinjaTrader 8\bin\Custom\Strategies"),
    (Join-Path $env:USERPROFILE "Documents\NinjaTrader 8\bin\Custom\Strategies")
)) {
    $customParent = Split-Path $base -Parent
    if (Test-Path $customParent) {
        if (-not (Test-Path $base)) {
            New-Item -ItemType Directory -Path $base -Force | Out-Null
            Write-Host "Created directory: $base"
        }
        $ntStrategyDirs += $base
    }
}
if ($ntStrategyDirs.Count -eq 0) {
    $fallback = Join-Path $env:USERPROFILE "Documents\NinjaTrader 8\bin\Custom\Strategies"
    if (-not (Test-Path $fallback)) {
        New-Item -ItemType Directory -Path $fallback -Force | Out-Null
    }
    $ntStrategyDirs = @($fallback)
}

if (-not (Test-Path $source)) {
    Write-Host "[ERROR] Source not found: $source"
    exit 1
}

foreach ($ntStrategies in $ntStrategyDirs) {
    Copy-Item $source -Destination (Join-Path $ntStrategies "RobotSimStrategy.cs") -Force
    Write-Host "[OK] RobotSimStrategy.cs copied to $ntStrategies"
}
