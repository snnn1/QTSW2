# Verify that StreamStateMachine.cs files are synchronized
# This script checks if the two files are identical

param(
    [switch]$Fix,
    [switch]$Verbose
)

$ErrorActionPreference = "Stop"

$sourceFile = "modules\robot\core\StreamStateMachine.cs"
$targetFile = "RobotCore_For_NinjaTrader\StreamStateMachine.cs"

if (-not (Test-Path $sourceFile)) {
    Write-Error "Source file not found: $sourceFile"
    exit 1
}

if (-not (Test-Path $targetFile)) {
    Write-Error "Target file not found: $targetFile"
    exit 1
}

Write-Host "Verifying StreamStateMachine.cs synchronization..." -ForegroundColor Cyan

# Compare file contents
$sourceContent = Get-Content $sourceFile -Raw
$targetContent = Get-Content $targetFile -Raw

if ($sourceContent -eq $targetContent) {
    Write-Host "Files are synchronized" -ForegroundColor Green
    exit 0
} else {
    Write-Host "Files are OUT OF SYNC!" -ForegroundColor Red
    Write-Host ""
    Write-Host "Source: $sourceFile" -ForegroundColor Yellow
    Write-Host "Target: $targetFile" -ForegroundColor Yellow
    Write-Host ""
    
    if ($Fix) {
        Write-Host "Fixing synchronization..." -ForegroundColor Cyan
        Copy-Item -Path $sourceFile -Destination $targetFile -Force
        Write-Host "Files synchronized" -ForegroundColor Green
        exit 0
    } else {
        Write-Host "Run with -Fix to automatically sync:" -ForegroundColor Yellow
        Write-Host "  .\verify_sync.ps1 -Fix" -ForegroundColor White
        Write-Host ""
        Write-Host "Or run the full sync script:" -ForegroundColor Yellow
        Write-Host "  .\sync_robotcore_to_ninjatrader.ps1" -ForegroundColor White
        exit 1
    }
}
