# Copy RobotSimStrategy.cs to NinjaTrader Strategies folder
$ntPath = "$env:USERPROFILE\Documents\NinjaTrader 8\bin\Custom\Strategies"
$source = "modules\robot\ninjatrader\RobotSimStrategy.cs"
$dest = Join-Path $ntPath "RobotSimStrategy.cs"

if (-not (Test-Path $ntPath)) {
    New-Item -ItemType Directory -Path $ntPath -Force | Out-Null
    Write-Host "Created directory: $ntPath"
}

if (Test-Path $source) {
    Copy-Item $source -Destination $dest -Force
    Write-Host "Successfully copied $source to $dest"
    Get-Item $dest | Select-Object FullName, LastWriteTime
} else {
    Write-Host "ERROR: Source file not found: $source"
    exit 1
}
