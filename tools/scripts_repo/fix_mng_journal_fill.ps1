# Fix MNG journal - record entry fill for NG2 short intent (54479ffbb70cd695)
# Run from project root. Use when fill was processed by broker but journal was never updated.
# Default: short intent filled at 2.835 (stop price) for 2 contracts.
# Override: .\fix_mng_journal_fill.ps1 -IntentId "1323e5b608db36b6" -FillPrice 2.775 -FillQty 2

param(
    [string]$IntentId = "54479ffbb70cd695",  # Short intent (change to 1323e5b608db36b6 for long)
    [decimal]$FillPrice = 2.835,
    [int]$FillQty = 2,
    [string]$TradingDate = (Get-Date -Format "yyyy-MM-dd")
)

$ErrorActionPreference = "Stop"
$journalDir = Join-Path $PSScriptRoot "..\data\execution_journals"
$stream = "NG2"
$journalPath = Join-Path $journalDir "${TradingDate}_${stream}_${IntentId}.json"

if (-not (Test-Path $journalPath)) {
    Write-Host "ERROR: Journal not found: $journalPath"
    exit 1
}

$json = Get-Content $journalPath -Raw | ConvertFrom-Json
if ($json.EntryFilled -eq $true -and $json.EntryFilledQuantityTotal -ge $FillQty) {
    Write-Host "Journal already has fill recorded (EntryFilled=true, EntryFilledQuantityTotal=$($json.EntryFilledQuantityTotal)). No change."
    exit 0
}

$utcNow = [DateTimeOffset]::UtcNow.ToString("o")
$multiplier = if ($json.ContractMultiplier) { [double]$json.ContractMultiplier } else { 10000 }  # NG = $10k/mmBtu
$notional = $FillPrice * $FillQty * $multiplier

$json.EntryFilled = $true
$json.EntryFilledAt = $utcNow
$json.EntryFilledAtUtc = $utcNow
$json.FillPrice = [double]$FillPrice
$json.FillQuantity = $FillQty
$json.EntryFilledQuantityTotal = $FillQty
$json.EntryAvgFillPrice = [double]$FillPrice
$json.ActualFillPrice = [double]$FillPrice
$json.EntryFillNotional = [double]$notional

Write-Host "Updating journal: EntryFilled=true, FillPrice=$FillPrice, FillQty=$FillQty"
$json | ConvertTo-Json -Depth 10 -Compress | Set-Content $journalPath -NoNewline
Write-Host "Done. Journal updated. Restart NinjaTrader or wait for next bar for hydration to pick up the fix."
Write-Host "BE detection requires: 1) Journal with fill, 2) Intent in IntentMap (hydration), 3) No reconciliation freeze."
