# Fix Windows Task Scheduler - Update script path
$taskName = "Pipeline Runner"
$correctScript = "automation\run_pipeline_standalone.py"

Write-Host "Fixing task: $taskName" -ForegroundColor Yellow

try {
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
    $currentAction = $task.Actions[0]
    
    Write-Host "Current Arguments: $($currentAction.Arguments)" -ForegroundColor Gray
    
    if ($currentAction.Arguments -eq $correctScript) {
        Write-Host "Task is already correct!" -ForegroundColor Green
        exit 0
    }
    
    $newAction = New-ScheduledTaskAction `
        -Execute $currentAction.Execute `
        -Argument $correctScript `
        -WorkingDirectory $currentAction.WorkingDirectory
    
    Set-ScheduledTask -TaskName $taskName -Action $newAction -ErrorAction Stop
    
    Write-Host "Task updated successfully!" -ForegroundColor Green
    Write-Host "New Arguments: $correctScript" -ForegroundColor Green
    
    # Verify
    $updatedTask = Get-ScheduledTask -TaskName $taskName
    $updatedAction = $updatedTask.Actions[0]
    Write-Host ""
    Write-Host "Verification:" -ForegroundColor Cyan
    Write-Host "  Execute: $($updatedAction.Execute)" -ForegroundColor Gray
    Write-Host "  Arguments: $($updatedAction.Arguments)" -ForegroundColor Gray
    Write-Host "  Working Directory: $($updatedAction.WorkingDirectory)" -ForegroundColor Gray
    
} catch {
    Write-Host "Error: $_" -ForegroundColor Red
    Write-Host ""
    Write-Host "This may require Administrator privileges." -ForegroundColor Yellow
    Write-Host "Try running: tools\FIX_SCHEDULER_TASK.bat as Administrator" -ForegroundColor Yellow
    exit 1
}



