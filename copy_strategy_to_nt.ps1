# Copy RobotSimStrategy.cs to NinjaTrader Strategies folder
# Source: RobotCore_For_NinjaTrader (same project as DLL)

$ErrorActionPreference = "Stop"
$projectRoot = if ($PSScriptRoot) { $PSScriptRoot } else { $PWD }
if (-not (Test-Path (Join-Path $projectRoot "RobotCore_For_NinjaTrader\Strategies\RobotSimStrategy.cs"))) {
    $projectRoot = Split-Path $projectRoot -Parent
}
$source = Join-Path $projectRoot "RobotCore_For_NinjaTrader\Strategies\RobotSimStrategy.cs"

# Resolve NinjaTrader Strategies path (OneDrive or Documents)
$ntStrategies = $null
foreach ($base in @(
    (Join-Path $env:USERPROFILE "OneDrive\Documents\NinjaTrader 8\bin\Custom\Strategies"),
    (Join-Path $env:USERPROFILE "Documents\NinjaTrader 8\bin\Custom\Strategies")
)) {
    if (Test-Path (Split-Path $base -Parent)) {
        $ntStrategies = $base
        break
    }
}
if (-not $ntStrategies) {
    $ntStrategies = Join-Path $env:USERPROFILE "Documents\NinjaTrader 8\bin\Custom\Strategies"
}

if (-not (Test-Path $ntStrategies)) {
    New-Item -ItemType Directory -Path $ntStrategies -Force | Out-Null
    Write-Host "Created directory: $ntStrategies"
}

if (Test-Path $source) {
    Copy-Item $source -Destination (Join-Path $ntStrategies "RobotSimStrategy.cs") -Force
    Write-Host "[OK] RobotSimStrategy.cs copied to $ntStrategies"
} else {
    Write-Host "[ERROR] Source not found: $source"
    exit 1
}
