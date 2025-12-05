# Dashboard Backend with Orchestrator - PowerShell Script

Write-Host ""
Write-Host "================================================" -ForegroundColor Green
Write-Host "  Starting Dashboard Backend with Orchestrator" -ForegroundColor Green
Write-Host "================================================" -ForegroundColor Green
Write-Host ""

# Change to dashboard directory (not backend - uvicorn needs to be run from backend)
$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not (Test-Path (Join-Path $scriptPath "backend\main.py"))) {
    Write-Host "ERROR: Backend file not found" -ForegroundColor Red
    Read-Host "Press Enter to exit"
    exit 1
}

Set-Location (Join-Path $scriptPath "backend")

Write-Host "Starting backend server with orchestrator..." -ForegroundColor Yellow
Write-Host "Backend will be available at: http://localhost:8000" -ForegroundColor Cyan
Write-Host "API docs at: http://localhost:8000/docs" -ForegroundColor Cyan
Write-Host ""
Write-Host "Keep this window open!" -ForegroundColor Yellow
Write-Host "Press Ctrl+C to stop the server" -ForegroundColor Yellow
Write-Host "================================================" -ForegroundColor Green
Write-Host ""

try {
    python -m uvicorn main:app --reload --host 0.0.0.0 --port 8000
}
catch {
    Write-Host ""
    Write-Host "ERROR: Backend failed to start!" -ForegroundColor Red
    Write-Host "Check the error messages above." -ForegroundColor Red
    Read-Host "Press Enter to exit"
}

