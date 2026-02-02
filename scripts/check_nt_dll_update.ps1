# Check if Robot.Core.dll in NinjaTrader bin needs to be updated
# Compares the built DLL with the one in NinjaTrader's Custom folder

$ErrorActionPreference = "Stop"

# Get project root - script is in scripts/ folder, project root is parent
$projectRoot = if ($PSScriptRoot) { Split-Path -Parent $PSScriptRoot } else { $PWD }
$sourceDll = Join-Path $projectRoot "RobotCore_For_NinjaTrader\bin\Release\net48\Robot.Core.dll"
$targetDll = Join-Path $env:USERPROFILE "Documents\NinjaTrader 8\bin\Custom\Robot.Core.dll"

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  Checking Robot.Core.dll Update Status" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan
Write-Host ""

# Check if source DLL exists
if (-not (Test-Path $sourceDll)) {
    Write-Host "❌ Source DLL not found!" -ForegroundColor Red
    Write-Host "   Path: $sourceDll" -ForegroundColor Gray
    Write-Host ""
    Write-Host "💡 Build the project first:" -ForegroundColor Yellow
    Write-Host "   dotnet build RobotCore_For_NinjaTrader\Robot.Core.csproj --configuration Release" -ForegroundColor Gray
    exit 1
}

# Check if target DLL exists
if (-not (Test-Path $targetDll)) {
    Write-Host "⚠️  Target DLL not found in NinjaTrader bin!" -ForegroundColor Yellow
    Write-Host "   Path: $targetDll" -ForegroundColor Gray
    Write-Host ""
    Write-Host "💡 DLL needs to be copied to NinjaTrader." -ForegroundColor Yellow
    Write-Host ""
    $copy = Read-Host "Copy DLL now? (y/n)"
    if ($copy -eq "y" -or $copy -eq "Y") {
        $targetDir = Split-Path $targetDll -Parent
        if (-not (Test-Path $targetDir)) {
            New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
        }
        Copy-Item $sourceDll $targetDll -Force
        Write-Host "✅ DLL copied successfully!" -ForegroundColor Green
    }
    exit 0
}

# Get file information
$sourceInfo = Get-Item $sourceDll
$targetInfo = Get-Item $targetDll

Write-Host "Source DLL (Built):" -ForegroundColor Yellow
Write-Host "  Path: $sourceDll" -ForegroundColor Gray
Write-Host "  Size: $([math]::Round($sourceInfo.Length / 1KB, 2)) KB" -ForegroundColor Gray
Write-Host "  Modified: $($sourceInfo.LastWriteTime)" -ForegroundColor Gray
Write-Host ""

Write-Host "Target DLL (NinjaTrader):" -ForegroundColor Yellow
Write-Host "  Path: $targetDll" -ForegroundColor Gray
Write-Host "  Size: $([math]::Round($targetInfo.Length / 1KB, 2)) KB" -ForegroundColor Gray
Write-Host "  Modified: $($targetInfo.LastWriteTime)" -ForegroundColor Gray
Write-Host ""

# Compare versions using .NET reflection
try {
    Add-Type -AssemblyName System.Reflection
    
    $sourceAssembly = [System.Reflection.Assembly]::LoadFrom($sourceDll)
    $targetAssembly = [System.Reflection.Assembly]::LoadFrom($targetDll)
    
    $sourceVersion = $sourceAssembly.GetName().Version
    $targetVersion = $targetAssembly.GetName().Version
    
    Write-Host "Version Comparison:" -ForegroundColor Yellow
    Write-Host "  Source: $sourceVersion" -ForegroundColor Gray
    Write-Host "  Target: $targetVersion" -ForegroundColor Gray
    Write-Host ""
    
    # Compare versions
    if ($sourceVersion -gt $targetVersion) {
        Write-Host "⚠️  Source DLL is NEWER (version $sourceVersion > $targetVersion)" -ForegroundColor Yellow
        $needsUpdate = $true
    } elseif ($sourceVersion -lt $targetVersion) {
        Write-Host "⚠️  Target DLL is NEWER (version $targetVersion > $sourceVersion)" -ForegroundColor Yellow
        Write-Host "   This is unusual - target may have been manually modified." -ForegroundColor Gray
        $needsUpdate = $false
    } else {
        Write-Host "✅ Versions match ($sourceVersion)" -ForegroundColor Green
        $needsUpdate = $false
    }
} catch {
    Write-Host "⚠️  Could not compare versions: $($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host "   Falling back to file date comparison..." -ForegroundColor Gray
    $needsUpdate = $null
}

# Compare file dates
if ($needsUpdate -eq $null) {
    Write-Host ""
    Write-Host "File Date Comparison:" -ForegroundColor Yellow
    $timeDiff = $sourceInfo.LastWriteTime - $targetInfo.LastWriteTime
    
    if ($timeDiff.TotalSeconds -gt 0) {
        Write-Host "  Source is $([math]::Round($timeDiff.TotalMinutes, 1)) minutes NEWER" -ForegroundColor Yellow
        $needsUpdate = $true
    } elseif ($timeDiff.TotalSeconds -lt 0) {
        Write-Host "  Target is $([math]::Round([math]::Abs($timeDiff.TotalMinutes), 1)) minutes NEWER" -ForegroundColor Yellow
        $needsUpdate = $false
    } else {
        Write-Host "  Files have same modification time" -ForegroundColor Green
        $needsUpdate = $false
    }
}

# Compare file sizes
if ($sourceInfo.Length -ne $targetInfo.Length) {
    Write-Host ""
    Write-Host "⚠️  File sizes differ:" -ForegroundColor Yellow
    Write-Host "  Source: $($sourceInfo.Length) bytes" -ForegroundColor Gray
    Write-Host "  Target: $($targetInfo.Length) bytes" -ForegroundColor Gray
    if ($needsUpdate -eq $false) {
        $needsUpdate = $true
    }
}

Write-Host ""
Write-Host "================================================" -ForegroundColor Cyan

if ($needsUpdate) {
    Write-Host "❌ UPDATE NEEDED" -ForegroundColor Red
    Write-Host ""
    Write-Host "The DLL in NinjaTrader bin is outdated." -ForegroundColor Yellow
    Write-Host ""
    $copy = Read-Host "Copy updated DLL to NinjaTrader? (y/n)"
    if ($copy -eq "y" -or $copy -eq "Y") {
        try {
            Copy-Item $sourceDll $targetDll -Force
            Write-Host ""
            Write-Host "✅ DLL updated successfully!" -ForegroundColor Green
            Write-Host "   You may need to restart NinjaTrader for changes to take effect." -ForegroundColor Yellow
        } catch {
            Write-Host ""
            Write-Host "❌ Failed to copy DLL: $($_.Exception.Message)" -ForegroundColor Red
            Write-Host "   Make sure NinjaTrader is closed." -ForegroundColor Yellow
            exit 1
        }
    }
} else {
    Write-Host "✅ DLL is UP TO DATE" -ForegroundColor Green
    Write-Host ""
    Write-Host "No update needed. The DLL in NinjaTrader matches the built version." -ForegroundColor Gray
}
