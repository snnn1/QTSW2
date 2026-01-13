# Sync Robot.Core source files to RobotCore_For_NinjaTrader directory
# This ensures NinjaTrader always has the latest source files
#
# IMPORTANT: StreamStateMachine.cs MUST remain synchronized between:
#   - modules/robot/core/StreamStateMachine.cs (source - edit here)
#   - RobotCore_For_NinjaTrader/StreamStateMachine.cs (copy - auto-synced)
# Always edit the source file, never edit the copy directly!

param(
    [switch]$WhatIf,
    [switch]$Verbose
)

$ErrorActionPreference = "Stop"

$sourceDir = "modules\robot\core"
$targetDir = "RobotCore_For_NinjaTrader"

if (-not (Test-Path $sourceDir)) {
    Write-Error "Source directory not found: $sourceDir"
    exit 1
}

if (-not (Test-Path $targetDir)) {
    Write-Error "Target directory not found: $targetDir"
    exit 1
}

Write-Host "Syncing Robot.Core files from $sourceDir to $targetDir..." -ForegroundColor Cyan

# Files to sync (excluding build artifacts and project files)
$filesToSync = @(
    "DateOnlyCompat.cs",
    "ExecutionMode.cs",
    "FilePoller.cs",
    "HealthMonitor.cs",
    "HealthMonitorConfig.cs",
    "IBarProvider.cs",
    "IsExternalInitCompat.cs",
    "JournalStore.cs",
    "JsonUtil.cs",
    "LoggingConfig.cs",
    "Models.ParitySpec.cs",
    "Models.TimetableContract.cs",
    "NinjaTraderExtensions.cs",
    "ProjectRootResolver.cs",
    "RobotEngine.cs",
    "RobotLogEvent.cs",
    "RobotLogger.cs",
    "RobotLoggingService.cs",
    "StreamStateMachine.cs",
    "TickRounding.cs",
    "TimeService.cs"
)

# Execution subdirectory files
$executionFiles = @(
    "ExecutionAdapterFactory.cs",
    "ExecutionJournal.cs",
    "ExecutionSummary.cs",
    "FlattenResult.cs",
    "IExecutionAdapter.cs",
    "Intent.cs",
    "KillSwitch.cs",
    "NinjaTraderLiveAdapter.cs",
    "NinjaTraderSimAdapter.cs",
    "NinjaTraderSimAdapter.NT.cs",
    "NullExecutionAdapter.cs",
    "OrderModificationResult.cs",
    "OrderSubmissionResult.cs",
    "RiskGate.cs"
)

# Notifications subdirectory files
$notificationFiles = @(
    "NotificationService.cs",
    "PushoverClient.cs"
)

# NinjaTrader-specific files (don't sync these, they're unique to NT)
$ninjatraderSpecificFiles = @(
    "NinjaTraderBarProvider.cs",
    "NinjaTraderBarProviderWrapper.cs",
    "SnapshotParquetBarProvider.cs"
)

$syncedCount = 0
$skippedCount = 0
$errorCount = 0

# Sync main directory files
foreach ($file in $filesToSync) {
    $sourcePath = Join-Path $sourceDir $file
    $targetPath = Join-Path $targetDir $file
    
    if (-not (Test-Path $sourcePath)) {
        Write-Warning "Source file not found: $sourcePath"
        $skippedCount++
        continue
    }
    
    try {
        if ($WhatIf) {
            Write-Host "[WHATIF] Would copy: $file" -ForegroundColor Yellow
        } else {
            Copy-Item -Path $sourcePath -Destination $targetPath -Force
            if ($Verbose) {
                Write-Host "Synced: $file" -ForegroundColor Green
            }
            $syncedCount++
        }
    } catch {
        Write-Error "Failed to sync $file : $_"
        $errorCount++
    }
}

# Sync Execution subdirectory
$executionSourceDir = Join-Path $sourceDir "Execution"
$executionTargetDir = Join-Path $targetDir "Execution"

if (-not (Test-Path $executionTargetDir)) {
    New-Item -ItemType Directory -Path $executionTargetDir -Force | Out-Null
}

foreach ($file in $executionFiles) {
    $sourcePath = Join-Path $executionSourceDir $file
    $targetPath = Join-Path $executionTargetDir $file
    
    if (-not (Test-Path $sourcePath)) {
        Write-Warning "Source file not found: $sourcePath"
        $skippedCount++
        continue
    }
    
    try {
        if ($WhatIf) {
            Write-Host "[WHATIF] Would copy: Execution\$file" -ForegroundColor Yellow
        } else {
            Copy-Item -Path $sourcePath -Destination $targetPath -Force
            if ($Verbose) {
                Write-Host "Synced: Execution\$file" -ForegroundColor Green
            }
            $syncedCount++
        }
    } catch {
        Write-Error "Failed to sync Execution\$file : $_"
        $errorCount++
    }
}

# Sync Notifications subdirectory
$notificationsSourceDir = Join-Path $sourceDir "Notifications"
$notificationsTargetDir = Join-Path $targetDir "Notifications"

if (-not (Test-Path $notificationsTargetDir)) {
    New-Item -ItemType Directory -Path $notificationsTargetDir -Force | Out-Null
}

foreach ($file in $notificationFiles) {
    $sourcePath = Join-Path $notificationsSourceDir $file
    $targetPath = Join-Path $notificationsTargetDir $file
    
    if (-not (Test-Path $sourcePath)) {
        Write-Warning "Source file not found: $sourcePath"
        $skippedCount++
        continue
    }
    
    try {
        if ($WhatIf) {
            Write-Host "[WHATIF] Would copy: Notifications\$file" -ForegroundColor Yellow
        } else {
            Copy-Item -Path $sourcePath -Destination $targetPath -Force
            if ($Verbose) {
                Write-Host "Synced: Notifications\$file" -ForegroundColor Green
            }
            $syncedCount++
        }
    } catch {
        Write-Error "Failed to sync Notifications\$file : $_"
        $errorCount++
    }
}

# Summary
Write-Host "`nSync Summary:" -ForegroundColor Cyan
Write-Host "  Synced: $syncedCount files" -ForegroundColor Green
Write-Host "  Skipped: $skippedCount files" -ForegroundColor Yellow
if ($errorCount -gt 0) {
    Write-Host "  Errors: $errorCount files" -ForegroundColor Red
    exit 1
}

if (-not $WhatIf) {
    Write-Host "`nSync complete! RobotCore_For_NinjaTrader is now synchronized with modules\robot\core" -ForegroundColor Green
}
