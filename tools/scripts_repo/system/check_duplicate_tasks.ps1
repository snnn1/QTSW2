# Check for duplicate Pipeline Runner tasks
Write-Host "Checking for duplicate tasks..." -ForegroundColor Yellow

$tasks = Get-ScheduledTask -TaskName "*Pipeline*" -ErrorAction SilentlyContinue

if ($tasks) {
    Write-Host ""
    Write-Host "Found $($tasks.Count) task(s) matching Pipeline:" -ForegroundColor Cyan
    foreach ($task in $tasks) {
        Write-Host ""
        Write-Host "  Task Name: $($task.TaskName)" -ForegroundColor White
        Write-Host "    Path: $($task.TaskPath)" -ForegroundColor Gray
        Write-Host "    State: $($task.State)" -ForegroundColor Gray
        
        $taskInfo = Get-ScheduledTaskInfo -TaskName $task.TaskName
        Write-Host "    Last Run: $($taskInfo.LastRunTime)" -ForegroundColor Gray
        Write-Host "    Last Result: $($taskInfo.LastTaskResult)" -ForegroundColor Gray
        Write-Host "    Next Run: $($taskInfo.NextRunTime)" -ForegroundColor Gray
        
        $taskDef = Get-ScheduledTask -TaskName $task.TaskName
        Write-Host "    Triggers: $($taskDef.Triggers.Count)" -ForegroundColor Gray
        foreach ($trigger in $taskDef.Triggers) {
            Write-Host "      - $($trigger.CimClass.CimClassName): $($trigger.StartBoundary)" -ForegroundColor DarkGray
        }
    }
    
    if ($tasks.Count -gt 1) {
        Write-Host ""
        Write-Host "WARNING: Multiple tasks found!" -ForegroundColor Yellow
        Write-Host "  You should have only ONE Pipeline Runner task with 5 triggers." -ForegroundColor Yellow
        Write-Host ""
        Write-Host "To fix this:" -ForegroundColor Cyan
        Write-Host "  1. Delete all Pipeline Runner tasks" -ForegroundColor Gray
        Write-Host "  2. Run batch/SETUP_WINDOWS_SCHEDULER.bat as Administrator" -ForegroundColor Gray
    } else {
        Write-Host ""
        Write-Host "Only one task found - this is correct!" -ForegroundColor Green
    }
} else {
    Write-Host "No tasks found matching Pipeline" -ForegroundColor Yellow
}
