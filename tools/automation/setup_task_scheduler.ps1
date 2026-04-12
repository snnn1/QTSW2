# PowerShell script to set up Windows Task Scheduler for pipeline runner
# Run this script as Administrator

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  Pipeline Runner - Task Scheduler Setup" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan
Write-Host ""

# Configuration
$taskName = "Pipeline Runner"
$workingDir = "C:\Users\jakej\QTSW2"
# Use standalone runner that doesn't require backend
$arguments = "-m automation.run_pipeline_standalone"
$description = "Runs data pipeline at :00, :15, :30, :45 of every hour and at system startup"

# Try to find Python - first check if 'python' is in PATH, then try common locations
Write-Host "Detecting Python installation..." -ForegroundColor Yellow
$pythonPath = $null
try {
    $pythonCmd = Get-Command python -ErrorAction Stop
    $pythonPath = $pythonCmd.Source
    Write-Host "[OK] Found Python in PATH: $pythonPath" -ForegroundColor Green
} catch {
    # Try common Python installation locations
    $commonPaths = @(
        "$env:LOCALAPPDATA\Programs\Python\Python313\python.exe",
        "$env:LOCALAPPDATA\Programs\Python\Python312\python.exe",
        "$env:LOCALAPPDATA\Programs\Python\Python311\python.exe",
        "$env:PROGRAMFILES\Python313\python.exe",
        "$env:PROGRAMFILES\Python312\python.exe",
        "$env:PROGRAMFILES\Python311\python.exe",
        "C:\Python313\python.exe",
        "C:\Python312\python.exe",
        "C:\Python311\python.exe"
    )
    
    foreach ($path in $commonPaths) {
        if (Test-Path $path) {
            $pythonPath = $path
            Write-Host "[OK] Found Python at: $pythonPath" -ForegroundColor Green
            break
        }
    }
    
    if (-not $pythonPath) {
        Write-Host "[ERROR] Python not found. Please install Python or specify the full path." -ForegroundColor Red
        exit 1
    }
}

Write-Host ""
Write-Host "Task Name: $taskName" -ForegroundColor Yellow
Write-Host "Python: $pythonPath" -ForegroundColor Yellow
Write-Host "Working Directory: $workingDir" -ForegroundColor Yellow
Write-Host "Arguments: $arguments" -ForegroundColor Yellow
Write-Host ""

# Check if running as administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[ERROR] This script must be run as Administrator!" -ForegroundColor Red
    Write-Host ""
    Write-Host "To fix:" -ForegroundColor Yellow
    Write-Host "  1. Right-click batch\SETUP_WINDOWS_SCHEDULER.bat" -ForegroundColor Gray
    Write-Host "  2. Select 'Run as administrator'" -ForegroundColor Gray
    Write-Host "  3. Click Yes when prompted" -ForegroundColor Gray
    Write-Host ""
    exit 1
}

# Check if task already exists
$existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue

if ($existingTask) {
    Write-Host "Task '$taskName' already exists." -ForegroundColor Yellow
    Write-Host "Removing existing task to recreate with correct permissions..." -ForegroundColor Yellow
    try {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
        Write-Host "[OK] Old task removed" -ForegroundColor Green
        Start-Sleep -Seconds 1
    } catch {
        Write-Host "[ERROR] Could not remove existing task: $_" -ForegroundColor Red
        Write-Host "You may need to manually delete it from Task Scheduler first." -ForegroundColor Yellow
        exit 1
    }
}

# Verify Python is available and can run
Write-Host "Verifying Python installation..." -ForegroundColor Yellow
try {
    $pythonVersion = & $pythonPath --version 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host "[OK] Python verified: $pythonVersion" -ForegroundColor Green
    } else {
        Write-Host "[ERROR] Python at $pythonPath returned error code: $LASTEXITCODE" -ForegroundColor Red
        exit 1
    }
} catch {
    Write-Host "[ERROR] Failed to run Python at: $pythonPath" -ForegroundColor Red
    Write-Host "Error: $_" -ForegroundColor Red
    exit 1
}

# Verify working directory exists
if (-not (Test-Path $workingDir)) {
    Write-Host "✗ Working directory does not exist: $workingDir" -ForegroundColor Red
    exit 1
}
Write-Host "[OK] Working directory exists: $workingDir" -ForegroundColor Green

