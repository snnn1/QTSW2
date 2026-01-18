# Check if RobotCore_For_NinjaTrader files are in sync with modules/robot/core

$mainDir = "modules\robot\core"
$ntDir = "RobotCore_For_NinjaTrader"
$separator = "=" * 80

Write-Host $separator
Write-Host "SYNC STATUS CHECK: RobotCore_For_NinjaTrader vs modules/robot/core"
Write-Host $separator
Write-Host ""

# Files to check
$filesToCheck = @(
    "RobotEngine.cs",
    "StreamStateMachine.cs",
    "Notifications\NotificationService.cs",
    "Notifications\PushoverClient.cs"
)

$differences = @()
$synced = @()

foreach ($file in $filesToCheck) {
    $mainPath = Join-Path $mainDir $file
    $ntPath = Join-Path $ntDir $file
    
    if (-not (Test-Path $mainPath)) {
        Write-Host "[WARN] Main file not found: $mainPath" -ForegroundColor Yellow
        continue
    }
    
    if (-not (Test-Path $ntPath)) {
        Write-Host "[ERROR] NinjaTrader file not found: $ntPath" -ForegroundColor Red
        $differences += @{
            File = $file
            Status = "MISSING_IN_NT"
            MainPath = $mainPath
            NTPath = $ntPath
        }
        continue
    }
    
    # Compare file hashes
    $mainHash = (Get-FileHash $mainPath -Algorithm SHA256).Hash
    $ntHash = (Get-FileHash $ntPath -Algorithm SHA256).Hash
    
    if ($mainHash -eq $ntHash) {
        Write-Host "[OK] $file - Files are identical" -ForegroundColor Green
        $synced += $file
    } else {
        Write-Host "[DIFF] $file - Files differ" -ForegroundColor Red
        
        # Get line counts for context
        $mainLines = (Get-Content $mainPath).Count
        $ntLines = (Get-Content $ntPath).Count
        
        $differences += @{
            File = $file
            Status = "DIFFERENT"
            MainPath = $mainPath
            NTPath = $ntPath
            MainLines = $mainLines
            NTLines = $ntLines
            MainHash = $mainHash
            NTHash = $ntHash
        }
    }
}

Write-Host ""
Write-Host $separator
Write-Host "SUMMARY"
Write-Host $separator
Write-Host ""

if ($synced.Count -gt 0) {
    Write-Host "Synced files ($($synced.Count)):" -ForegroundColor Green
    foreach ($file in $synced) {
        Write-Host "  [OK] $file"
    }
    Write-Host ""
}

if ($differences.Count -gt 0) {
    Write-Host "Files that need syncing ($($differences.Count)):" -ForegroundColor Red
    foreach ($diff in $differences) {
        Write-Host ""
        Write-Host "  [DIFF] $($diff.File)" -ForegroundColor Red
        Write-Host "    Status: $($diff.Status)"
        if ($diff.Status -eq "DIFFERENT") {
            Write-Host "    Main: $($diff.MainLines) lines"
            Write-Host "    NT:   $($diff.NTLines) lines"
            Write-Host "    Main Hash: $($diff.MainHash.Substring(0, 16))..."
            Write-Host "    NT Hash:   $($diff.NTHash.Substring(0, 16))..."
        }
    }
} else {
    Write-Host "[OK] All checked files are in sync!" -ForegroundColor Green
}

Write-Host ""
Write-Host $separator
