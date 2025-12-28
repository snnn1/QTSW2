# PowerShell script to create a Windows Task Scheduler task that starts the backend automatically
# This ensures the backend is always running for scheduled pipeline runs

param(
    [switch]$Remove
)

$ErrorActionPreference = "Stop"

# Get project root
$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptPath

$taskName = "Pipeline Backend Auto-Start"
$taskDescription = "Automatically starts the Pipeline Dashboard backend on system startup to enable scheduled pipeline runs"

# Determine Python executable path
$pythonPath = ""
$commonPaths = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Python\Python313\python.exe"),
    (Join-Path $env:LOCALAPPDATA "Programs\Python\Python312\python.exe"),
    (Join-Path $env:LOCALAPPDATA "Programs\Python\Python311\python.exe"),
    (Join-Path $env:LOCALAPPDATA "Programs\Python\Python310\python.exe"),
    (Join-Path $env:APPDATA "Python\Python313\python.exe"),
    (Join-Path $env:APPDATA "Python\Python312\python.exe"),
    (Join-Path $env:APPDATA "Python\Python311\python.exe"),
    (Join-Path $env:APPDATA "Python\Python310\python.exe"),
    "C:\Python313\python.exe",
    "C:\Python312\python.exe",
    "C:\Python311\python.exe",
    "C:\Python310\python.exe",
    "python.exe" # Fallback to PATH
)

Write-Host "Attempting to find Python executable..." -ForegroundColor Yellow
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

# Backend startup script
$backendScript = Join-Path $projectRoot "batch\START_ORCHESTRATOR.bat"

if (-not (Test-Path $backendScript)) {
    Write-Host "[ERROR] Backend startup script not found: $backendScript" -ForegroundColor Red
    exit 1
}

if ($Remove) {
    Write-Host "Removing task: $taskName" -ForegroundColor Yellow
    try {
        schtasks /delete /tn $taskName /f 2>&1 | Out-Null
        Write-Host "[OK] Task removed successfully" -ForegroundColor Green
    } catch {
        Write-Host "[INFO] Task does not exist or already removed" -ForegroundColor Gray
    }
    exit 0
}

# Check if task already exists
$existingTask = schtasks /query /tn $taskName /fo LIST 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "[INFO] Task already exists. Removing old task..." -ForegroundColor Yellow
    schtasks /delete /tn $taskName /f 2>&1 | Out-Null
    Start-Sleep -Seconds 1
}

Write-Host "Creating Windows Task Scheduler task..." -ForegroundColor Yellow
Write-Host "  Task Name: $taskName" -ForegroundColor Gray
Write-Host "  Description: $taskDescription" -ForegroundColor Gray
Write-Host "  Trigger: At system startup" -ForegroundColor Gray
Write-Host "  Action: $backendScript" -ForegroundColor Gray

# Create the task
$action = New-ScheduledTaskAction -Execute $backendScript -WorkingDirectory $projectRoot
$trigger = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -RunOnlyIfNetworkAvailable:$false
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest

try {
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Description $taskDescription -Force | Out-Null
    Write-Host "[OK] Task created successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "The backend will now start automatically when Windows starts." -ForegroundColor Cyan
    Write-Host "This ensures scheduled pipeline runs will always work." -ForegroundColor Cyan
    Write-Host ""
    Write-Host "To remove this auto-start task, run:" -ForegroundColor Yellow
    Write-Host "  powershell -ExecutionPolicy Bypass -File automation\setup_backend_autostart.ps1 -Remove" -ForegroundColor Gray
} catch {
    Write-Host "[ERROR] Failed to create task: $_" -ForegroundColor Red
    Write-Host "You may need to run this script as Administrator." -ForegroundColor Yellow
    exit 1
}




























