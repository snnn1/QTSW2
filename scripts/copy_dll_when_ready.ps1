# Script to copy Robot.Core.dll, Robot.Contracts.dll, and dependencies to NinjaTrader Custom folder
# Waits for NinjaTrader to close if DLL is locked
# -NoPause: skip pause at end (for use by deploy script)

param([switch]$NoPause)
$ErrorActionPreference = "Stop"
$projectRoot = if ($PSScriptRoot) { $PSScriptRoot } else { $PWD }
$sourceDir = Join-Path $projectRoot "RobotCore_For_NinjaTrader\bin\Release\net48"

# Resolve NinjaTrader Custom path (Documents or OneDrive\Documents)
$ntCustom = $null
foreach ($base in @(
    (Join-Path $env:USERPROFILE "OneDrive\Documents\NinjaTrader 8\bin\Custom"),
    (Join-Path $env:USERPROFILE "Documents\NinjaTrader 8\bin\Custom")
)) {
    if (Test-Path $base) {
        $ntCustom = $base
        break
    }
}
if (-not $ntCustom) {
    Write-Host "[ERROR] NinjaTrader Custom folder not found. Tried OneDrive\Documents and Documents."
    pause
    exit 1
}

# Files to copy: Robot.Core, Robot.Contracts, and runtime deps NinjaTrader may not have
$filesToCopy = @(
    @{ Name = "Robot.Core.dll"; Required = $true },
    @{ Name = "Robot.Core.pdb"; Required = $false },
    @{ Name = "Robot.Contracts.dll"; Required = $true },
    @{ Name = "Robot.Contracts.pdb"; Required = $false },
    @{ Name = "System.Text.Json.dll"; Required = $true },
    @{ Name = "System.Text.Encodings.Web.dll"; Required = $true },
    @{ Name = "System.Buffers.dll"; Required = $true },
    @{ Name = "System.Memory.dll"; Required = $true },
    @{ Name = "System.Numerics.Vectors.dll"; Required = $true },
    @{ Name = "System.Runtime.CompilerServices.Unsafe.dll"; Required = $true },
    @{ Name = "System.Threading.Tasks.Extensions.dll"; Required = $true },
    @{ Name = "System.ValueTuple.dll"; Required = $true },
    @{ Name = "Microsoft.Bcl.AsyncInterfaces.dll"; Required = $true }
)

Write-Host "============================================================"
Write-Host "  Copying Robot.Core + dependencies to NinjaTrader"
Write-Host "============================================================"
Write-Host ""
Write-Host "Source: $sourceDir"
Write-Host "Destination: $ntCustom"
Write-Host ""

if (-not (Test-Path $sourceDir)) {
    Write-Host "[ERROR] Build output not found. Build first:"
    Write-Host "   dotnet build RobotCore_For_NinjaTrader\Robot.Core.csproj -c Release"
    pause
    exit 1
}

foreach ($f in $filesToCopy) {
    if ($f.Required -and -not (Test-Path (Join-Path $sourceDir $f.Name))) {
        Write-Host "[ERROR] Required file not found: $($f.Name)"
        pause
        exit 1
    }
}

# Try to copy, wait if locked
$maxAttempts = 30
$attempt = 0
$copied = $false

while ($attempt -lt $maxAttempts -and -not $copied) {
    $attempt++
    try {
        foreach ($f in $filesToCopy) {
            $src = Join-Path $sourceDir $f.Name
            $dst = Join-Path $ntCustom $f.Name
            if (Test-Path $src) {
                Copy-Item $src $dst -Force -ErrorAction Stop
                Write-Host "[OK] $($f.Name)"
            }
        }
        $copied = $true
    }
    catch {
        if ($_.Exception.Message -match "being used by another process") {
            if ($attempt -eq 1) {
                Write-Host "[WAIT] DLL is locked by NinjaTrader. Waiting for it to close..."
            }
            Write-Host "  Attempt $attempt/$maxAttempts - locked, waiting 2 seconds..."
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
    Write-Host "[ERROR] Could not copy after $maxAttempts attempts. Close NinjaTrader and try again."
    pause
    exit 1
}

Write-Host ""
Write-Host "[OK] All files copied successfully!"
Write-Host ""
Write-Host "IMPORTANT: Restart NinjaTrader to load the new DLLs"
Write-Host ""

if (-not $NoPause) { pause }
