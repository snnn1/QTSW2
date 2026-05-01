# Copy RobotSimStrategy.cs to NinjaTrader Strategies folder
# Source of truth: modules\robot\ninjatrader (editorial); fallback RobotCore_For_NinjaTrader\Strategies

$ErrorActionPreference = "Stop"
$projectRoot = if ($PSScriptRoot) { $PSScriptRoot } else { $PWD }
for ($i = 0; $i -lt 8; $i++) {
    if (Test-Path (Join-Path $projectRoot "system\RobotCore_For_NinjaTrader\Strategies\RobotSimStrategy.cs")) { break }
    $parent = Split-Path $projectRoot -Parent
    if ([string]::IsNullOrEmpty($parent) -or $parent -eq $projectRoot) { break }
    $projectRoot = $parent
}
$sourceModules = Join-Path $projectRoot "system\modules\robot\ninjatrader\RobotSimStrategy.cs"
$sourceRobotCore = Join-Path $projectRoot "system\RobotCore_For_NinjaTrader\Strategies\RobotSimStrategy.cs"
if (Test-Path $sourceModules) {
    $source = $sourceModules
    Write-Host "Strategy source: system\modules\robot\ninjatrader (canonical)" -ForegroundColor Gray
} else {
    $source = $sourceRobotCore
    Write-Host "Strategy source: system\RobotCore_For_NinjaTrader\Strategies" -ForegroundColor Gray
}

# Resolve NinjaTrader Strategies path from the Windows Documents known-folder.
# Do not deploy to stale OneDrive mirrors just because they still exist.
$ntStrategyDirs = @()
$documentsPath = [Environment]::GetFolderPath("MyDocuments")
if ([string]::IsNullOrWhiteSpace($documentsPath)) {
    $documentsPath = Join-Path $env:USERPROFILE "Documents"
}
foreach ($base in @(
    (Join-Path $documentsPath "NinjaTrader 8\bin\Custom\Strategies"),
    (Join-Path $env:USERPROFILE "Documents\NinjaTrader 8\bin\Custom\Strategies")
) | Select-Object -Unique) {
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
    $fallback = Join-Path $documentsPath "NinjaTrader 8\bin\Custom\Strategies"
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
exit 0
