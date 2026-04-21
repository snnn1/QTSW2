# Clear only obsolete legacy-classifier-gap risk latches.
# By default this script is dry-run only.
#
# Usage:
#   .\clear_stale_legacy_risk_latches.ps1
#   .\clear_stale_legacy_risk_latches.ps1 -Apply
#   .\clear_stale_legacy_risk_latches.ps1 -Apply -Instruments MES,MYM

param(
    [switch]$Apply,
    [string[]]$Instruments
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $scriptDir "..\..")
$riskLatchDir = Join-Path $root "data\risk_latches"
$obsoleteMarker = "release_blocker_legacy_count_without_classifier"

Write-Host "=== Stale Legacy Risk Latch Cleanup ===" -ForegroundColor Cyan
Write-Host "Project root: $root"
Write-Host "Mode: $(if ($Apply) { 'APPLY' } else { 'DRY-RUN' })"
Write-Host ""

if (-not (Test-Path $riskLatchDir)) {
    Write-Host "Risk latch directory not found: $riskLatchDir" -ForegroundColor Yellow
    exit 0
}

$instrumentFilter = @()
foreach ($i in $Instruments) {
    $instrumentFilter += $i -split "," | ForEach-Object { $_.Trim().ToUpperInvariant() } | Where-Object { $_ }
}

$matches = @()
Get-ChildItem $riskLatchDir -Filter "risk_latch_*.json" | ForEach-Object {
    try {
        $j = Get-Content $_.FullName -Raw | ConvertFrom-Json
        $inst = if ($j.Instrument) { $j.Instrument.Trim().ToUpperInvariant() } else { "" }
        $reason = if ($j.Reason) { [string]$j.Reason } else { "" }
        if ($reason -notlike "*$obsoleteMarker*") { return }
        if ($instrumentFilter.Count -gt 0 -and $inst -notin $instrumentFilter) { return }

        $matches += [pscustomobject]@{
            File = $_.Name
            FullPath = $_.FullName
            Account = $j.Account
            Instrument = $j.Instrument
            BlockedAtUtc = $j.BlockedAtUtc
            Reason = $reason
        }
    } catch {
        Write-Host "Skipping malformed latch file: $($_.Name)" -ForegroundColor Yellow
    }
}

if ($matches.Count -eq 0) {
    Write-Host "No obsolete legacy-classifier-gap risk latches found." -ForegroundColor Green
    exit 0
}

Write-Host "Matched obsolete latches:" -ForegroundColor Yellow
foreach ($m in $matches) {
    Write-Host "  $($m.File)"
    Write-Host "    Account: $($m.Account)  Instrument: $($m.Instrument)"
    Write-Host "    BlockedAt: $($m.BlockedAtUtc)"
    Write-Host "    Reason: $($m.Reason)"
}
Write-Host ""

if (-not $Apply) {
    Write-Host "Dry-run only. Re-run with -Apply to delete these files." -ForegroundColor Green
    exit 0
}

$deleted = 0
foreach ($m in $matches) {
    Remove-Item -LiteralPath $m.FullPath -Force
    $deleted++
    Write-Host "Deleted $($m.File)" -ForegroundColor Green
}

Write-Host ""
Write-Host "Deleted $deleted obsolete risk latch file(s)." -ForegroundColor Green
Write-Host "Restart the robot to verify these instruments no longer emit RISK_LATCH_HYDRATED for the stale legacy reason."
