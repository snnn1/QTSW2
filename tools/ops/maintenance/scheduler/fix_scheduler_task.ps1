# Fix Windows Task Scheduler - Update script path
# Run this as Administrator

$taskName = "Pipeline Runner"
$workingDir = "C:\Users\jakej\QTSW2"
$correctScript = "automation\run_pipeline_standalone.py"

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  Fixing Pipeline Runner Task" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan
Write-Host ""

# Check if task exists
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if (-not $task) {
    Write-Host "❌ Task '$taskName' not found!" -ForegroundColor Red
    Write-Host "   Run: batch\SETUP_WINDOWS_SCHEDULER.bat" -ForegroundColor Yellow
    exit 1
}

Write-Host "✅ Found task: '$taskName'" -ForegroundColor Green
Write-Host ""

# Get current action
$currentAction = $task.Actions[0]
Write-Host "Current configuration:" -ForegroundColor Yellow
Write-Host "  Executable: $($currentAction.Execute)" -ForegroundColor Gray
Write-Host "  Arguments: $($currentAction.Arguments)" -ForegroundColor Gray
Write-Host "  Working Directory: $($currentAction.WorkingDirectory)" -ForegroundColor Gray
Write-Host ""

# Check if it's already correct
if ($currentAction.Arguments -eq $correctScript) {
    Write-Host "✅ Task is already configured correctly!" -ForegroundColor Green
    exit 0
}

Write-Host "Updating task to use correct script: $correctScript" -ForegroundColor Yellow

# Create new action with correct script
$newAction = New-ScheduledTaskAction `
    -Execute $currentAction.Execute `
    -Argument $correctScript `
    -WorkingDirectory $workingDir

# Update the task
try {
    Set-ScheduledTask -TaskName $taskName -Action $newAction -ErrorAction Stop
    Write-Host "✅ Task updated successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "New configuration:" -ForegroundColor Cyan
    Write-Host "  Executable: $($newAction.Execute)" -ForegroundColor Gray
    Write-Host "  Arguments: $($newAction.Arguments)" -ForegroundColor Gray
    Write-Host "  Working Directory: $($newAction.WorkingDirectory)" -ForegroundColor Gray
    Write-Host ""
    Write-Host "The task should now work correctly." -ForegroundColor Green
} catch {
    Write-Host "❌ Failed to update task: $_" -ForegroundColor Red
    Write-Host ""
    Write-Host "This script must be run as Administrator!" -ForegroundColor Yellow
    exit 1
}



