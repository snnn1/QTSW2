# Diagnose RECONCILIATION_QTY_MISMATCH - list current state and suggest resolution
# Usage: .\scripts\diagnose_reconciliation.ps1

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $scriptDir "..")
$journalDir = Join-Path $root "data\execution_journals"
$riskLatchDir = Join-Path $root "data\risk_latches"

Write-Host "=== Reconciliation QTY Mismatch Diagnostic ===" -ForegroundColor Cyan
Write-Host "Project root: $root"
Write-Host ""

# 1. Risk latches (frozen instruments)
Write-Host "--- Risk Latches (Frozen Instruments) ---" -ForegroundColor Yellow
if (Test-Path $riskLatchDir) {
    Get-ChildItem $riskLatchDir -Filter "risk_latch_*.json" | ForEach-Object {
        $j = Get-Content $_.FullName | ConvertFrom-Json
        Write-Host "  $($_.Name)"
        Write-Host "    Account: $($j.Account)  Instrument: $($j.Instrument)"
        Write-Host "    Reason: $($j.Reason)"
        Write-Host "    BlockedAt: $($j.BlockedAtUtc)"
    }
    if (-not (Get-ChildItem $riskLatchDir -Filter "risk_latch_*.json" -ErrorAction SilentlyContinue)) {
        Write-Host "  (none)"
    }
} else {
    Write-Host "  Risk latch dir not found: $riskLatchDir"
}
Write-Host ""

# 2. Open journals by instrument (EntryFilled=true, TradeCompleted=false)
Write-Host "--- Open Execution Journals (EntryFilled + !TradeCompleted) ---" -ForegroundColor Yellow
$openByInst = @{}
if (Test-Path $journalDir) {
    Get-ChildItem $journalDir -Filter "*.json" | ForEach-Object {
        try {
            $j = Get-Content $_.FullName -Raw | ConvertFrom-Json
            if ($j.EntryFilled -eq $true -and $j.TradeCompleted -ne $true -and $j.EntryFilledQuantityTotal -gt 0) {
                $inst = if ($j.Instrument) { $j.Instrument.Trim() } else { "UNKNOWN" }
                if (-not $openByInst[$inst]) { $openByInst[$inst] = @() }
                $openByInst[$inst] += @{
                    File = $_.Name
                    Stream = $j.Stream
                    IntentId = $j.IntentId
                    Qty = $j.EntryFilledQuantityTotal
                    EntryAt = $j.EntryFilledAtUtc
                }
            }
        } catch { Write-Host "  (skip $($_.Name): $_)" }
    }
}
foreach ($inst in ($openByInst.Keys | Sort-Object)) {
    $entries = $openByInst[$inst]
    $totalQty = ($entries | ForEach-Object { $_.Qty } | Measure-Object -Sum).Sum
    Write-Host "  $inst : journal_qty=$totalQty ($($entries.Count) entries)"
    foreach ($e in $entries) {
        Write-Host "    - $($e.File)  qty=$($e.Qty)  entry=$($e.EntryAt)"
    }
}
if ($openByInst.Count -eq 0) {
    Write-Host "  (no open journals with EntryFilled + !TradeCompleted)"
}
Write-Host ""

# 3. Recent RECONCILIATION events from logs
Write-Host "--- Recent RECONCILIATION events (last 5) ---" -ForegroundColor Yellow
$engineLog = Join-Path $root "logs\robot\robot_ENGINE.jsonl"
if (Test-Path $engineLog) {
    $recent = Get-Content $engineLog -Tail 5000 | Select-String "RECONCILIATION_QTY_MISMATCH|RECONCILIATION_CONTEXT" | Select-Object -Last 5
    foreach ($line in $recent) {
        if ($line -match '"instrument":"([^"]+)"') { $inst = $matches[1] } else { $inst = "?" }
        if ($line -match '"account_qty":"?(\d+)"?') { $aq = $matches[1] } else { $aq = "?" }
        if ($line -match '"journal_qty":"?(\d+)"?') { $jq = $matches[1] } else { $jq = "?" }
        if ($line -match '"broker_qty":"?(\d+)"?') { $bq = $matches[1] } else { $bq = $aq }
        if ($line -match '"ts_utc":"([^"]+)"') { $ts = $matches[1] } else { $ts = "" }
        $evt = if ($line -match 'RECONCILIATION_QTY_MISMATCH') { "QTY_MISMATCH" } else { "CONTEXT" }
        Write-Host "  $ts | $evt | $inst broker=$bq journal=$jq"
    }
    if (-not $recent) { Write-Host "  (none in last 5000 lines)" }
} else {
    Write-Host "  Log not found: $engineLog"
}
Write-Host ""

# 4. Resolution suggestions
Write-Host "--- Resolution ---" -ForegroundColor Green
Write-Host "If broker has position but journal_qty=0:"
Write-Host "  - Broker position exists without a matching open journal."
Write-Host "  - Possible causes: manual trade, journal in different project root, or journal not yet written."
Write-Host ""
Write-Host "Option A - Flatten then restart:"
Write-Host "  1. Manually flatten the affected instrument in NinjaTrader."
Write-Host "  2. Restart the robot. Reconciliation will close orphan journals when broker is flat."
Write-Host ""
Write-Host "Option B - Force reconcile (when broker position is correct):"
Write-Host "  .\scripts\force_reconcile.ps1 M2K"
Write-Host "  (Creates pending_force_reconcile.json; robot picks up on next Tick)"
Write-Host ""
Write-Host "If project root differs at runtime (NinjaTrader cwd), set:"
Write-Host "  `$env:QTSW2_PROJECT_ROOT = '$root'"
Write-Host "  before starting NinjaTrader."
