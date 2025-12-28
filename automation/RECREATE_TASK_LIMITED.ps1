# Recreate Task with Limited Privileges
# This script deletes the existing task and recreates it with Limited privileges
# Run this as Administrator

$ErrorActionPreference = "Stop"

$taskName = "Pipeline Runner"
$projectRoot = $PSScriptRoot | Split-Path -Parent

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Recreate Pipeline Runner Task (Limited Privileges)" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# Check if running as admin
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[ERROR] This script must be run as Administrator!" -ForegroundColor Red
    Write-Host "Right-click and select 'Run as administrator'" -ForegroundColor Yellow
    exit 1
}

# Find Python executable
Write-Host "Finding Python executable..." -ForegroundColor Yellow
$pythonPath = $null
$possiblePaths = @(
    "$env:LOCALAPPDATA\Programs\Python\Python*\python.exe",
    "$env:PROGRAMFILES\Python*\python.exe",
    "C:\Python*\python.exe"
)

foreach ($pattern in $possiblePaths) {
    $found = Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) {
        $pythonPath = $found.FullName
        break
    }
}

if (-not $pythonPath) {
    # Try python in PATH
    try {
        $pythonPath = (Get-Command python -ErrorAction Stop).Source
    } catch {
        Write-Host "[ERROR] Python not found! Please install Python or add it to PATH." -ForegroundColor Red
        exit 1
    }
}

Write-Host "[OK] Found Python: $pythonPath" -ForegroundColor Green

# Set up paths
$arguments = "automation\run_pipeline_standalone.py"
$workingDir = $projectRoot

# Delete existing task if it exists
Write-Host ""
Write-Host "Checking for existing task..." -ForegroundColor Yellow
$existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existingTask) {
    Write-Host "Found existing task. Deleting..." -ForegroundColor Yellow
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
    Write-Host "[OK] Existing task deleted" -ForegroundColor Green
    Start-Sleep -Seconds 1
} else {
    Write-Host "[OK] No existing task found" -ForegroundColor Green
}

# Create action
Write-Host ""
Write-Host "Creating task action..." -ForegroundColor Yellow
$action = New-ScheduledTaskAction `
    -Execute $pythonPath `
    -Argument $arguments `
    -WorkingDirectory $workingDir

# Create 4 triggers - one for each 15-minute mark (:00, :15, :30, :45)
Write-Host "Creating triggers (at :00, :15, :30, :45 every hour)..." -ForegroundColor Yellow

$now = Get-Date
$today = $now.Date
$triggers = @()

# Trigger 1: :00
$trigger00Time = $today.AddHours($now.Hour).AddMinutes(0).AddSeconds(0)
if ($trigger00Time -le $now) {
    $trigger00Time = $trigger00Time.AddHours(1)
}
$trigger00 = New-ScheduledTaskTrigger `
    -Once `
    -At $trigger00Time `
    -RepetitionInterval (New-TimeSpan -Hours 1) `
    -RepetitionDuration (New-TimeSpan -Days 365)
$triggers += $trigger00

# Trigger 2: :15
$trigger15Time = $today.AddHours($now.Hour).AddMinutes(15).AddSeconds(0)
if ($trigger15Time -le $now) {
    $trigger15Time = $trigger15Time.AddHours(1)
}
$trigger15 = New-ScheduledTaskTrigger `
    -Once `
    -At $trigger15Time `
    -RepetitionInterval (New-TimeSpan -Hours 1) `
    -RepetitionDuration (New-TimeSpan -Days 365)
$triggers += $trigger15

# Trigger 3: :30
$trigger30Time = $today.AddHours($now.Hour).AddMinutes(30).AddSeconds(0)
if ($trigger30Time -le $now) {
    $trigger30Time = $trigger30Time.AddHours(1)
}
$trigger30 = New-ScheduledTaskTrigger `
    -Once `
    -At $trigger30Time `
    -RepetitionInterval (New-TimeSpan -Hours 1) `
    -RepetitionDuration (New-TimeSpan -Days 365)
$triggers += $trigger30

# Trigger 4: :45
$trigger45Time = $today.AddHours($now.Hour).AddMinutes(45).AddSeconds(0)
if ($trigger45Time -le $now) {
    $trigger45Time = $trigger45Time.AddHours(1)
}
$trigger45 = New-ScheduledTaskTrigger `
    -Once `
    -At $trigger45Time `
    -RepetitionInterval (New-TimeSpan -Hours 1) `
    -RepetitionDuration (New-TimeSpan -Days 365)
$triggers += $trigger45

# Trigger 5: At startup
$startupTrigger = New-ScheduledTaskTrigger -AtStartup
$triggers += $startupTrigger

# Create principal with LIMITED privileges (this is the key!)
Write-Host "Creating task principal (Limited privileges)..." -ForegroundColor Yellow
$principal = New-ScheduledTaskPrincipal `
    -UserId "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType S4U `
    -RunLevel Limited  # <-- This is critical!

# Create settings
Write-Host "Creating task settings..." -ForegroundColor Yellow
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RunOnlyIfNetworkAvailable:$false `
    -ExecutionTimeLimit (New-TimeSpan -Hours 2)

# Register the task
Write-Host ""
Write-Host "Registering scheduled task..." -ForegroundColor Yellow
try {
    Register-ScheduledTask `
        -TaskName $taskName `
        -Action $action `
        -Trigger $triggers `
        -Principal $principal `
        -Settings $settings `
        -Description "Pipeline Runner - Automated data processing pipeline" `
        -ErrorAction Stop
    
    Write-Host "[OK] Task '$taskName' created successfully!" -ForegroundColor Green
} catch {
    Write-Host "[ERROR] Failed to create task: $_" -ForegroundColor Red
    exit 1
}

# Verify
Write-Host ""
Write-Host "Verifying task..." -ForegroundColor Yellow
$createdTask = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
Write-Host "[OK] Task verified!" -ForegroundColor Green
Write-Host ""
Write-Host "Task Details:" -ForegroundColor Cyan
Write-Host "  Name: $($createdTask.TaskName)" -ForegroundColor Gray
Write-Host "  RunLevel: $($createdTask.Principal.RunLevel)" -ForegroundColor Gray
Write-Host "  UserId: $($createdTask.Principal.UserId)" -ForegroundColor Gray
Write-Host "  Triggers: 5 (4 time-based + 1 startup)" -ForegroundColor Gray
Write-Host "  Command: $pythonPath $arguments" -ForegroundColor Gray
Write-Host "  Working Directory: $workingDir" -ForegroundColor Gray

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Task Recreated Successfully!" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "The dashboard button should now work without admin rights!" -ForegroundColor Yellow
Write-Host ""



























