# Verify that StreamStateMachine.cs files are in sync
param(
    [switch]$Fix
)

$file1 = "RobotCore_For_NinjaTrader\StreamStateMachine.cs"
$file2 = "modules\robot\core\StreamStateMachine.cs"

if (-not (Test-Path $file1)) {
    Write-Error "File not found: $file1"
    exit 1
}

if (-not (Test-Path $file2)) {
    Write-Error "File not found: $file2"
    exit 1
}

# Compare file contents (ignoring line endings)
$content1 = Get-Content $file1 -Raw
$content2 = Get-Content $file2 -Raw

# Normalize line endings for comparison
$content1 = $content1 -replace "`r`n", "`n" -replace "`r", "`n"
$content2 = $content2 -replace "`r`n", "`n" -replace "`r", "`n"

if ($content1 -eq $content2) {
    Write-Host "Files are in sync."
    exit 0
}

if ($Fix) {
    Write-Host "Syncing files..."
    Copy-Item -Path $file2 -Destination $file1 -Force
    Write-Host "Files synced."
    exit 0
}

Write-Error "Files are out of sync!"
exit 1
