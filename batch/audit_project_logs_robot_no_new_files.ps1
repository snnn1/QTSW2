# After an isolated run: fails if <ProjectRoot>\logs\robot contains files not in the baseline (detects leakage into global tree).
# Usage:
#   .\batch\audit_project_logs_robot_no_new_files.ps1 -ProjectRoot "C:\path\to\QTSW2" -BaselinePath ".cache\logs_robot_baseline.json" -SaveBaseline
#   (run the robot / harness)
#   .\batch\audit_project_logs_robot_no_new_files.ps1 -ProjectRoot "C:\path\to\QTSW2" -BaselinePath ".cache\logs_robot_baseline.json"

param(
    [Parameter(Mandatory = $true)][string]$ProjectRoot,
    [Parameter(Mandatory = $true)][string]$BaselinePath,
    [switch]$SaveBaseline
)

$ErrorActionPreference = "Stop"
$logDir = Join-Path $ProjectRoot "logs\robot"

$files = @()
if (Test-Path -LiteralPath $logDir) {
    $files = @(Get-ChildItem -LiteralPath $logDir -File | Sort-Object Name | ForEach-Object {
        [ordered]@{ Name = $_.Name; Length = $_.Length }
    })
}

if ($SaveBaseline) {
    $dir = Split-Path -Parent $BaselinePath
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    @{ files = $files } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $BaselinePath -Encoding UTF8
    Write-Host "Baseline saved: $BaselinePath ($($files.Count) files)"
    exit 0
}

if (-not (Test-Path -LiteralPath $BaselinePath)) {
    Write-Error "Baseline not found: $BaselinePath — run once with -SaveBaseline"
    exit 2
}

$baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json
$baseNames = @($baseline.files | ForEach-Object { $_.Name })
$curNames = @($files | ForEach-Object { $_.Name })

$new = $curNames | Where-Object { $baseNames -notcontains $_ }
if ($new.Count -gt 0) {
    Write-Error ("FAIL: new file(s) under logs\robot (global leak): " + ($new -join ", "))
    exit 1
}

Write-Host "OK: no new files under logs\robot (baseline $($baseNames.Count) files, current $($curNames.Count) files)"
exit 0
