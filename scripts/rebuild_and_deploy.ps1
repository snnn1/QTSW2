# Full rebuild and deploy - run from project root
# Verifies all builds succeed before reporting success
# -SkipCacheClear: Pass to deploy script to avoid clearing NinjaTrader cache (use if strategies disappear after deploy)

param([switch]$SkipCacheClear)
$projectRoot = if ($PSScriptRoot) { Split-Path $PSScriptRoot -Parent } else { $PWD }
Set-Location $projectRoot

$failed = $false

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  FULL REBUILD AND DEPLOY" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# 1. Robot.Core
Write-Host "[1/5] Building Robot.Core (Release)..." -ForegroundColor Yellow
try {
    dotnet build "RobotCore_For_NinjaTrader\Robot.Core.csproj" -c Release | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
    Write-Host "      [OK] Robot.Core built" -ForegroundColor Green
} catch {
    Write-Host "      [FAIL] $_" -ForegroundColor Red
    $failed = $true
}
Write-Host ""

# 2. Deploy to NinjaTrader
Write-Host "[2/5] Deploying to NinjaTrader (DLLs, strategy, cache)..." -ForegroundColor Yellow
try {
    $deployArgs = @()
    if ($SkipCacheClear) { $deployArgs += "-SkipCacheClear" }
    & "$projectRoot\scripts\deploy_to_ninjatrader.ps1" @deployArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Deploy failed" }
    Write-Host "      [OK] Deploy complete" -ForegroundColor Green
} catch {
    Write-Host "      [FAIL] $_" -ForegroundColor Red
    $failed = $true
}
Write-Host ""

# 3. Watchdog frontend
Write-Host "[3/5] Building standalone Watchdog frontend..." -ForegroundColor Yellow
try {
    Push-Location "modules\watchdog\frontend"
    npm run build 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
    Write-Host "      [OK] Watchdog frontend built" -ForegroundColor Green
    Pop-Location
} catch {
    Write-Host "      [FAIL] $_" -ForegroundColor Red
    $failed = $true
    Pop-Location -ErrorAction SilentlyContinue
}
Write-Host ""

# 4. Dashboard frontend
Write-Host "[4/5] Building Dashboard frontend..." -ForegroundColor Yellow
$dashDir = Join-Path $projectRoot "modules\dashboard\frontend"
Push-Location $dashDir
$null = cmd /c "npm run build:dashboard 2>nul"
$dashExit = $LASTEXITCODE
Pop-Location
if ($dashExit -ne 0) {
    Write-Host "      [FAIL] Build failed (exit $dashExit)" -ForegroundColor Red
    $failed = $true
} else {
    Write-Host "      [OK] Dashboard frontend built" -ForegroundColor Green
}
Write-Host ""

# 5. Verify backends import
Write-Host "[5/5] Verifying Python backends..." -ForegroundColor Yellow
Set-Location $projectRoot
& python -c "from modules.watchdog.backend.main import app" 2>$null
$wExit = $LASTEXITCODE
if ($wExit -ne 0) {
    Write-Host "      [FAIL] Watchdog backend import failed" -ForegroundColor Red
    $failed = $true
} else {
    Write-Host "      [OK] Watchdog backend OK" -ForegroundColor Green
}
& python -c "from modules.dashboard.backend.main import app" 2>$null
$dExit = $LASTEXITCODE
if ($dExit -ne 0) {
    Write-Host "      [FAIL] Dashboard backend import failed" -ForegroundColor Red
    $failed = $true
} else {
    Write-Host "      [OK] Dashboard backend OK" -ForegroundColor Green
}
Write-Host ""

# Summary
Write-Host "============================================================" -ForegroundColor Cyan
if ($failed) {
    Write-Host "  REBUILD FAILED - check errors above" -ForegroundColor Red
    exit 1
} else {
    Write-Host "  ALL BUILDS SUCCEEDED" -ForegroundColor Green
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor White
    Write-Host "  1. Start NinjaTrader to load new Robot DLLs" -ForegroundColor Gray
    Write-Host "  2. Watchdog:  batch\START_WATCHDOG_FULL.bat (backend 8002 + frontend 5175)" -ForegroundColor Gray
    Write-Host "  3. Dashboard: batch\START_DASHBOARD_AS_ADMIN.bat or similar (backend 8001 + frontend 5173)" -ForegroundColor Gray
    Write-Host ""
    Write-Host "If strategies disappear after deploy: run with -SkipCacheClear next time, or fix compile errors in NinjaScript Editor." -ForegroundColor Gray
    Write-Host ""
    exit 0
}
