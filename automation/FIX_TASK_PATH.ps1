# Fix Pipeline Runner task to use run_pipeline_standalone.py
# Run this script as Administrator

$ErrorActionPreference = "Stop"

$taskName = "Pipeline Runner"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Fix Pipeline Runner Task Path" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# Check if running as admin
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[ERROR] This script must be run as Administrator!" -ForegroundColor Red
    Write-Host "Right-click this file and select 'Run as administrator'" -ForegroundColor Yellow
    exit 1
}

# Check if task exists
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if (-not $task) {
    Write-Host "[ERROR] Task '$taskName' not found!" -ForegroundColor Red
    Write-Host "Please run: batch\SETUP_WINDOWS_SCHEDULER.bat" -ForegroundColor Yellow
    exit 1
}

Write-Host "Current task configuration:" -ForegroundColor Yellow
Write-Host "  Execute: $($task.Actions.Execute)" -ForegroundColor Gray
Write-Host "  Arguments: $($task.Actions.Arguments)" -ForegroundColor Gray
Write-Host "  Working Directory: $($task.Actions.WorkingDirectory)" -ForegroundColor Gray
Write-Host ""

# Get Python path from current task
$pythonPath = $task.Actions.Execute
$workingDir = $task.Actions.WorkingDirectory

# Create new action with correct script path
Write-Host "Updating task to use run_pipeline_standalone.py..." -ForegroundColor Yellow
$newAction = New-ScheduledTaskAction `
    -Execute $pythonPath `
    -Argument "automation\run_pipeline_standalone.py" `
    -WorkingDirectory $workingDir

# Update the task (keep all other settings)
Set-ScheduledTask -TaskName $taskName -Action $newAction -ErrorAction Stop

Write-Host "[OK] Task updated successfully!" -ForegroundColor Green
Write-Host ""

# Verify the change
$updatedTask = Get-ScheduledTask -TaskName $taskName
Write-Host "Updated task configuration:" -ForegroundColor Cyan
Write-Host "  Execute: $($updatedTask.Actions.Execute)" -ForegroundColor Gray
Write-Host "  Arguments: $($updatedTask.Actions.Arguments)" -ForegroundColor Gray
Write-Host "  Working Directory: $($updatedTask.Actions.WorkingDirectory)" -ForegroundColor Gray
Write-Host ""

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Task Path Fixed!" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Test the task: Right-click 'Pipeline Runner' in Task Scheduler" -ForegroundColor Gray
Write-Host "  2. Select 'Run' and check Last Run Result (should be 0x0)" -ForegroundColor Gray
Write-Host ""













