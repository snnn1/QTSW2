# Start Watchdog Backend and Frontend
# Run from QTSW2 root: .\scripts\start_watchdog.ps1

$ErrorActionPreference = "Stop"
# PSScriptRoot = QTSW2/scripts when run as .\scripts\start_watchdog.ps1
$Root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path "$Root\modules\watchdog")) {
    throw "Cannot find QTSW2 root. Run from QTSW2: .\scripts\start_watchdog.ps1"
}

Write-Host "Starting Watchdog (backend: 8002, frontend: 5175)" -ForegroundColor Cyan
Write-Host ""

# Start backend in new window
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$Root'; python -m uvicorn modules.watchdog.backend.main:app --host 0.0.0.0 --port 8002"

# Wait for backend to start
Start-Sleep -Seconds 3

# Start frontend in new window
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$Root\modules\watchdog\frontend'; npm run dev"

Write-Host "Backend and frontend started in separate windows." -ForegroundColor Green
Write-Host "Open: http://localhost:5175" -ForegroundColor Green
