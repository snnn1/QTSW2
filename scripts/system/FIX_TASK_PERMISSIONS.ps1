# Fix Task Permissions - Make task modifiable by current user
# Run this as Administrator

$taskName = "Pipeline Runner"

Write-Host "Fixing permissions for task: $taskName" -ForegroundColor Yellow

# Get the task
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue

if (-not $task) {
    Write-Host "[ERROR] Task '$taskName' not found!" -ForegroundColor Red
    exit 1
}

Write-Host "Current task owner: $($task.Principal.UserId)" -ForegroundColor Gray
Write-Host "Current RunLevel: $($task.Principal.RunLevel)" -ForegroundColor Gray

# Get task definition
$taskDef = Get-ScheduledTask -TaskName $taskName

# Create new principal with current user and Limited privileges
$newPrincipal = New-ScheduledTaskPrincipal `
    -UserId "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType S4U `
    -RunLevel Limited

# Update the task with new principal
Write-Host "Updating task with Limited privileges and current user..." -ForegroundColor Yellow
try {
    Set-ScheduledTask -TaskName $taskName -Principal $newPrincipal -ErrorAction Stop
    Write-Host "[OK] Task permissions updated successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "The dashboard button should now work!" -ForegroundColor Cyan
} catch {
    Write-Host "[ERROR] Failed to update task: $_" -ForegroundColor Red
    Write-Host ""
    Write-Host "You may need to delete and recreate the task." -ForegroundColor Yellow
    exit 1
}

# Verify
$updatedTask = Get-ScheduledTask -TaskName $taskName
Write-Host ""
Write-Host "Updated task info:" -ForegroundColor Cyan
Write-Host "  Owner: $($updatedTask.Principal.UserId)" -ForegroundColor Gray
Write-Host "  RunLevel: $($updatedTask.Principal.RunLevel)" -ForegroundColor Gray









