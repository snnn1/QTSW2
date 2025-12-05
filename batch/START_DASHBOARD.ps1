# Start Pipeline Dashboard
# This starts both the backend and frontend in a single terminal

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  Pipeline Dashboard" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan
Write-Host ""

# Change to project root
$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location (Join-Path $scriptPath "..")
$projectRoot = Get-Location

Write-Host "[1/2] Starting backend..." -ForegroundColor Yellow
Write-Host "================================================" -ForegroundColor Cyan

# Start backend process in background (output to temp files)
$backendOutFile = Join-Path $env:TEMP "dashboard_backend_out.log"
$backendErrFile = Join-Path $env:TEMP "dashboard_backend_err.log"

$backendProcess = Start-Process -FilePath "python" `
    -ArgumentList "-m", "uvicorn", "dashboard.backend.main:app", "--reload", "--host", "0.0.0.0", "--port", "8001" `
    -WorkingDirectory $projectRoot `
    -NoNewWindow `
    -PassThru `
    -RedirectStandardOutput $backendOutFile `
    -RedirectStandardError $backendErrFile

# Wait for backend to start
Start-Sleep -Seconds 3

Write-Host ""
Write-Host "[2/2] Starting frontend..." -ForegroundColor Yellow
Write-Host "Frontend will run in this window..." -ForegroundColor Gray
Write-Host ""

# Open browser
Start-Sleep -Seconds 2
Start-Process "http://localhost:5173" | Out-Null

Write-Host ""
Write-Host "================================================" -ForegroundColor Cyan
Write-Host "Dashboard is ready" -ForegroundColor Green
Write-Host ""
Write-Host "Backend:  http://localhost:8001 (running in background)" -ForegroundColor Gray
Write-Host "Frontend: http://localhost:5173 (output shown below)" -ForegroundColor Gray
Write-Host ""
Write-Host "Browser should open automatically." -ForegroundColor Gray
Write-Host ""
Write-Host "Press Ctrl+C to stop frontend (backend will stop automatically)." -ForegroundColor Gray
Write-Host "================================================" -ForegroundColor Cyan
Write-Host ""

# Change to frontend directory and start
Set-Location "dashboard\frontend"

try {
    # Start frontend
    npm run dev
}
finally {
    # Cleanup: Stop backend process when frontend stops
    Write-Host ""
    Write-Host "Stopping backend..." -ForegroundColor Yellow
    if ($backendProcess -and !$backendProcess.HasExited) {
        Stop-Process -Id $backendProcess.Id -Force -ErrorAction SilentlyContinue
    }
    Write-Host "Dashboard stopped." -ForegroundColor Green
}
