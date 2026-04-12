# Sync system/NT_ADDONS from system/RobotCore_For_NinjaTrader
# Prevents divergence: RobotCore is source of truth for NinjaTrader code.
# Run from project root. Call this before deploy if NT_ADDONS is used.

$ErrorActionPreference = "Stop"
$projectRoot = if ($PSScriptRoot) { $PSScriptRoot } else { $PWD }
for ($i = 0; $i -lt 6; $i++) {
    if (Test-Path (Join-Path $projectRoot "system\RobotCore_For_NinjaTrader")) { break }
    $parent = Split-Path $projectRoot -Parent
    if ([string]::IsNullOrEmpty($parent) -or $parent -eq $projectRoot) { break }
    $projectRoot = $parent
}

$sourceDir = Join-Path $projectRoot "system\RobotCore_For_NinjaTrader"
$destDir = Join-Path $projectRoot "system\NT_ADDONS"

if (-not (Test-Path $destDir)) {
    Write-Host "[WARN] NT_ADDONS not found, skipping sync" -ForegroundColor Yellow
    exit 0
}

# Critical files that must stay in sync (timetable cache, engine, etc.)
$filesToSync = @(
    "TimetableCache.cs",
    "TimetableFilePoller.cs",
    "RobotEngine.cs"
)

$synced = 0
foreach ($rel in $filesToSync) {
    $src = Join-Path $sourceDir $rel
    $dst = Join-Path $destDir $rel
    if (Test-Path $src) {
        $dstParent = Split-Path $dst -Parent
        if (-not (Test-Path $dstParent)) { New-Item -ItemType Directory -Path $dstParent -Force | Out-Null }
        Copy-Item $src $dst -Force
        $synced++
        Write-Host "  Synced: $rel" -ForegroundColor Gray
    }
}

Write-Host "[OK] Synced $synced files to system/NT_ADDONS from system/RobotCore_For_NinjaTrader" -ForegroundColor Green
