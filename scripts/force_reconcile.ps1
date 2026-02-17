# Force-reconcile orphan journals for specified instruments.
# Use when you have confirmed the account position is correct and journals are stale
# (e.g. after manual flatten without exit fills).
#
# Usage: .\force_reconcile.ps1 -Instruments MYM,MCL
# Or:    .\force_reconcile.ps1 MYM MCL
#
# Creates data/pending_force_reconcile.json. The robot picks it up on next Tick cycle.

param(
    [Parameter(ValueFromRemainingArguments=$true)]
    [string[]]$Instruments
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $scriptDir "..")
$dataDir = Join-Path $root "data"
$triggerFile = Join-Path $dataDir "pending_force_reconcile.json"

if ($Instruments.Count -eq 0) {
    Write-Host "Usage: .\force_reconcile.ps1 -Instruments MYM,MCL"
    Write-Host "   Or: .\force_reconcile.ps1 MYM MCL"
    Write-Host ""
    Write-Host "Instruments: MYM, MCL, M2K, MNQ, MES, etc."
    exit 1
}

# Flatten comma-separated
$instList = @()
foreach ($i in $Instruments) {
    $instList += $i -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ }
}

if ($instList.Count -eq 0) {
    Write-Host "No valid instruments specified."
    exit 1
}

if (-not (Test-Path $dataDir)) {
    New-Item -ItemType Directory -Path $dataDir | Out-Null
}

$payload = @{ instruments = $instList } | ConvertTo-Json
$payload | Set-Content -Path $triggerFile -Encoding UTF8

Write-Host "Created $triggerFile with instruments: $($instList -join ', ')"
Write-Host "The robot will process this on the next Tick cycle (within ~1 second)."
Write-Host "Check logs for FORCE_RECONCILE_EXECUTED and FORCE_RECONCILE_COMPLETE events."
