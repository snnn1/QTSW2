# PowerShell script to prevent Windows Task Scheduler from auto-disabling the task
# Run this script as Administrator

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  Prevent Task Auto-Disable Configuration" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan
Write-Host ""

$taskName = "Pipeline Runner"

# Check if running as administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[ERROR] This script must be run as Administrator!" -ForegroundColor Red
    Write-Host ""
    Write-Host "To fix:" -ForegroundColor Yellow
    Write-Host "  1. Right-click this script" -ForegroundColor Gray
    Write-Host "  2. Select 'Run as administrator'" -ForegroundColor Gray
    Write-Host "  3. Click Yes when prompted" -ForegroundColor Gray
    Write-Host ""
    exit 1
}

# Get the task
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if (-not $task) {
    Write-Host "[ERROR] Task '$taskName' not found!" -ForegroundColor Red
    Write-Host "Please create the task first using setup_task_scheduler.ps1" -ForegroundColor Yellow
    exit 1
}

Write-Host "Found task: $taskName" -ForegroundColor Green
Write-Host ""

# Get current settings
$currentSettings = $task.Settings
Write-Host "Current Settings:" -ForegroundColor Yellow
Write-Host "  ExecutionTimeLimit: $($currentSettings.ExecutionTimeLimit)" -ForegroundColor Gray
Write-Host "  RestartCount: $($currentSettings.RestartCount)" -ForegroundColor Gray
Write-Host "  RestartInterval: $($currentSettings.RestartInterval)" -ForegroundColor Gray
Write-Host "  DeleteExpiredTaskAfter: $($currentSettings.DeleteExpiredTaskAfter)" -ForegroundColor Gray
Write-Host ""

# Create new settings that prevent auto-disable
Write-Host "Configuring settings to prevent auto-disable..." -ForegroundColor Yellow

# Key settings to prevent auto-disable:
# 1. DeleteExpiredTaskAfter = Never (PT0S means never delete)
# 2. RestartCount = High but reasonable number (100) - prevents auto-disable but not infinite
#    NOTE: Application has its own retry logic (max_retries=3 per stage). This is for process-level failures.
# 3. RestartInterval = Short interval (5 minutes) - gives system time to recover
# 4. ExecutionTimeLimit = Long enough (2 hours is fine)
# 5. MultipleInstances = IgnoreNew (don't kill existing instances)

$newSettings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 100 `  # High enough to prevent auto-disable, but not infinite (application has own retry logic)
    -RestartInterval (New-TimeSpan -Minutes 5) `
    -ExecutionTimeLimit (New-TimeSpan -Hours 2) `
    -MultipleInstances IgnoreNew `
    -DeleteExpiredTaskAfter (New-TimeSpan -Seconds 0) `  # Never delete (PT0S)
    -StopTaskIfRunsLongerThan (New-TimeSpan -Hours 2)

Write-Host "New Settings:" -ForegroundColor Green
Write-Host "  ExecutionTimeLimit: $($newSettings.ExecutionTimeLimit)" -ForegroundColor Gray
Write-Host "  RestartCount: $($newSettings.RestartCount) (high enough to prevent auto-disable, app has own retries)" -ForegroundColor Gray
Write-Host "  RestartInterval: $($newSettings.RestartInterval)" -ForegroundColor Gray
Write-Host "  DeleteExpiredTaskAfter: $($newSettings.DeleteExpiredTaskAfter) (Never)" -ForegroundColor Gray
Write-Host ""
Write-Host "Note: This prevents Windows auto-disable. Application has its own retry logic:" -ForegroundColor Yellow
Write-Host "  - Each stage retries up to 3 times (configurable)" -ForegroundColor Gray
Write-Host "  - Watchdog monitors for hung runs" -ForegroundColor Gray
Write-Host "  - Persistent failures will be logged and visible in dashboard" -ForegroundColor Gray
Write-Host "  MultipleInstances: $($newSettings.MultipleInstances)" -ForegroundColor Gray
Write-Host ""

# Apply the new settings
try {
    Set-ScheduledTask -TaskName $taskName -Settings $newSettings -ErrorAction Stop
    Write-Host "[OK] Settings updated successfully!" -ForegroundColor Green
    Write-Host ""
    
    # Verify the changes
    $updatedTask = Get-ScheduledTask -TaskName $taskName
    $updatedSettings = $updatedTask.Settings
    Write-Host "Verification:" -ForegroundColor Cyan
    Write-Host "  RestartCount: $($updatedSettings.RestartCount)" -ForegroundColor Gray
    Write-Host "  DeleteExpiredTaskAfter: $($updatedSettings.DeleteExpiredTaskAfter)" -ForegroundColor Gray
    Write-Host ""
    
    Write-Host "================================================" -ForegroundColor Cyan
    Write-Host "  Configuration Complete" -ForegroundColor Green
    Write-Host "================================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "The task is now configured to:" -ForegroundColor Yellow
    Write-Host "  ✓ Never auto-delete (DeleteExpiredTaskAfter = Never)" -ForegroundColor Green
    Write-Host "  ✓ Restart up to 100 times on process failure (prevents Windows auto-disable)" -ForegroundColor Green
    Write-Host "  ✓ Wait 5 minutes between restart attempts" -ForegroundColor Green
    Write-Host "  ✓ Allow 2 hours execution time limit" -ForegroundColor Green
    Write-Host ""
    Write-Host "Two-Layer Retry Strategy:" -ForegroundColor Cyan
    Write-Host "  1. Application-level: Each stage retries up to 3 times (handles transient errors)" -ForegroundColor Gray
    Write-Host "  2. Process-level: Windows restarts process up to 100 times (handles crashes)" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Windows Task Scheduler will no longer auto-disable this task." -ForegroundColor Green
    Write-Host "Persistent failures will be logged in dashboard and event logs." -ForegroundColor Green
    Write-Host ""
    
} catch {
    Write-Host "[ERROR] Failed to update settings: $_" -ForegroundColor Red
    Write-Host ""
    Write-Host "You may need to:" -ForegroundColor Yellow
    Write-Host "  1. Run this script as Administrator" -ForegroundColor Gray
    Write-Host "  2. Check Task Scheduler permissions" -ForegroundColor Gray
    Write-Host "  3. Manually configure in Task Scheduler GUI (taskschd.msc)" -ForegroundColor Gray
    Write-Host ""
    exit 1
}

