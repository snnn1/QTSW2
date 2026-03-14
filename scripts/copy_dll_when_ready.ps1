# Script to copy Robot.Core.dll, Robot.Contracts.dll, and dependencies to NinjaTrader Custom folder
# Uses atomic copy (write to .tmp, then rename) so NinjaTrader never reads a partially copied DLL.
# NinjaTrader must be closed before deploying.
# -NoPause: skip pause at end (for use by deploy script)

param([switch]$NoPause)
$ErrorActionPreference = "Stop"
$projectRoot = if ($PSScriptRoot) { $PSScriptRoot } else { $PWD }
if (-not (Test-Path (Join-Path $projectRoot "RobotCore_For_NinjaTrader"))) {
    $projectRoot = Split-Path $projectRoot -Parent
}
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
    if (-not $NoPause) { pause }
    exit 1
}

# Ensure NinjaTrader is not running
$ntProcess = Get-Process -Name "NinjaTrader" -ErrorAction SilentlyContinue
if ($ntProcess) {
    Write-Host "[ERROR] NinjaTrader must be closed before deploying Robot DLLs." -ForegroundColor Red
    if (-not $NoPause) { pause }
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

Write-Host "Source: $sourceDir"
Write-Host "Destination: $ntCustom"
Write-Host ""

if (-not (Test-Path $sourceDir)) {
    Write-Host "[ERROR] Build output not found. Build first:"
    Write-Host "   dotnet build RobotCore_For_NinjaTrader\Robot.Core.csproj -c Release"
    if (-not $NoPause) { pause }
    exit 1
}

foreach ($f in $filesToCopy) {
    if ($f.Required -and -not (Test-Path (Join-Path $sourceDir $f.Name))) {
        Write-Host "[ERROR] Required file not found: $($f.Name)"
        if (-not $NoPause) { pause }
        exit 1
    }
}

# Atomic copy: write to .tmp, then rename (Rename-Item is atomic on NTFS)
foreach ($f in $filesToCopy) {
    $src = Join-Path $sourceDir $f.Name
    if (-not (Test-Path $src)) { continue }
    $dst = Join-Path $ntCustom $f.Name
    $dstTmp = Join-Path $ntCustom ($f.Name + ".tmp")
    try {
        if (Test-Path $dst) { Remove-Item $dst -Force -ErrorAction Stop }
        Copy-Item $src $dstTmp -Force -ErrorAction Stop
        Rename-Item -Path $dstTmp -NewName $f.Name -Force -ErrorAction Stop
        Write-Host "[OK] $($f.Name)"
    } catch {
        if (Test-Path $dstTmp) { Remove-Item $dstTmp -Force -ErrorAction SilentlyContinue }
        Write-Host "[ERROR] Failed to copy $($f.Name): $_"
        if (-not $NoPause) { pause }
        exit 1
    }
}

Write-Host ""
Write-Host "[OK] All files copied successfully (atomic swap)."
Write-Host ""
Write-Host "IMPORTANT: Restart NinjaTrader to load the new DLLs"
Write-Host ""

if (-not $NoPause) { pause }
