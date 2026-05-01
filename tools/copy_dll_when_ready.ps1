# Script to copy Robot.Core.dll, Robot.Contracts.dll, and dependencies to NinjaTrader Custom folder
# Waits for NinjaTrader to close if DLL is locked
# -NoPause: skip pause at end (for use by deploy script)

param([switch]$NoPause)
$ErrorActionPreference = "Stop"
$projectRoot = if ($PSScriptRoot) { $PSScriptRoot } else { $PWD }
for ($i = 0; $i -lt 6; $i++) {
    if (Test-Path (Join-Path $projectRoot "system\RobotCore_For_NinjaTrader")) { break }
    $parent = Split-Path $projectRoot -Parent
    if ([string]::IsNullOrEmpty($parent) -or $parent -eq $projectRoot) { break }
    $projectRoot = $parent
}
$sourceDir = Join-Path $projectRoot "system\RobotCore_For_NinjaTrader\bin\Release\net48"

# Resolve NinjaTrader Custom path from the Windows Documents known-folder.
# Do not deploy to stale OneDrive mirrors just because they still exist.
$ntCustomDirs = @()
$documentsPath = [Environment]::GetFolderPath("MyDocuments")
if ([string]::IsNullOrWhiteSpace($documentsPath)) {
    $documentsPath = Join-Path $env:USERPROFILE "Documents"
}
foreach ($base in @(
    (Join-Path $documentsPath "NinjaTrader 8\bin\Custom"),
    (Join-Path $env:USERPROFILE "Documents\NinjaTrader 8\bin\Custom")
) | Select-Object -Unique) {
    if (Test-Path $base) {
        $ntCustomDirs += $base
    }
}
if ($ntCustomDirs.Count -eq 0) {
    Write-Host "[ERROR] NinjaTrader Custom folder not found under Windows Documents or local Documents."
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
Write-Host "Destinations:"
foreach ($ntCustom in $ntCustomDirs) {
    Write-Host "  $ntCustom"
}
Write-Host ""

if (-not (Test-Path $sourceDir)) {
    Write-Host "[ERROR] Build output not found. Build first:"
    Write-Host "   dotnet build system\RobotCore_For_NinjaTrader\Robot.Core.csproj -c Release"
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
        foreach ($ntCustom in $ntCustomDirs) {
            Write-Host "Destination: $ntCustom"
            foreach ($f in $filesToCopy) {
                $src = Join-Path $sourceDir $f.Name
                $dst = Join-Path $ntCustom $f.Name
                if (Test-Path $src) {
                    Copy-Item $src $dst -Force -ErrorAction Stop
                    Write-Host "[OK] $($f.Name)"
                }
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
Write-Host "Verifying deployed DLL hashes..." -ForegroundColor Cyan
$verificationFailed = $false
foreach ($ntCustom in $ntCustomDirs) {
    foreach ($f in $filesToCopy) {
        $src = Join-Path $sourceDir $f.Name
        $dst = Join-Path $ntCustom $f.Name
        if (-not (Test-Path $src)) { continue }
        if (-not (Test-Path $dst)) {
            Write-Host "[ERROR] Missing deployed file: $dst" -ForegroundColor Red
            $verificationFailed = $true
            continue
        }

        $srcHash = (Get-FileHash $src -Algorithm SHA256).Hash
        $dstHash = (Get-FileHash $dst -Algorithm SHA256).Hash
        if ($srcHash -ne $dstHash) {
            Write-Host "[ERROR] Hash mismatch for $($f.Name) at $ntCustom" -ForegroundColor Red
            Write-Host "        source: $($srcHash.Substring(0, 24))"
            Write-Host "        target: $($dstHash.Substring(0, 24))"
            $verificationFailed = $true
        } elseif ($f.Name -eq "Robot.Core.dll") {
            $dstItem = Get-Item $dst
            Write-Host "[OK] Robot.Core.dll verified at $ntCustom"
            Write-Host "     hash=$($dstHash.Substring(0, 24)) last_write_utc=$($dstItem.LastWriteTimeUtc.ToString('o'))"
        }
    }
}

if ($verificationFailed) {
    Write-Host ""
    Write-Host "[ERROR] Deploy verification failed. NinjaTrader will keep loading the old DLL until this is fixed." -ForegroundColor Red
    if (-not $NoPause) { pause }
    exit 1
}

Write-Host ""
Write-Host "[OK] All files copied successfully!"
Write-Host ""
Write-Host "IMPORTANT: Restart NinjaTrader to load the new DLLs"
Write-Host ""

if (-not $NoPause) { pause }
