# PowerShell script to set up Windows Task Scheduler for pipeline runner
# Run this script as Administrator

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  Pipeline Runner - Task Scheduler Setup" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan
Write-Host ""

# Configuration
$taskName = "Pipeline Runner"
$pythonPath = "python"  # Use 'python' from PATH, or specify full path like "C:\Users\jakej\AppData\Local\Programs\Python\Python313\python.exe"
$workingDir = "C:\Users\jakej\QTSW2"
$arguments = "-m automation.pipeline_runner"
$description = "Runs data pipeline every 15 minutes at :00, :15, :30, :45"

Write-Host "Task Name: $taskName" -ForegroundColor Yellow
Write-Host "Python: $pythonPath" -ForegroundColor Yellow
Write-Host "Working Directory: $workingDir" -ForegroundColor Yellow
Write-Host "Arguments: $arguments" -ForegroundColor Yellow
Write-Host ""

# Check if task already exists
$existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue

if ($existingTask) {
    Write-Host "Task '$taskName' already exists." -ForegroundColor Yellow
    $response = Read-Host "Do you want to remove and recreate it? (Y/N)"
    if ($response -eq "Y" -or $response -eq "y") {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        Write-Host "Existing task removed." -ForegroundColor Green
    } else {
        Write-Host "Exiting. Task not modified." -ForegroundColor Yellow
        exit
    }
}

# Verify Python is available
Write-Host "Verifying Python installation..." -ForegroundColor Yellow
try {
    $pythonVersion = & $pythonPath --version 2>&1
    Write-Host "✓ Python found: $pythonVersion" -ForegroundColor Green
} catch {
    Write-Host "✗ Python not found at: $pythonPath" -ForegroundColor Red
    Write-Host "Please specify the full path to python.exe" -ForegroundColor Yellow
    exit 1
}

# Verify working directory exists
if (-not (Test-Path $workingDir)) {
    Write-Host "✗ Working directory does not exist: $workingDir" -ForegroundColor Red
    exit 1
}
Write-Host "✓ Working directory exists: $workingDir" -ForegroundColor Green

# Create action
Write-Host "Creating task action..." -ForegroundColor Yellow
$action = New-ScheduledTaskAction `
    -Execute $pythonPath `
    -Argument $arguments `
    -WorkingDirectory $workingDir

# Create trigger (runs every 15 minutes starting from now)
Write-Host "Creating trigger (every 15 minutes)..." -ForegroundColor Yellow
$startTime = Get-Date
# Round down to nearest 15-minute mark
$minutes = $startTime.Minute
$roundedMinutes = [math]::Floor($minutes / 15) * 15
$startTime = $startTime.AddMinutes($roundedMinutes - $minutes).AddSeconds(-$startTime.Second).AddMilliseconds(-$startTime.Millisecond)

$trigger = New-ScheduledTaskTrigger `
    -Once `
    -At $startTime `
    -RepetitionInterval (New-TimeSpan -Minutes 15) `
    -RepetitionDuration (New-TimeSpan -Days 365)

# Create settings
Write-Host "Creating task settings..." -ForegroundColor Yellow
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 5) `
    -ExecutionTimeLimit (New-TimeSpan -Hours 2)

# Create principal (run as current user with highest privileges)
Write-Host "Creating task principal..." -ForegroundColor Yellow
$principal = New-ScheduledTaskPrincipal `
    -UserId "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType S4U `
    -RunLevel Highest

# Register the task
Write-Host "Registering scheduled task..." -ForegroundColor Yellow
try {
    Register-ScheduledTask `
        -TaskName $taskName `
        -Action $action `
        -Trigger $trigger `
        -Settings $settings `
        -Principal $principal `
        -Description $description `
        -Force
    
    Write-Host "✓ Task '$taskName' created successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "Task Details:" -ForegroundColor Cyan
    Write-Host "  Name: $taskName" -ForegroundColor Gray
    Write-Host "  Schedule: Every 15 minutes starting at $($startTime.ToString('HH:mm:ss'))" -ForegroundColor Gray
    Write-Host "  Command: $pythonPath $arguments" -ForegroundColor Gray
    Write-Host "  Working Directory: $workingDir" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Next Steps:" -ForegroundColor Cyan
    Write-Host "  1. Open Task Scheduler to verify the task" -ForegroundColor Gray
    Write-Host "  2. Right-click the task and select 'Run' to test" -ForegroundColor Gray
    Write-Host "  3. Check 'Last Run Result' - should be 0x0 (success)" -ForegroundColor Gray
    Write-Host "  4. Monitor via dashboard or log files" -ForegroundColor Gray
    Write-Host ""
    
} catch {
    Write-Host "✗ Failed to create task: $_" -ForegroundColor Red
    Write-Host "Make sure you are running PowerShell as Administrator" -ForegroundColor Yellow
    exit 1
}

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  Setup Complete" -ForegroundColor Green
Write-Host "================================================" -ForegroundColor Cyan

