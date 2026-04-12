# Setup Eligibility Builder - runs once daily at 18:00 (6 PM) local time
# Run as Administrator to create/update the task
#
# The eligibility builder should run ONCE per day at 18:00 CT, not every hour.

$ErrorActionPreference = "Stop"
$taskName = "QTSW2_EligibilityBuilder_18CT"
$projectRoot = (Split-Path (Split-Path $MyInvocation.MyCommand.Path) -Parent)

# Require admin
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[ERROR] Run as Administrator. Right-click -> Run as administrator" -ForegroundColor Red
    exit 1
}

# Find Python
$pythonPath = $null
try {
    $pythonPath = (Get-Command python -ErrorAction Stop).Source
} catch {
    $pythonPath = "$env:LOCALAPPDATA\Programs\Python\Python313\python.exe"
    if (-not (Test-Path $pythonPath)) {
        $pythonPath = "$env:LOCALAPPDATA\Programs\Python\Python312\python.exe"
    }
}
if (-not $pythonPath -or -not (Test-Path $pythonPath)) {
    Write-Host "[ERROR] Python not found" -ForegroundColor Red
    exit 1
}

$scriptPath = "$projectRoot\scripts\eligibility_builder.py"
if (-not (Test-Path $scriptPath)) {
    Write-Host "[ERROR] Script not found: $scriptPath" -ForegroundColor Red
    exit 1
}

# Delete existing task (it may be configured to run hourly)
$existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Removing existing task (was running hourly)..." -ForegroundColor Yellow
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    Start-Sleep -Seconds 1
}

# Create DAILY trigger at 18:00 (6 PM) - runs ONCE per day, no repetition
$action = New-ScheduledTaskAction -Execute $pythonPath -Argument "`"$scriptPath`"" -WorkingDirectory $projectRoot
$trigger = New-ScheduledTaskTrigger -Daily -At "18:00"
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType S4U -RunLevel Limited

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal `
    -Description "Runs eligibility builder once daily at 18:00 (6 PM) - 18:00 CT freeze" -Force

Write-Host "[OK] Task created: runs once daily at 18:00 (6 PM)" -ForegroundColor Green
Write-Host "  Next run: tomorrow at 18:00" -ForegroundColor Gray
