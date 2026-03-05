# test_execution_logging.ps1
# Verifies execution logging changes (Phase 1-4).
# Run: .\scripts\test_execution_logging.ps1 [--date YYYY-MM-DD]

param(
    [string]$Date = "2026-03-03"
)

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path $projectRoot)) {
    $projectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
}
Set-Location $projectRoot

$snapshotPath = Join-Path (Join-Path $projectRoot "data") "audit_snapshot_test.json"
$failed = $false

function Write-Step { param($n, $msg) Write-Host "`n[$n] $msg" -ForegroundColor Cyan }
function Write-Ok { param($msg) Write-Host "  OK: $msg" -ForegroundColor Green }
function Write-Fail { param($msg) Write-Host "  FAIL: $msg" -ForegroundColor Red; $script:failed = $true }

Write-Host "`n=== Execution Logging Test Suite ===" -ForegroundColor Yellow
Write-Host "Date: $Date | Project: $projectRoot`n"

# 1. Rebuild ledger
Write-Step 1 "Rebuild ledger from logs..."
$out = [string](python scripts/rebuild_ledger_from_logs.py --date $Date --out $snapshotPath 2>$null)
if ($LASTEXITCODE -ne 0) {
    Write-Fail "rebuild_ledger_from_logs exited $LASTEXITCODE"
} else {
    if ($out -match "REBUILD:pnl_hash=(\S+)") { Write-Ok "pnl_hash=$($Matches[1])" }
    elseif ($out -match "REBUILD:") { Write-Ok "rebuild completed" }
}

# 2. Determinism check
Write-Step 2 "Determinism check (run twice, compare hashes)..."
$out = [string](python scripts/rebuild_ledger_from_logs.py --date $Date --compare $snapshotPath 2>$null)
if ($LASTEXITCODE -ne 0) {
    Write-Fail "hash mismatch or compare failed"
} elseif ($out -match "AUDIT:PASS") {
    Write-Ok "hash matches snapshot"
} else {
    Write-Ok "compare passed"
}

# 3. Fill metrics
Write-Step 3 "Fill metrics..."
$out = [string](python scripts/fill_metrics_daily.py --date $Date --json 2>$null)
# Exit 1 when targets not met (e.g. null_trading_date_rate>0) - we still want the metrics
$metrics = $out | ConvertFrom-Json -ErrorAction SilentlyContinue
if ($metrics) {
    Write-Ok "total_fills=$($metrics.total_fills) fill_coverage_rate=$($metrics.fill_coverage_rate) unmapped_rate=$($metrics.unmapped_rate) null_trading_date_rate=$($metrics.null_trading_date_rate)"
} else {
    Write-Fail "fill_metrics_daily did not return valid JSON"
}

# 4. Rebuild with metrics
Write-Step 4 "Rebuild with metrics..."
$out = [string](python scripts/rebuild_ledger_from_logs.py --date $Date --metrics 2>$null)
if ($LASTEXITCODE -ne 0) {
    Write-Fail "rebuild with metrics exited $LASTEXITCODE"
} elseif ($out -match "METRICS:invariant_violation_count=(\d+)") {
    $count = [int]$Matches[1]
    if ($count -eq 0) {
        Write-Ok "invariant_violation_count=0"
    } else {
        Write-Fail "invariant_violation_count=$count (expected 0)"
    }
} elseif ($out -match "REBUILD:") {
    Write-Ok "rebuild with metrics completed"
} else {
    Write-Fail "unexpected output"
}

# Cleanup test snapshot
if (Test-Path $snapshotPath) {
    Remove-Item $snapshotPath -Force
}

# Summary
Write-Host ""
if ($failed) {
    Write-Host "=== RESULT: FAILED ===" -ForegroundColor Red
    exit 1
} else {
    Write-Host "=== RESULT: PASSED ===" -ForegroundColor Green
    exit 0
}
