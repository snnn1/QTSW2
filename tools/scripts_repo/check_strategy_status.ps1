# Check NinjaTrader Strategy Status from Logs
# Shows recent key events to verify strategy is working correctly

$ErrorActionPreference = "Continue"

$logFile = Join-Path $PSScriptRoot "..\logs\robot\robot_ENGINE.jsonl"

if (-not (Test-Path $logFile)) {
    Write-Host "❌ Log file not found: $logFile" -ForegroundColor Red
    exit 1
}

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  Strategy Status Check" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan
Write-Host ""

# Get last 50 lines and parse JSON
$recentLogs = Get-Content $logFile -Tail 50 | ForEach-Object {
    try {
        $_ | ConvertFrom-Json
    } catch {
        $null
    }
} | Where-Object { $null -ne $_ }

# Filter for key events
$keyEvents = $recentLogs | Where-Object {
    $_.event -in @("ENGINE_START", "EXECUTION_MODE_SET", "SIM_ACCOUNT_VERIFIED", "REALTIME_STATE_REACHED", 
                   "ORDER_SUBMIT_ATTEMPT", "ORDER_SUBMIT_SUCCESS", "EXECUTION_FILLED", "BAR_RECEIVED_NO_STREAMS",
                   "DATA_LOSS_DETECTED")
}

Write-Host "📊 Recent Key Events (Last 50 log entries):" -ForegroundColor Yellow
Write-Host ""

$grouped = $keyEvents | Group-Object event | Sort-Object Count -Descending

foreach ($group in $grouped) {
    $count = $group.Count
    $eventName = $group.Name
    $latest = ($group.Group | Sort-Object -Property @{Expression={[DateTimeOffset]::Parse($_.ts_utc)}} -Descending | Select-Object -First 1)
    
    $color = switch ($eventName) {
        "ENGINE_START" { "Green" }
        "EXECUTION_MODE_SET" { "Green" }
        "SIM_ACCOUNT_VERIFIED" { "Green" }
        "REALTIME_STATE_REACHED" { "Green" }
        "ORDER_SUBMIT_SUCCESS" { "Cyan" }
        "EXECUTION_FILLED" { "Cyan" }
        "BAR_RECEIVED_NO_STREAMS" { "Yellow" }
        "DATA_LOSS_DETECTED" { "Red" }
        default { "Gray" }
    }
    
    $timeStr = if ($latest.ts_utc) {
        try {
            $dt = [DateTimeOffset]::Parse($latest.ts_utc)
            $dt.LocalDateTime.ToString("HH:mm:ss")
        } catch {
            "N/A"
        }
    } else {
        "N/A"
    }
    
    Write-Host "  [$timeStr] $eventName" -ForegroundColor $color -NoNewline
    Write-Host " (x$count)" -ForegroundColor Gray
    
    # Show details for important events
    if ($eventName -eq "EXECUTION_MODE_SET" -and $latest.data.payload) {
        Write-Host "      → $($latest.data.payload)" -ForegroundColor DarkGray
    }
    if ($eventName -eq "SIM_ACCOUNT_VERIFIED" -and $latest.data.account_name) {
        Write-Host "      → Account: $($latest.data.account_name)" -ForegroundColor DarkGray
    }
    if ($eventName -eq "REALTIME_STATE_REACHED" -and $latest.data.instrument) {
        Write-Host "      → Instrument: $($latest.data.instrument)" -ForegroundColor DarkGray
    }
}

Write-Host ""

# Check for errors
$errors = $recentLogs | Where-Object { $_.level -eq "ERROR" } | Select-Object -First 5
if ($errors) {
    Write-Host "⚠️  Recent Errors:" -ForegroundColor Yellow
    foreach ($err in $errors) {
        $timeStr = try {
            [DateTimeOffset]::Parse($err.ts_utc).LocalDateTime.ToString("HH:mm:ss")
        } catch { "N/A" }
        Write-Host "  [$timeStr] $($err.event): $($err.message)" -ForegroundColor Red
        if ($err.data.note) {
            Write-Host "      → $($err.data.note)" -ForegroundColor DarkGray
        }
    }
    Write-Host ""
}

# Summary
Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  Status Summary" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan

$hasStart = ($keyEvents | Where-Object { $_.event -eq "ENGINE_START" }).Count -gt 0
$hasModeSet = ($keyEvents | Where-Object { $_.event -eq "EXECUTION_MODE_SET" }).Count -gt 0
$hasSimVerified = ($keyEvents | Where-Object { $_.event -eq "SIM_ACCOUNT_VERIFIED" }).Count -gt 0
$hasRealtime = ($keyEvents | Where-Object { $_.event -eq "REALTIME_STATE_REACHED" }).Count -gt 0

Write-Host ""
Write-Host "✅ Engine Started:        " -NoNewline
Write-Host $(if ($hasStart) { "YES" } else { "NO" }) -ForegroundColor $(if ($hasStart) { "Green" } else { "Red" })

Write-Host "✅ Execution Mode Set:    " -NoNewline
Write-Host $(if ($hasModeSet) { "YES" } else { "NO" }) -ForegroundColor $(if ($hasModeSet) { "Green" } else { "Red" })

Write-Host "✅ SIM Account Verified:  " -NoNewline
Write-Host $(if ($hasSimVerified) { "YES" } else { "NO" }) -ForegroundColor $(if ($hasSimVerified) { "Green" } else { "Red" })

Write-Host "✅ Realtime State:        " -NoNewline
Write-Host $(if ($hasRealtime) { "YES" } else { "NO" }) -ForegroundColor $(if ($hasRealtime) { "Green" } else { "Red" })

Write-Host ""

if ($hasStart -and $hasModeSet -and $hasSimVerified -and $hasRealtime) {
    Write-Host "🎉 Strategy is running correctly!" -ForegroundColor Green
    Write-Host ""
    Write-Host "The strategy has:" -ForegroundColor Gray
    Write-Host "  • Started successfully" -ForegroundColor Gray
    Write-Host "  • Set execution mode to SIM" -ForegroundColor Gray
    Write-Host "  • Verified SIM account" -ForegroundColor Gray
    Write-Host "  • Reached realtime state" -ForegroundColor Gray
} else {
    Write-Host "WARNING: Some initialization steps may be missing." -ForegroundColor Yellow
    Write-Host "   Check the logs above for details." -ForegroundColor Gray
}
Write-Host ""

Write-Host ""