# Create action
Write-Host "Creating task action..." -ForegroundColor Yellow
$action = New-ScheduledTaskAction `
    -Execute $pythonPath `
    -Argument $arguments `
    -WorkingDirectory $workingDir

# Create 4 triggers - one for each 15-minute mark (:00, :15, :30, :45)
# Each trigger uses a "Once" trigger that repeats every hour
Write-Host "Creating triggers (at :00, :15, :30, :45 every hour)..." -ForegroundColor Yellow

# Get today's date and time
$now = Get-Date
$today = $now.Date

# Create 4 triggers, one for each 15-minute mark
# Use daily triggers with repetition to ensure they run at :00, :15, :30, :45 every hour
$triggers = @()

# Get current time and calculate next occurrence for each trigger
$now = Get-Date
$today = $now.Date

# Trigger 1: :00 (runs at :00 of every hour)
# Calculate next :00 (could be this hour or next hour)
$next00 = $today.AddHours($now.Hour).AddMinutes(0).AddSeconds(0)
if ($next00 -le $now) {
    $next00 = $next00.AddHours(1)
}
$trigger00 = New-ScheduledTaskTrigger -Once -At $next00 -RepetitionInterval (New-TimeSpan -Hours 1) -RepetitionDuration (New-TimeSpan -Days 365)
$triggers += $trigger00

# Trigger 2: :15 (runs at :15 of every hour)
$next15 = $today.AddHours($now.Hour).AddMinutes(15).AddSeconds(0)
if ($next15 -le $now) {
    $next15 = $next15.AddHours(1)
}
$trigger15 = New-ScheduledTaskTrigger -Once -At $next15 -RepetitionInterval (New-TimeSpan -Hours 1) -RepetitionDuration (New-TimeSpan -Days 365)
$triggers += $trigger15

# Trigger 3: :30 (runs at :30 of every hour)
$next30 = $today.AddHours($now.Hour).AddMinutes(30).AddSeconds(0)
if ($next30 -le $now) {
    $next30 = $next30.AddHours(1)
}
$trigger30 = New-ScheduledTaskTrigger -Once -At $next30 -RepetitionInterval (New-TimeSpan -Hours 1) -RepetitionDuration (New-TimeSpan -Days 365)
$triggers += $trigger30

# Trigger 4: :45 (runs at :45 of every hour)
$next45 = $today.AddHours($now.Hour).AddMinutes(45).AddSeconds(0)
if ($next45 -le $now) {
    $next45 = $next45.AddHours(1)
}
$trigger45 = New-ScheduledTaskTrigger -Once -At $next45 -RepetitionInterval (New-TimeSpan -Hours 1) -RepetitionDuration (New-TimeSpan -Days 365)
$triggers += $trigger45

# Trigger 5: At system startup (runs when Windows starts)
$triggerStartup = New-ScheduledTaskTrigger -AtStartup
$triggers += $triggerStartup

Write-Host "  Created 5 triggers:" -ForegroundColor Green
Write-Host "    - :00 of every hour (starts: $($next00.ToString('yyyy-MM-dd HH:mm:ss')))" -ForegroundColor Gray
Write-Host "    - :15 of every hour (starts: $($next15.ToString('yyyy-MM-dd HH:mm:ss')))" -ForegroundColor Gray
Write-Host "    - :30 of every hour (starts: $($next30.ToString('yyyy-MM-dd HH:mm:ss')))" -ForegroundColor Gray
Write-Host "    - :45 of every hour (starts: $($next45.ToString('yyyy-MM-dd HH:mm:ss')))" -ForegroundColor Gray
Write-Host "    - At system startup" -ForegroundColor Gray

# Create settings
# CRITICAL: Configure to prevent Windows from auto-disabling the task after failures
# NOTE: Application has its own retry logic (max_retries=3 per stage). This RestartCount
# is for process-level failures (crashes, exit codes). Setting to 100 prevents auto-disable
# while still allowing the application's intelligent retry logic to handle transient errors.
Write-Host "Creating task settings (configured to prevent auto-disable)..." -ForegroundColor Yellow
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 100 `  # High enough to prevent auto-disable, but not infinite (app has own retries)
    -RestartInterval (New-TimeSpan -Minutes 5) `
    -ExecutionTimeLimit (New-TimeSpan -Hours 2) `
    -MultipleInstances IgnoreNew `
    -DeleteExpiredTaskAfter (New-TimeSpan -Seconds 0) `  # Never delete expired tasks (PT0S = never)
    -StopTaskIfRunsLongerThan (New-TimeSpan -Hours 2)

# Create principal (run as current user - no highest privileges needed for enable/disable)
Write-Host "Creating task principal..." -ForegroundColor Yellow
$principal = New-ScheduledTaskPrincipal `
    -UserId "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType S4U `
    -RunLevel Limited

# Delete existing task if it exists (to ensure clean recreation)
Write-Host "Checking for existing task..." -ForegroundColor Yellow
$existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existingTask) {
    Write-Host "  Found existing task, deleting it first..." -ForegroundColor Yellow
    try {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
        Write-Host "  [OK] Old task deleted" -ForegroundColor Green
        Start-Sleep -Seconds 1
    } catch {
        Write-Host "  [WARNING] Could not delete existing task: $_" -ForegroundColor Yellow
        Write-Host "  Will try to overwrite with -Force" -ForegroundColor Yellow
    }
}

# Register the task with multiple triggers
Write-Host "Registering scheduled task..." -ForegroundColor Yellow
try {
    Register-ScheduledTask `
        -TaskName $taskName `
        -Action $action `
        -Trigger $triggers `
        -Settings $settings `
        -Principal $principal `
        -Description $description `
        -Force `
        -ErrorAction Stop
    
    Write-Host "[OK] Task '$taskName' created successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "Task Details:" -ForegroundColor Cyan
    Write-Host "  Name: $taskName" -ForegroundColor Gray
    Write-Host "  Schedule: At :00, :15, :30, :45 of every hour + At system startup" -ForegroundColor Gray
    Write-Host '  Triggers: 5 (4 time-based + 1 startup)' -ForegroundColor Gray
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
    Write-Host "[ERROR] Failed to create task: $_" -ForegroundColor Red
    Write-Host ""
    Write-Host "This script MUST be run as Administrator to create/modify scheduled tasks." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "To fix:" -ForegroundColor Cyan
    Write-Host "  1. Right-click batch\SETUP_WINDOWS_SCHEDULER.bat" -ForegroundColor Gray
    Write-Host "  2. Select 'Run as administrator'" -ForegroundColor Gray
    Write-Host "  3. Click Yes when prompted" -ForegroundColor Gray
    Write-Host ""
    exit 1
}

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  Setup Complete" -ForegroundColor Green
Write-Host "================================================" -ForegroundColor Cyan

