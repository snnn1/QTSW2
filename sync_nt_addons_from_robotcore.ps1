# Sync NT_ADDONS from RobotCore_For_NinjaTrader
# Prevents divergence: RobotCore_For_NinjaTrader is source of truth for NinjaTrader code.
# Run from project root. Call this before deploy if NT_ADDONS is used.

$ErrorActionPreference = "Stop"
$projectRoot = if ($PSScriptRoot) { $PSScriptRoot } else { $PWD }
if (-not (Test-Path (Join-Path $projectRoot "RobotCore_For_NinjaTrader"))) {
    $projectRoot = Split-Path $projectRoot -Parent
}

$sourceDir = Join-Path $projectRoot "RobotCore_For_NinjaTrader"
$destDir = Join-Path $projectRoot "NT_ADDONS"

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

Write-Host "[OK] Synced $synced files to NT_ADDONS from RobotCore_For_NinjaTrader" -ForegroundColor Green
