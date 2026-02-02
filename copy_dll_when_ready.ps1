# Script to copy Robot.Core.dll to NinjaTrader Custom folder
# Waits for NinjaTrader to close if DLL is locked

$source = "RobotCore_For_NinjaTrader\bin\Release\net48\Robot.Core.dll"
$dest = "$env:USERPROFILE\OneDrive\Documents\NinjaTrader 8\bin\Custom\Robot.Core.dll"
$pdbSource = "RobotCore_For_NinjaTrader\bin\Release\net48\Robot.Core.pdb"
$pdbDest = "$env:USERPROFILE\OneDrive\Documents\NinjaTrader 8\bin\Custom\Robot.Core.pdb"

Write-Host "============================================================"
Write-Host "  Copying Robot.Core.dll to NinjaTrader"
Write-Host "============================================================"
Write-Host ""

if (-not (Test-Path $source)) {
    Write-Host "[ERROR] Source DLL not found: $source"
    pause
    exit 1
}

Write-Host "Source: $source"
Write-Host "Destination: $dest"
Write-Host ""

# Check if destination directory exists
$destDir = Split-Path $dest
if (-not (Test-Path $destDir)) {
    Write-Host "[ERROR] Destination directory does not exist: $destDir"
    pause
    exit 1
}

# Try to copy, wait if locked
$maxAttempts = 30
$attempt = 0
$copied = $false

while ($attempt -lt $maxAttempts -and -not $copied) {
    $attempt++
    try {
        Copy-Item $source $dest -Force -ErrorAction Stop
        Write-Host "[OK] Copied DLL successfully (attempt $attempt)"
        $copied = $true
        
        # Copy PDB if it exists
        if (Test-Path $pdbSource) {
            Copy-Item $pdbSource $pdbDest -Force -ErrorAction Stop
            Write-Host "[OK] Copied PDB successfully"
        }
    }
    catch {
        if ($_.Exception.Message -match "being used by another process") {
            if ($attempt -eq 1) {
                Write-Host "[WAIT] DLL is locked by NinjaTrader. Waiting for it to close..."
            }
            Write-Host "  Attempt $attempt/$maxAttempts - DLL still locked, waiting 2 seconds..."
            Start-Sleep -Seconds 2
        }
        else {
            Write-Host "[ERROR] Failed to copy: $_"
            pause
            exit 1
        }
    }
}

if (-not $copied) {
    Write-Host ""
    Write-Host "[ERROR] Could not copy DLL after $maxAttempts attempts."
    Write-Host "Please close NinjaTrader manually and run this script again."
    pause
    exit 1
}

Write-Host ""
Write-Host "[OK] DLL copied successfully!"
Write-Host ""
Write-Host "IMPORTANT: Restart NinjaTrader to load the new DLL"
Write-Host ""
Write-Host "The new DLL includes:"
Write-Host "  - Recovery guard check for protective orders"
Write-Host "  - Order rejection handling (flatten on rejection)"
Write-Host "  - Updated execution callbacks"
Write-Host ""

pause
