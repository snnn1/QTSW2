# Non-interactive: compare built Robot.Core.dll to NinjaTrader Custom (OneDrive + Documents).
$ErrorActionPreference = "Stop"
$projectRoot = if ($PSScriptRoot) { Split-Path -Parent $PSScriptRoot } else { $PWD }
$src = Join-Path $projectRoot "RobotCore_For_NinjaTrader\bin\Release\net48\Robot.Core.dll"
$bases = @(
    (Join-Path $env:USERPROFILE "OneDrive\Documents\NinjaTrader 8\bin\Custom"),
    (Join-Path $env:USERPROFILE "Documents\NinjaTrader 8\bin\Custom")
)

function ShowDllInfo([string]$label, [string]$path) {
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Host "$label" -ForegroundColor Yellow
        Write-Host "  MISSING: $path" -ForegroundColor Gray
        return $null
    }
    $i = Get-Item -LiteralPath $path
    $h = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    Write-Host "$label" -ForegroundColor Cyan
    Write-Host "  Path: $($i.FullName)"
    Write-Host "  Length: $($i.Length)  LastWrite: $($i.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))"
    Write-Host "  SHA256: $h"
    return $h
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Robot.Core.dll on-disk comparison" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
$hs = ShowDllInfo "SOURCE (repo Release build)" $src
Write-Host ""
$hashes = @{}
foreach ($b in $bases) {
    $short = if ($b -match "OneDrive") { "NT OneDrive ...\bin\Custom" } else { "NT Documents ...\bin\Custom" }
    $t = Join-Path $b "Robot.Core.dll"
    $h = ShowDllInfo $short $t
    if ($h) { $hashes[$b] = $h }
    Write-Host ""
}

Write-Host "Deploy scripts use the first Custom folder that exists (OneDrive before Documents)." -ForegroundColor Gray
Write-Host ""

if (-not $hs) {
    Write-Host "No source build found - run dotnet build first." -ForegroundColor Red
    exit 2
}

$firstBase = $null
foreach ($b in $bases) { if (Test-Path -LiteralPath $b) { $firstBase = $b; break } }
if (-not $firstBase) {
    Write-Host "No NinjaTrader Custom folder found." -ForegroundColor Red
    exit 3
}
$active = Join-Path $firstBase "Robot.Core.dll"
if (-not (Test-Path -LiteralPath $active)) {
    Write-Host "Active deploy target would be: $active" -ForegroundColor Yellow
    Write-Host "Robot.Core.dll is NOT present there - NT is not loading this build from the script target." -ForegroundColor Red
    exit 4
}
$ht = (Get-FileHash -LiteralPath $active -Algorithm SHA256).Hash
if ($ht -eq $hs) {
    Write-Host "RESULT: On-disk match - Custom folder picked by scripts has SAME bytes as repo Release build." -ForegroundColor Green
    Write-Host "NOTE: NinjaTrader must still be restarted after copy to load the new assembly." -ForegroundColor Gray
    exit 0
}

Write-Host "RESULT: MISMATCH - deploy target DLL differs from repo build (copy/deploy or rebuild)." -ForegroundColor Red
exit 1
