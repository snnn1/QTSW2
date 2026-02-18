# Check IEA (Instrument Execution Authority) events in robot logs
# Run from project root: .\scripts\check_iea_logs.ps1

param(
    [int]$LastLines = 100,
    [switch]$Live,
    [switch]$Summary
)

$logDir = "logs\robot"
$engineLog = Join-Path $logDir "robot_ENGINE.jsonl"

if (-not (Test-Path $engineLog)) {
    Write-Host "ENGINE log not found: $engineLog" -ForegroundColor Yellow
    Write-Host "Ensure strategy has run and logs/robot exists." -ForegroundColor Gray
    exit 1
}

if ($Summary) {
    Write-Host "`n=== IEA Event Summary (robot_ENGINE.jsonl) ===" -ForegroundColor Cyan
    $content = Get-Content $engineLog -ErrorAction SilentlyContinue
    $ieaEvents = $content | Where-Object { $_ -match "IEA_BINDING|IEA_HEARTBEAT|IEA_EXEC_UPDATE_ROUTED|IEA_ENQUEUE_AND_WAIT|IEA_QUEUE_WORKER|IEA_ENQUEUE_REJECTED|IEA_BYPASS" }
    $byType = @{}
    foreach ($line in $ieaEvents) {
        if ($line -match '"event"\s*:\s*"([^"]+)"') {
            $evt = $matches[1]
            if (-not $byType.ContainsKey($evt)) { $byType[$evt] = 0 }
            $byType[$evt]++
        }
    }
    $byType.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object {
        Write-Host ("  {0}: {1}" -f $_.Key, $_.Value)
    }
    Write-Host ""
    exit 0
}

if ($Live) {
    Write-Host "Tailing ENGINE log for IEA events (Ctrl+C to stop)..." -ForegroundColor Cyan
    Get-Content $engineLog -Wait -Tail 10 | Where-Object { $_ -match "IEA_|iea_instance_id|enqueue_sequence" }
    exit 0
}

Write-Host "`n=== Last $LastLines IEA-related lines from robot_ENGINE.jsonl ===" -ForegroundColor Cyan
Get-Content $engineLog -Tail ($LastLines * 5) -ErrorAction SilentlyContinue |
    Select-String -Pattern "IEA_|iea_instance_id|enqueue_sequence|last_processed" |
    Select-Object -Last $LastLines |
    ForEach-Object { $_.Line }

Write-Host "`n=== IEA events in per-instrument logs ===" -ForegroundColor Cyan
$instrumentLogs = Get-ChildItem (Join-Path $logDir "robot_*.jsonl") -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notmatch "ENGINE|archive" }
$found = $false
foreach ($log in $instrumentLogs) {
    $matches = Select-String -Path $log.FullName -Pattern "iea_instance_id" -ErrorAction SilentlyContinue | Select-Object -Last 3
    if ($matches) {
        $found = $true
        Write-Host "`n$($log.Name):" -ForegroundColor Gray
        $matches | ForEach-Object { Write-Host $_.Line.Substring(0, [Math]::Min(200, $_.Line.Length)) + "..." }
    }
}
if (-not $found) {
    Write-Host "  No iea_instance_id found in instrument logs (IEA context in CRITICAL events)" -ForegroundColor Gray
}

Write-Host "`nDone. Use -Summary for counts, -Live to tail." -ForegroundColor Gray
